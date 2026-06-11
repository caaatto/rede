using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Rede.Core.Crypto;
using Rede.Core.Networking;
using Rede.Core.Services;
using Rede.Core.Storage;
using Rede.Desktop.ViewModels;
using Rede.Desktop.Views;

namespace Rede.Desktop;

public partial class MainWindow : Window
{
    private readonly LoginViewModel _loginVm = new();
    private readonly MainViewModel _mainVm = new();
    private readonly ProfileStore _store = new();
    // FIDO2 hardware-key unlock. Windows uses the built-in WebAuthn API (always available; the OS
    // dialog drives PIN/touch); other platforms use libfido2 (system package or on-demand install).
    // The unlock service owns the session Profile Master Secret, cleared on logout/close.
    private readonly Rede.Core.Crypto.Fido2.IFido2Authenticator _fidoAuth;
    private readonly Rede.Core.Crypto.Fido2.Fido2UnlockService _fido;
    // Native window handle, cached on the UI thread (the Windows WebAuthn API needs an HWND and
    // must not pull it from a background thread). Updated once the window handle exists.
    private IntPtr _windowHandle;

    private RedeConnection? _conn;
    private AuthService? _auth;
    private ChatService? _chat;
    private ContactService? _contacts;
    private GroupService? _groups;
    private PlaceService? _places;
    private BlobService? _blobs;
    private DeviceService? _devices;

    // Persistent handler reference so InitServices can unsubscribe before re-binding.
    // Without this, every re-login would stack another handler on the long-lived
    // ProfileStore and duplicate save-error toasts.
    private Action<string>? _saveErrorHandler;
    private CallService? _call;
    private GroupCallService? _groupCall;
    private Views.GroupCallWindow? _groupCallWindow;
    private readonly CallViewModel _callVm = new();
    private readonly Rede.Core.Services.NotificationService _notifications = new();

    // H10: Thread-safe pending devices (accessed from connection handler threads)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string PublicKey, string SigningKey)> _pendingDevices = new();
    private UpdateService.ReleaseInfo? _pendingRelease;
    private string _lastServerUrl = "";
    private string _lastTransport = "";
    private bool _isNewUser;

    // Idle auto-away: switch to "away" after IdleAwayThreshold of no input/mouse,
    // restore the prior status on the next activity. Status is sent over the wire
    // but NOT persisted to Profile so the user's chosen status returns naturally.
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private string? _statusBeforeAutoAway;
    private DispatcherTimer? _idleTimer;
    private static readonly TimeSpan IdleAwayThreshold = TimeSpan.FromMinutes(5);

    public MainWindow()
    {
        _fidoAuth = OperatingSystem.IsWindows()
            ? new Rede.Core.Crypto.Fido2.WindowsWebAuthnAuthenticator(() => _windowHandle)
            : new Rede.Core.Crypto.Fido2.LibFido2Authenticator();
        _fido = new Rede.Core.Crypto.Fido2.Fido2UnlockService(_fidoAuth, _store);
        InitializeComponent();
        AdjustStartupSize();
        Loaded += (_, _) =>
        {
            _windowHandle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            ShowLogin();
            CheckForUpdatesAsync();

            // --minimized (e.g. from OS autostart): hide to tray after the login view
            // is fully initialized so the user can surface it via the tray icon.
            if (Program.StartMinimized)
            {
                Dispatcher.UIThread.Post(() => Hide(), Avalonia.Threading.DispatcherPriority.Background);
            }
        };
        Closing += OnWindowClosing;

        // Track user input/mouse activity for idle auto-away.
        // RoutingStrategies.Tunnel | Bubble + handledEventsToo so we still see
        // events that child controls have handled (typing in TextBox, scrolling, etc.).
        AddHandler(InputElement.PointerMovedEvent, (_, _) => OnUserActivity(),
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        AddHandler(InputElement.KeyDownEvent, (_, _) => OnUserActivity(),
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        AddHandler(InputElement.PointerPressedEvent, (_, _) => OnUserActivity(),
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
    }

    // Title-bar drag strip — the window is chromeless so we move it ourselves.
    private void TitleBarDrag_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void TitleBarDrag_DoubleTapped(object? sender, TappedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnUserActivity()
    {
        _lastActivityUtc = DateTime.UtcNow;
        // If we previously auto-flipped to "away", restore the user's chosen status.
        if (_statusBeforeAutoAway is not null)
        {
            var restore = _statusBeforeAutoAway;
            _statusBeforeAutoAway = null;
            SendEphemeralStatus(restore);
        }
    }

    private void StartIdleTimer()
    {
        if (_idleTimer is not null) return;
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _idleTimer.Tick += (_, _) => OnIdleTick();
        _idleTimer.Start();
    }

    private void OnIdleTick()
    {
        if (_auth?.Profile is null || _conn is null) return;
        // Only auto-away from "online" — never override DND or invisible.
        var current = _auth.Profile.Status ?? "online";
        if (_statusBeforeAutoAway is not null) return; // already auto-away
        if (current != "online") return;
        if (DateTime.UtcNow - _lastActivityUtc < IdleAwayThreshold) return;

        _statusBeforeAutoAway = current;
        SendEphemeralStatus("away");
    }

    private void SendEphemeralStatus(string status)
    {
        if (_conn is null || _auth?.Profile is null) return;
        // Update broadcast + UI but DON'T touch Profile.Status — the user's chosen
        // status must return on activity without re-saving the profile.
        _conn.Send(Rede.Core.Protocol.Msg.StatusUpdate, new System.Text.Json.Nodes.JsonObject
        {
            ["status"] = status,
            ["customStatus"] = _auth.Profile.CustomStatus,
        });
        _notifications.OwnStatus = status;
        Dispatcher.UIThread.Post(() =>
        {
            _mainVm.OwnStatus = status;
        });
    }

    private bool _flushingOnClose;
    private bool _forceQuit;

    // mlock/VirtualLock handle for the active passphrase byte[]. Keeps the
    // buffer pinned and resident (no swap/hibernation leakage) until close.
    private Rede.Core.Crypto.SecureMemory.SecureHandle? _passphraseLock;

    private void AdjustStartupSize()
    {
        const double idealW = 1100, idealH = 750;
        const double minW = 700, minH = 500;
        const double screenFraction = 0.80;

        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null)
        {
            Width = idealW;
            Height = idealH;
            return;
        }

        var work = screen.WorkingArea;
        double availW = work.Width / screen.Scaling;
        double availH = work.Height / screen.Scaling;

        Width = Math.Clamp(Math.Min(idealW, availW * screenFraction), minW, idealW);
        Height = Math.Clamp(Math.Min(idealH, availH * screenFraction), minH, idealH);
    }

    /// <summary>
    /// Triggers a real application shutdown (bypassing the minimize-to-tray interception).
    /// Called by the tray "Quit" menu item.
    /// </summary>
    public void ForceQuit()
    {
        _forceQuit = true;
        if (!IsVisible) Show(); // surface the window so the closing flush path runs normally
        Close();
    }

    private async void OnWindowClosing(object? sender, Avalonia.Controls.WindowClosingEventArgs e)
    {
        // Minimize-to-tray: if logged in and the user enabled the preference, hide the
        // window instead of tearing down the session. Bypass when _forceQuit is set
        // (tray Quit menu, Ctrl+Q, /quit slash command).
        if (!_forceQuit
            && _auth?.Profile is { MinimizeToTray: true }
            && _conn is not null)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        // Flush any pending debounced saves before window closes — otherwise
        // recent profile edits (accent color, avatar, status) can be lost if
        // the user closes within the 500ms debounce window.
        if (_flushingOnClose) return;
        if (_auth?.Profile is null || _auth?.Passphrase is null)
        {
            // No session to flush — skip Avalonia/WebView/audio teardown entirely.
            // Plain Shutdown() here hangs on webkit2gtk + HttpClient teardown on Linux
            // (several seconds, feels like a crash). Nothing is dirty, so a hard exit
            // is safe and instant.
            Environment.Exit(0);
            return;
        }
        e.Cancel = true;
        _flushingOnClose = true;
        try
        {
            ExportNoncesToProfile();
            await _store.FlushAsync(_auth.Profile, _auth.Passphrase);
            _auth.Profile?.ZeroSecrets();
            // Zero the passphrase WHILE still mlock'd, so the last content
            // written to the physical page is zeros, then release the lock+pin.
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(_auth.Passphrase);
            _passphraseLock?.Dispose();
            _passphraseLock = null;
            _fido.ClearSession();
            _lastFidoPin = null;
        }
        catch { }
        _conn?.Dispose();
        // Same rationale as the login-screen path: Avalonia's cooperative shutdown
        // hangs on WebView/audio/HttpClient teardown (multi-second freeze).
        // Everything that needs to persist has been flushed; everything sensitive
        // has been zeroed. A hard exit at this point is safe and instant.
        Environment.Exit(0);
    }

    private static string? _notificationSoundPath;
    private static string? _ringtonePath;

    private void ExtractNotificationSound()
    {
        _notificationSoundPath = ExtractEmbeddedWav("notification.wav", "rede-notification.wav", _notificationSoundPath);
        if (_notificationSoundPath is not null) _notifications.SetSoundPath(_notificationSoundPath);

        _ringtonePath = ExtractEmbeddedWav("ringtone.wav", "rede-ringtone.wav", _ringtonePath);
        if (_ringtonePath is not null) _notifications.SetRingtonePath(_ringtonePath);
    }

    private static string? ExtractEmbeddedWav(string resourceName, string tmpName, string? cached)
    {
        if (cached is not null) return cached;
        try
        {
            var asm = typeof(MainWindow).Assembly;
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream is null) return null;
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), tmpName);
            using (var fs = new System.IO.FileStream(tmp, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                stream.CopyTo(fs);
            return tmp;
        }
        catch { return null; }
    }

    private async void CheckForUpdatesAsync()
    {
        try
        {
            // Try git-based update first (dev / git clone installs)
            var repoPath = UpdateService.DetectRepoPath();
            if (repoPath is not null)
            {
                var updater = new UpdateService(repoPath);
                var (hasUpdates, local, remote) = await updater.CheckForUpdatesAsync();

                if (hasUpdates)
                {
                    // H2: Only notify — don't auto-apply git updates without user action
                    Dispatcher.UIThread.Post(() =>
                        _loginVm.StatusMessage = $"Update available ({remote[..8]}). Run 'git pull' to update.");
                    return;
                }
            }

            // Always check GitHub Releases API — even if git says up to date,
            // the running binary may be older than the latest release
            var release = await UpdateService.CheckGitHubReleaseAsync();
            if (release is not null)
            {
                _pendingRelease = release;
                Dispatcher.UIThread.Post(() =>
                {
                    _loginVm.StatusMessage = release.DownloadUrl is not null
                        ? $"Update available: {release.Tag} - click to install"
                        : $"Update available: {release.Tag} (no binary for this platform)";
                    _loginVm.IsUpdateAvailable = release.DownloadUrl is not null;
                });
            }
        }
        catch { }
    }

    private void ShowLogin(string? errorMessage = null)
    {
        _loginVm.ErrorMessage = errorMessage ?? "";
        _loginVm.IsLoading = false;
        // K1: Unsubscribe before subscribing to prevent duplicate handlers on repeated ShowLogin calls
        _loginVm.OnLoginRequested -= OnLogin;
        _loginVm.OnQuickLoginRequested -= OnQuickLogin;
        _loginVm.OnRegisterRequested -= OnRegister;
        _loginVm.OnUpdateRequested -= OnUpdateRequested;
        _loginVm.OnLoginRequested += OnLogin;
        _loginVm.OnQuickLoginRequested += OnQuickLogin;
        _loginVm.OnRegisterRequested += OnRegister;
        _loginVm.OnUpdateRequested += OnUpdateRequested;

        // FIDO2 unlock gate handlers
        _loginVm.OnSecurityKeyUnlockRequested -= OnSecurityKeyUnlock;
        _loginVm.OnRecoveryUnlockRequested -= OnRecoveryUnlock;
        _loginVm.OnFidoCancelRequested -= OnFidoCancel;
        _loginVm.OnSecurityKeyUnlockRequested += OnSecurityKeyUnlock;
        _loginVm.OnRecoveryUnlockRequested += OnRecoveryUnlock;
        _loginVm.OnFidoCancelRequested += OnFidoCancel;
        // Reset any stale gate state from a previous attempt.
        _pendingFido = null;
        _loginVm.IsAwaitingSecurityKey = false;
        _loginVm.ShowRecoveryEntry = false;
        _loginVm.NeedsPin = false;
        _loginVm.KeyPin = "";
        _loginVm.RecoveryCode = "";
        _loginVm.SecurityKeyStatus = "";

        // Enable quick-login mode if a profile hint exists from a previous session
        var hint = _store.ReadLastProfileHint();
        if (hint is not null)
        {
            _loginVm.QuickLoginHash = hint.Hash;
            _loginVm.HasQuickLogin = true;
            if (hint.ServerName is not null && _loginVm.ServerOptions.Contains(hint.ServerName))
                _loginVm.SelectedServer = hint.ServerName;
        }
        else
        {
            _loginVm.HasQuickLogin = false;
            _loginVm.QuickLoginHash = "";
        }

        var loginView = new LoginView { DataContext = _loginVm };
        RootContent.Content = loginView;
    }

    private async void OnUpdateRequested()
    {
        if (_pendingRelease is null) return;
        _loginVm.IsUpdateAvailable = false;
        var release = _pendingRelease;
        var ok = await UpdateService.DownloadAndReplaceAsync(release, status =>
            Dispatcher.UIThread.Post(() => _loginVm.StatusMessage = status));
        if (!ok)
            Dispatcher.UIThread.Post(() => _loginVm.IsUpdateAvailable = true);
    }

    private bool _mainVmWired;
    private BootView? _bootView;
    private TaskCompletionSource? _bootDone;
    private bool _authReady;
    private bool _authFailed;
    private DateTime _lastRetryTime;

    private void ShowMain()
    {
        // K1: Only wire once to prevent handler accumulation on reconnect
        if (!_mainVmWired)
        {
            WireMainViewModel();
            _mainVmWired = true;
        }

        // Boot animation running — signal it that auth is done
        if (_bootView is not null)
        {
            _authReady = true;
            _bootDone?.TrySetResult();
            return;
        }

        // Reconnect (no boot animation)
        var mainView = CreateMainView();
        RootContent.Content = mainView;
    }

    private void StartBootAnimation(string userId)
    {
        _bootDone = new TaskCompletionSource();
        _bootView = new BootView();
        _authReady = false;
        _authFailed = false;
        RootContent.Content = _bootView;

        _bootView.OnBootComplete += () => Dispatcher.UIThread.Post(async () =>
        {
            // Boot done — wait for auth if it hasn't completed yet
            if (!_authReady && !_authFailed)
                await (_bootDone?.Task ?? Task.CompletedTask);

            // Auth failed during boot — don't transition to main
            if (_authFailed)
                return;

            // If the FIDO2 second-touch encryption overlay is mid-decrypt, let it finish before we
            // swap to the main view — otherwise AUTH_OK (which races the decrypt) would cut it short.
            if (_bootView is not null)
                await _bootView.SecurityGateTask;

            _bootView = null;
            _bootDone = null;
            var mainView = CreateMainView();
            RootContent.Content = mainView;
        });

        _bootView.OnFailComplete += (err) => Dispatcher.UIThread.Post(() =>
        {
            _bootView = null;
            _bootDone = null;
            ShowLogin(err);
        });

        _ = _bootView.RunBootSequence(userId, _isNewUser, _lastTransport, _lastServerUrl);
    }

    private void ShowBootFail(string error)
    {
        if (_bootView is not null)
        {
            _authFailed = true;
            _bootDone?.TrySetResult();
            _ = _bootView.RunFailSequence(error);
        }
        else
        {
            ShowLogin(error);
        }
    }

    private MainView CreateMainView()
    {
        var mainView = new MainView { DataContext = _mainVm };
        mainView.OnRetryConnection += () =>
        {
            // H6: Rate-limit retry to once per 3 seconds
            var now = DateTime.UtcNow;
            if ((now - _lastRetryTime).TotalSeconds < 3) return;
            _lastRetryTime = now;

            if (_conn is not null)
            {
                _mainVm.ConnectionStatus = "Reconnecting...";
                _ = _conn.ConnectAsync();
            }
        };
        mainView.OnCallContact += (userId) =>
        {
            if (_call is not null)
                _callVm.StartOutgoingCall(userId);
        };
        // Group-call header button — reuses the scope-aware starter (same path as /call).
        mainView.OnCallGroup += (_) => StartGroupCallForCurrentScope();
        mainView.OnSettingsRequested += () => ShowSettings();

        // Mount CallView overlay
        var callView = new CallView { DataContext = _callVm };
        mainView.FindControl<Avalonia.Controls.ContentControl>("CallOverlay")!.Content = callView;

        return mainView;
    }

    private void OnLogin(string userId, byte[] passphrase, string serverUrl, string transport)
    {
        LoginAsync(userId, passphrase, serverUrl, transport);
    }

    private void OnQuickLogin(string hash, byte[] passphrase, string serverUrl, string transport)
    {
        QuickLoginAsync(hash, passphrase, serverUrl, transport);
    }

    private void SaveLoginHint(Rede.Core.Storage.Profile profile, string serverName)
    {
        try
        {
            if (_loginVm.StaySignedIn)
            {
                _store.SaveLastProfileHint(profile.UserId, serverName);
            }
            else
            {
                // User opted out — remove any existing hint so the next launch
                // shows the full login form again.
                _store.ClearLastProfileHint();
            }
            if (profile.LastServerName != serverName)
            {
                profile.LastServerName = serverName;
                if (_auth?.Passphrase is not null)
                    _store.SaveProfileDebounced(profile, _auth.Passphrase);
            }
        }
        catch { }
    }

    private async void QuickLoginAsync(string hash, byte[] passBytes, string serverUrl, string transport)
    {
        try
        {
            // Security-key gate: a profile with an enrolled FIDO2 key cannot be decrypted by
            // passphrase alone. Obtain the Profile Master Secret (key tap or recovery code)
            // first; the continuation re-enters this method once _store has the PMS.
            if (TryBeginFidoGate(hash, () => QuickLoginAsync(hash, passBytes, serverUrl, transport)))
                return;

            _loginVm.IsLoading = true;
            _loginVm.ErrorMessage = "";
            _lastServerUrl = serverUrl;
            _lastTransport = transport;
            _isNewUser = false;

            // Lock the passphrase buffer — ownership transferred from the view
            // on Submit. MainWindow holds it until logout/close and zeros it then.
            _passphraseLock?.Dispose();
            _passphraseLock = Rede.Core.Crypto.SecureMemory.Lock(passBytes);

            // Decrypt profile locally first so we can extract userId for the boot animation.
            // LoginByHashAsync will do this again via AuthService but that's acceptable — scrypt
            // is cached after first derivation, so the second call is ~1ms.
            var probe = await _store.LoadProfileByHashAsync(hash, passBytes);
            probe ??= await SelfHealStaleSidecarAsync(hash, passBytes);
            if (probe is null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(passBytes);
                _passphraseLock?.Dispose();
                _passphraseLock = null;
                Dispatcher.UIThread.Post(() => { _loginVm.IsLoading = false; _loginVm.ErrorMessage = "Wrong passphrase."; });
                return;
            }

            Dispatcher.UIThread.Post(() => StartBootAnimation(probe.UserId));

            InitServices(serverUrl, transport);
            WireQueueEvents();

            await _conn!.ConnectAsync();
            await WaitForQueueIfNeeded();
            var ok = await _auth!.LoginByHashAsync(hash, passBytes);
            if (!ok)
            {
                Dispatcher.UIThread.Post(() =>
                    ShowBootFail("Profile not found or wrong passphrase."));
                return;
            }

            PropagateProfile();
            BroadcastOwnProfile();
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
                ShowBootFail(SanitizeErrorMessage(ex)));
        }
    }

    private void OnRegister(string displayName, byte[] passphrase, string serverUrl, string transport, string inviteCode)
    {
        RegisterAsync(displayName, passphrase, serverUrl, transport, inviteCode);
    }

    private TaskCompletionSource? _queueAdmitTcs;
    private bool _isQueued;

    private void WireGroupCallEvents()
    {
        if (_groupCall is null) return;

        _groupCall.OnTokenReceived += info => Dispatcher.UIThread.Post(() =>
        {
            if (_auth?.Profile is null) return;
            // SFrame key is mixed with the per-session callId returned by the
            // server, so calls in the same channel across time get unrelated
            // keys (inter-call forward secrecy).
            var key = GroupCallService.DeriveSFrameKey(_auth.Profile, info.Scope, info.CallId);
            if (key is null)
            {
                _mainVm.AddSystemMessage("[Call] No E2EE key available for this scope - call aborted.");
                _groupCall?.EndCall();
                return;
            }

            // Close any existing call window before opening a new one. The
            // window receives the user's display name for labeling purposes;
            // the LiveKit connection uses the server-issued pseudonym from
            // info.Identity so the SFU never sees the real userId.
            _groupCallWindow?.Close();
            _groupCallWindow = new Views.GroupCallWindow();
            _groupCallWindow.Configure(_groupCall!, info, _auth.Profile.DisplayName ?? "", key);
            _groupCallWindow.Closed += (_, _) => _groupCallWindow = null;
            _groupCallWindow.Show(this);

            // Tell other members we started the call.
            _groupCall!.Announce(info.Scope);
        });

        _groupCall.OnTokenFailed += (scope, reason) => Dispatcher.UIThread.Post(() =>
        {
            _mainVm.AddSystemMessage($"[Call] Could not start call: {reason}");
        });

        _groupCall.OnIncomingAnnounce += (scope, startedBy, startedAt) => Dispatcher.UIThread.Post(() =>
        {
            var label = scope.Kind == "place" ? "place" : "group";
            _mainVm.AddSystemMessage($"[Call] {startedBy} started a call in this {label}. Use the phone button to join.");
        });

        _groupCall.OnCallEnded += (scope, endedBy) => Dispatcher.UIThread.Post(() =>
        {
            _mainVm.AddSystemMessage($"[Call] Call ended.");
        });
    }

    /// <summary>
    /// Start or join a group call for the currently selected Place channel or Group.
    /// Triggered from chat header button or /call slash command in a group/place scope.
    /// </summary>
    public void StartGroupCallForCurrentScope()
    {
        if (_groupCall is null || _auth?.Profile is null)
        {
            _mainVm.AddSystemMessage("[Call] Not ready.");
            return;
        }

        GCallScope? scope = _mainVm.SelectedConversation switch
        {
            ChannelItemViewModel ch => new GCallScope
            {
                Kind = "place",
                Id = ch.PlaceId,
                ChannelId = ch.ChannelId,
            },
            GroupItemViewModel g => new GCallScope
            {
                Kind = "group",
                Id = g.GroupId,
            },
            _ => null,
        };

        if (scope is null)
        {
            _mainVm.AddSystemMessage("[Call] Select a place channel or group first.");
            return;
        }

        if (!_groupCall.RequestToken(scope))
        {
            _mainVm.AddSystemMessage("[Call] Already in a call.");
        }
    }

    private void WireQueueEvents()
    {
        _isQueued = false;
        _queueAdmitTcs = new TaskCompletionSource();

        _conn!.OnQueuePosition += (pos, total) =>
        {
            _isQueued = true;
            Dispatcher.UIThread.Post(() => _bootView?.UpdateQueueStatus(pos, total));
        };

        _conn!.OnQueueAdmit += () =>
        {
            Dispatcher.UIThread.Post(() => _bootView?.ShowQueueAdmitted());
            _queueAdmitTcs?.TrySetResult();
        };
    }

    private async Task WaitForQueueIfNeeded()
    {
        // Give server a moment to send QUEUE_POSITION if we're queued
        await Task.Delay(200);
        if (_isQueued)
            await (_queueAdmitTcs?.Task ?? Task.CompletedTask);
    }

    private async void LoginAsync(string userId, byte[] passBytes, string serverUrl, string transport)
    {
        try
        {
            // Security-key gate (see QuickLoginAsync). hash = sha256(userId).
            var fidoHash = Rede.Core.Crypto.Fido2.Fido2SidecarStore.HashForUserId(userId.Trim());
            if (TryBeginFidoGate(fidoHash, () => LoginAsync(userId, passBytes, serverUrl, transport)))
                return;

            _loginVm.IsLoading = true;
            _loginVm.ErrorMessage = "";
            _lastServerUrl = serverUrl;
            _lastTransport = transport;
            _isNewUser = false;

            // Start boot animation immediately as loading screen
            Dispatcher.UIThread.Post(() => StartBootAnimation(userId));

            InitServices(serverUrl, transport);
            WireQueueEvents();

            // Ownership of passBytes transferred from the view on Submit. Lock it
            // and hold until logout/close.
            _passphraseLock?.Dispose();
            _passphraseLock = Rede.Core.Crypto.SecureMemory.Lock(passBytes);

            await _conn!.ConnectAsync();
            await WaitForQueueIfNeeded();
            var ok = await _auth!.LoginAsync(userId, passBytes);
            if (!ok && _store.HasActivePms)
            {
                // Stale/desynced security-key data — fall back to passphrase-only instead of locking out.
                var healed = await SelfHealStaleSidecarAsync(
                    Rede.Core.Crypto.Fido2.Fido2SidecarStore.HashForUserId(userId.Trim()), passBytes);
                if (healed is not null) ok = await _auth.LoginAsync(userId, passBytes);
            }
            if (!ok)
            {
                Dispatcher.UIThread.Post(() =>
                    ShowBootFail("Profile not found or wrong passphrase."));
                return;
            }

            PropagateProfile();
            BroadcastOwnProfile();
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
                ShowBootFail(SanitizeErrorMessage(ex)));
        }
    }

    // --- FIDO2 hardware-key unlock gate ---

    private sealed record PendingFidoLogin(string Hash, Action Continue);
    private PendingFidoLogin? _pendingFido;
    // Transient: the key PIN entered at unlock/enroll, reused for the server-2FA touch within
    // this run so the user isn't prompted for the PIN twice. Cleared on logout/close.
    private string? _lastFidoPin;

    /// <summary>
    /// If the profile has an enrolled security key and the session PMS isn't available yet,
    /// reveal the unlock gate and stash a continuation. Returns true (caller must return) when
    /// the gate took over; false to proceed with the normal passphrase-only flow.
    /// </summary>
    private bool TryBeginFidoGate(string hash, Action continueLogin)
    {
        if (!Rede.Core.Crypto.Fido2.Fido2SidecarStore.HasFidoEnrolled(hash) || _store.HasActivePms)
            return false;

        _pendingFido = new PendingFidoLogin(hash, continueLogin);
        Dispatcher.UIThread.Post(() =>
        {
            _loginVm.IsLoading = false;
            _loginVm.ShowRecoveryEntry = false;
            _loginVm.NeedsPin = false;
            _loginVm.IsAwaitingSecurityKey = true;
            _loginVm.SecurityKeyStatus = _fido.BackendAvailable
                ? "Touch your security key to unlock…"
                : "Security-key support isn't installed on this device. Use your recovery code.";
        });
        // Auto-attempt the assertion so the key starts blinking for a touch immediately.
        if (_fido.BackendAvailable)
            BeginKeyUnlock(null);
        return true;
    }

    private void OnSecurityKeyUnlock(string? pin) => BeginKeyUnlock(pin);
    private void OnRecoveryUnlock(string code) => BeginRecoveryUnlock(code);
    private void OnFidoCancel() => _pendingFido = null;

    private void BeginKeyUnlock(string? pin)
    {
        var pending = _pendingFido;
        if (pending is null) return;
        _lastFidoPin = pin; // reuse for the server-2FA assertion later this run
        Dispatcher.UIThread.Post(() => _loginVm.SecurityKeyStatus = "Touch your security key…");
        _ = Task.Run(() =>
        {
            try
            {
                var pms = _fido.TryUnlockWithKey(pending.Hash, pin);
                Dispatcher.UIThread.Post(() =>
                {
                    if (pms is null)
                        // null now means "no FIDO enrollment for this profile" (sidecar vanished
                        // between the gate and here). A key that responds but doesn't match throws
                        // Fido2Exception(NoCredentials) and is handled in the catch below.
                        _loginVm.SecurityKeyStatus = "No security key is enrolled for this profile. Use your passphrase.";
                    else
                        CompleteFidoUnlock();
                });
            }
            catch (Rede.Core.Crypto.Fido2.Fido2Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (ex.Kind is Rede.Core.Crypto.Fido2.Fido2ErrorKind.PinRequired
                        or Rede.Core.Crypto.Fido2.Fido2ErrorKind.PinInvalid)
                        _loginVm.NeedsPin = true;
                    _loginVm.SecurityKeyStatus = ex.Message;
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => _loginVm.SecurityKeyStatus = SanitizeErrorMessage(ex));
            }
        });
    }

    private void BeginRecoveryUnlock(string code)
    {
        var pending = _pendingFido;
        if (pending is null) return;
        Dispatcher.UIThread.Post(() => _loginVm.SecurityKeyStatus = "Checking recovery code…");
        _ = Task.Run(() =>
        {
            byte[]? pms = null;
            try { pms = _fido.UnlockWithRecovery(pending.Hash, code); }
            catch { /* treated as wrong code below */ }
            Dispatcher.UIThread.Post(() =>
            {
                if (pms is null) _loginVm.SecurityKeyStatus = "Wrong recovery code.";
                else CompleteFidoUnlock();
            });
        });
    }

    private void CompleteFidoUnlock()
    {
        var pending = _pendingFido;
        _pendingFido = null;
        _loginVm.IsAwaitingSecurityKey = false;
        _loginVm.KeyPin = "";
        _loginVm.RecoveryCode = "";
        _loginVm.IsLoading = true;
        // Re-run the original login; the gate now passes because _store has the PMS.
        pending?.Continue();
    }

    /// <summary>
    /// Recover from a stale/desynced FIDO2 sidecar: if a key/recovery unlock produced a secret
    /// that does NOT decrypt the profile, retry passphrase-only. On success the profile is actually
    /// passphrase-only, so the stale sidecar is dropped — recovering instead of locking the user out.
    /// Returns the decrypted profile, or null if passphrase-only also fails.
    /// </summary>
    private async Task<Rede.Core.Storage.Profile?> SelfHealStaleSidecarAsync(string hash, byte[] passBytes)
    {
        if (!_store.HasActivePms) return null;
        _store.SetActivePms(null);
        var profile = await _store.LoadProfileByHashAsync(hash, passBytes);
        if (profile is not null)
        {
            try { Rede.Core.Crypto.Fido2.Fido2SidecarStore.Delete(hash); } catch { }
            _fido.ClearSession();
            Dispatcher.UIThread.Post(() => _mainVm.AddSystemMessage(
                "Your security-key data was out of sync and has been reset. Re-enroll your key in Settings."));
        }
        return profile;
    }

    private async void RegisterAsync(string displayName, byte[] passBytes, string serverUrl, string transport, string inviteCode)
    {
        try
        {
            _loginVm.IsLoading = true;
            _loginVm.ErrorMessage = "";
            _lastServerUrl = serverUrl;
            _lastTransport = transport;
            _isNewUser = true;

            // Start boot animation immediately as loading screen
            Dispatcher.UIThread.Post(() => StartBootAnimation(displayName));

            InitServices(serverUrl, transport);
            WireQueueEvents();

            _passphraseLock?.Dispose();
            _passphraseLock = Rede.Core.Crypto.SecureMemory.Lock(passBytes);

            await _conn!.ConnectAsync();
            await WaitForQueueIfNeeded();
            await _auth!.RegisterAsync(displayName, passBytes, inviteCode);

            // M13: Clear invite code from VM after successful registration
            Dispatcher.UIThread.Post(() => _loginVm.InviteCode = "");
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
                ShowBootFail(SanitizeErrorMessage(ex)));
        }
    }

    private void InitServices(string serverUrl, string transport)
    {
        var proxy = new ProxySettings
        {
            UseTor = transport == "Tor",
            UseI2P = transport == "I2P",
        };

        // Load proxy URLs from .env if available
        var envFile = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Rede", "rede-client", ".env");
        if (System.IO.File.Exists(envFile))
        {
            foreach (var line in System.IO.File.ReadAllLines(envFile))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('#') || !trimmed.Contains('=')) continue;
                var eq = trimmed.IndexOf('=');
                var key = trimmed[..eq].Trim();
                var val = trimmed[(eq + 1)..].Trim();
                // M11: Validate proxy URLs from .env
                if (key == "REDE_I2P_PROXY" && Uri.TryCreate(val, UriKind.Absolute, out _)) proxy.I2PProxy = val;
                if (key == "REDE_TOR_PROXY" && Uri.TryCreate(val, UriKind.Absolute, out _)) proxy.TorProxy = val;
            }
        }

        // Dispose previous service cluster before replacing references. On a
        // second login/reconnect this prevents orphaned service instances and
        // drops their event subscriptions deterministically instead of leaving
        // cleanup to GC. The RedeConnection owns the transport-layer handler
        // delegates, so disposing it drops strong references back to the services.
        _auth?.Dispose();
        _chat?.Dispose();
        _contacts?.Dispose();
        _groups?.Dispose();
        _places?.Dispose();
        _devices?.Dispose();
        _call?.Dispose();
        _conn?.Dispose();

        _conn = new RedeConnection(serverUrl, proxy);
        _auth = new AuthService(_conn, _store);
        _chat = new ChatService(_conn, _store);
        _contacts = new ContactService(_conn, _store);
        _groups = new GroupService(_conn, _store);
        _places = new PlaceService(_conn, _store);
        // Sender-key / member-list distribution goes through the ratcheted DM channel
        _groups.Chat = _chat;
        _places.Chat = _chat;
        _blobs = new BlobService(_conn);
        _devices = new DeviceService(_conn, _store);
        _call = new CallService(_conn, _store);
        _callVm.Init(_call, _notifications);
        _groupCall = new GroupCallService(_conn);
        WireGroupCallEvents();

        // Connection events
        _conn.OnConnected += () =>
        {
            // Re-authenticate on reconnect
            _auth?.Reauthenticate();
            Dispatcher.UIThread.Post(() =>
            {
                _mainVm.ConnectionStatus = "Connected";
                _mainVm.IsConnected = true;
            });
        };

        _conn.OnDisconnected += () => Dispatcher.UIThread.Post(() =>
        {
            _mainVm.ConnectionStatus = "Disconnected";
            _mainVm.IsConnected = false;
            // Drop pending ACK queues — post-reconnect ACKs don't correspond to pre-disconnect
            // sends, so stale entries would mispair and stamp wrong messages.
            _chat?.ClearPendingAcks();
            _groups?.ClearPendingAcks();
            _places?.ClearPendingAcks();
            _mainVm.PendingAckVms.Clear();
        });

        _conn.OnReconnecting += () => Dispatcher.UIThread.Post(() =>
        {
            _mainVm.ConnectionStatus = "Reconnecting...";
            _mainVm.IsConnected = false;
            _chat?.ClearPendingAcks();
            _groups?.ClearPendingAcks();
            _places?.ClearPendingAcks();
            _mainVm.PendingAckVms.Clear();
        });

        _conn.OnError += err => Dispatcher.UIThread.Post(() =>
        {
            _mainVm.AddSystemMessage($"[Error] {err}");
        });

        // Surface profile save errors to UI. ProfileStore is long-lived (single
        // instance across login/logout cycles) so we must unsubscribe the previous
        // handler before re-binding or handlers accumulate on every re-login.
        if (_saveErrorHandler is not null)
            _store.OnSaveError -= _saveErrorHandler;
        _saveErrorHandler = err => Dispatcher.UIThread.Post(() =>
        {
            _mainVm.AddSystemMessage($"[WARNING] {err}");
        });
        _store.OnSaveError += _saveErrorHandler;

        // FIDO2 server-side 2FA: after Ed25519 auth, the server asks for a hardware assertion
        // before sending AUTH_OK. Sign the server challenge with the enrolled key and reply.
        _conn.On(Rede.Core.Protocol.Msg.Fido2VerifyChallenge, msg =>
        {
            var challengeB64 = msg["challenge"]?.GetValue<string>();
            if (challengeB64 is null || _auth?.Profile is null) return;
            var hash = Rede.Core.Crypto.Fido2.Fido2SidecarStore.HashForUserId(_auth.Profile.UserId);
            var sc = Rede.Core.Crypto.Fido2.Fido2SidecarStore.Load(hash);
            if (sc is null || sc.Keys.Count == 0) return;
            byte[] challenge;
            try { challenge = Convert.FromBase64String(challengeB64); }
            catch { return; }
            var allow = sc.Keys.Select(k => Convert.FromBase64String(k.CredentialId)).ToList();
            var rpId = sc.RpId;
            var pin = _lastFidoPin;
            _ = Task.Run(() =>
            {
                try
                {
                    // This is the SECOND touch (first one unlocked the local profile). The user is
                    // watching the boot animation right now, so take it over with the ASCII encryption
                    // lock overlay — the MainView ConnectionStatus isn't visible yet. _bootView is null
                    // only if boot already finished, in which case ConnectionStatus on the visible
                    // MainView covers it. (Boot can't actually finish first: it waits on AUTH_OK, which
                    // the server only sends after this assertion — so the overlay is reliably shown.)
                    Dispatcher.UIThread.Post(() =>
                    {
                        _mainVm.ConnectionStatus = "Touch your security key again…";
                        _bootView?.BeginSecurityGate();
                    });
                    var a = _fidoAuth.GetServerAssertion(rpId, allow, challenge, pin);
                    _conn?.Send(Rede.Core.Protocol.Msg.Fido2VerifyResponse, new System.Text.Json.Nodes.JsonObject
                    {
                        ["credentialId"] = Convert.ToBase64String(a.CredentialId),
                        ["authData"] = Convert.ToBase64String(a.AuthData),
                        ["signature"] = Convert.ToBase64String(a.Signature),
                    });
                    Dispatcher.UIThread.Post(() => _bootView?.ResolveSecurityGate(true));
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _bootView?.ResolveSecurityGate(false);
                        ShowBootFail("Security key verification failed: " + SanitizeErrorMessage(ex));
                    });
                }
            });
        });

        // Status / Presence handler — server broadcasts to all, client filters locally
        _conn.On(Rede.Core.Protocol.Msg.StatusChange, msg =>
        {
            var userId = msg["userId"]?.GetValue<string>();
            var status = msg["status"]?.GetValue<string>();
            var customStatus = msg["customStatus"]?.GetValue<string>();
            if (userId is null || status is null) return;

            // Privacy: only process if this user is in our contact list (ignore strangers)
            if (_auth?.Profile?.Contacts.TryGetValue(userId, out var contact) != true) return;

            contact.Status = status;
            contact.CustomStatus = customStatus;

            // Update sidebar UI
            Dispatcher.UIThread.Post(() =>
            {
                var contactVm = _mainVm.Contacts.FirstOrDefault(c => c.UserId == userId);
                if (contactVm is not null)
                {
                    contactVm.Status = status;
                    contactVm.CustomStatus = customStatus;
                }
            });
        });

        // Auth events
        _auth.OnAuthSuccess += () => Dispatcher.UIThread.Post(() =>
        {
            if (_auth?.Profile is not null)
                SaveLoginHint(_auth.Profile, _loginVm.SelectedServer);
            PropagateProfile();
            ShowMain();
        });

        _auth.OnAuthFailed += err => Dispatcher.UIThread.Post(() =>
        {
            // H3: Sanitize server-provided error messages
            _loginVm.ErrorMessage = SanitizeServerMessage(err);
            _loginVm.IsLoading = false;
        });

        _auth.OnSystemMessage += msg => Dispatcher.UIThread.Post(() =>
        {
            _mainVm.AddSystemMessage(msg);
        });

        _auth.OnStatusUpdate += status => Dispatcher.UIThread.Post(() =>
        {
            _mainVm.ConnectionStatus = status;
        });

        // Chat events
        _chat.OnMessageReceived += (from, text, chatId, ts, isSealed, msgId, fullMsg) => Dispatcher.UIThread.Post(() =>
        {
            // Only render in chat if the matching conversation is selected;
            // message is already persisted in chat history by the service layer
            if (_mainVm.SelectedConversation is ContactItemViewModel sel && sel.UserId == from)
            {
                _mainVm.AddIncomingMessage(from, text, ts, msgId: msgId);
                if (fullMsg?.Attachments is { Count: > 0 } && _mainVm.Messages.Count > 0)
                    LoadAttachmentsForMessage(_mainVm.Messages[^1], fullMsg.Attachments);
            }
            MarkContactUnread(from);

            // Desktop notification if not viewing this conversation
            if (_mainVm.SelectedConversation is not ContactItemViewModel selN || selN.UserId != from)
            {
                var displayName = _auth?.Profile?.Contacts.TryGetValue(from, out var c) == true
                    ? c.DisplayName ?? from : from;
                _notifications.ShowMessageNotification(displayName, text);
            }
        });

        _chat.OnOwnMessageIdAssigned += (contactId, msgId) => Dispatcher.UIThread.Post(() =>
        {
            // Dequeue head of the UI FIFO. Queue is cleared on chat switch, so an ACK
            // for a different chat finds an empty queue and is a no-op (the stored
            // ChatHistory is already stamped by the backend FIFO — reopening the chat
            // will render the correct MsgId).
            if (_mainVm.PendingAckVms.Count == 0) return;
            var vm = _mainVm.PendingAckVms.Dequeue();
            vm.MsgId = msgId;
        });

        _chat.OnReactionUpdated += (chatId, msgId, emoji, reactions) => Dispatcher.UIThread.Post(() =>
        {
            var msg = _mainVm.Messages.FirstOrDefault(m => m.MsgId == msgId);
            msg?.UpdateReactions(reactions, _auth?.Profile?.UserId);
        });

        _chat.OnSystemMessage += msg => Dispatcher.UIThread.Post(() =>
        {
            _mainVm.AddSystemMessage(msg);
        });

        _chat.OnProfileReceived += (senderId, accentColor, avatarData, avatarMimeType) =>
        {
            if (_auth?.Profile is null || _auth.Passphrase is null) return;
            if (_auth.Profile.Contacts.TryGetValue(senderId, out var contact))
            {
                // Accent: only overwrite when the payload carries a valid hex color —
                // a missing field means "no change", not "clear".
                if (accentColor is not null &&
                    System.Text.RegularExpressions.Regex.IsMatch(accentColor, @"^#[0-9a-fA-F]{6}$"))
                {
                    contact.AccentColor = accentColor;
                }
                // Avatar: only update when the payload includes one. The previous code
                // wiped the contact's avatar whenever a profile message arrived without
                // avatar data (e.g. when the sender just changed their accent color),
                // which is why avatars appeared to "not propagate" — they did, then got
                // erased by the next profile message.
                if (avatarData is not null && avatarData.Length <= 350_000)
                {
                    contact.AvatarData = avatarData;
                    contact.AvatarMimeType = avatarMimeType;
                }
                _store.SaveProfileDebounced(_auth.Profile, _auth.Passphrase);
                Dispatcher.UIThread.Post(() =>
                {
                    RefreshContacts();
                    // Restamp the sender's avatar/initial/accent on already-
                    // rendered chat messages — without this, the sidebar
                    // updates but message bubbles keep the snapshot from
                    // when AddIncomingMessage / LoadChatHistory ran.
                    if (_contactIndex.TryGetValue(senderId, out var cvm))
                    {
                        _mainVm.RefreshMessagesFromSender(
                            senderId,
                            cvm.AccentColor,
                            cvm.Initial,
                            cvm.AvatarImage,
                            cvm.HasAvatar);
                    }
                });
            }
        };

        _chat.OnNewDeviceDetected += (targetUserId, deviceId, publicKey, signingKey) =>
        {
            // H10: Thread-safe store of pending device for user confirmation
            _pendingDevices.TryAdd($"{targetUserId}:{deviceId}", (publicKey, signingKey));
        };

        _chat.OnGroupKeyReceived += (groupId, name, key, sig, senderId, members, membersSig) =>
        {
            // K4: Snapshot to local variables before async boundary
            var profile = _auth?.Profile;
            var passphrase = _auth?.Passphrase;
            if (profile is null || passphrase is null) return;

            // K3: Validate inputs
            if (string.IsNullOrEmpty(groupId) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(key)) return;
            var safeName = SanitizeDisplayString(name, 64);

            // H1: Require valid signature — reject if unverifiable
            if (string.IsNullOrEmpty(senderId) || string.IsNullOrEmpty(sig))
            {
                Dispatcher.UIThread.Post(() =>
                    _mainVm.AddSystemMessage($"[SECURITY] Group key without sender/signature - rejected."));
                return;
            }

            if (!profile.Contacts.TryGetValue(senderId, out var sender) || sender.SigningKey is null)
            {
                Dispatcher.UIThread.Post(() =>
                    _mainVm.AddSystemMessage($"[SECURITY] Group key from unknown sender {senderId} - rejected."));
                return;
            }

            byte[] keyBytes, sigBytes;
            try
            {
                keyBytes = Convert.FromBase64String(key);
                sigBytes = Convert.FromBase64String(sig);
            }
            catch
            {
                Dispatcher.UIThread.Post(() =>
                    _mainVm.AddSystemMessage($"[SECURITY] Malformed group key payload from {senderId} - rejected."));
                return;
            }

            if (!Rede.Core.Crypto.CryptoService.VerifyGroupKey(groupId, name, keyBytes, sigBytes, sender.SigningKey))
            {
                Dispatcher.UIThread.Post(() =>
                    _mainVm.AddSystemMessage($"[SECURITY] Invalid group key signature from {senderId}! Key rejected."));
                return;
            }

            // Take over the signed member list if present and valid; otherwise fall
            // back to {inviter, self} so sender-key exchange works at minimum
            var memberList = new List<string>();
            if (members is not null && membersSig is not null)
            {
                var mPayload = $"GROUPMEMBERS:{groupId}:{string.Join(",", members.OrderBy(m => m, StringComparer.Ordinal))}";
                if (Rede.Core.Crypto.CryptoService.VerifyBytes(
                        System.Text.Encoding.UTF8.GetBytes(mPayload), membersSig, sender.SigningKey))
                {
                    memberList = new List<string>(members);
                }
            }
            if (!memberList.Contains(senderId)) memberList.Add(senderId);
            if (!memberList.Contains(profile.UserId)) memberList.Add(profile.UserId);

            // Key rotation for an existing group: reset our own sender chain too, so
            // it is regenerated and redistributed only to the CURRENT member list
            var isRotation = profile.Groups.TryGetValue(groupId, out var existingGrp)
                             && existingGrp.Key is { Length: > 0 }
                             && !existingGrp.Key.SequenceEqual(keyBytes);

            Task.Run(async () =>
            {
                await _store.AddGroupAsync(profile, groupId, safeName, keyBytes, memberList, passphrase);
                if (isRotation)
                    _groups?.ResetOwnSenderKey(groupId);
                Dispatcher.UIThread.Post(() =>
                {
                    _mainVm.AddSystemMessage(isRotation
                        ? $"Group key rotated for \"{safeName}\" - sender chain reset."
                        : $"Received group key for \"{safeName}\" ({memberList.Count} member(s))");
                    RefreshGroups();
                });
            });
        };

        _chat.OnGroupMembersReceived += (groupId, members, sig, senderId) =>
        {
            _groups?.AcceptGroupMembers(groupId, members, sig, senderId);
        };

        _chat.OnSenderKeyReceived += (contextId, chainKey, messageNumber, sig, senderId) =>
        {
            if (contextId.StartsWith("place:", StringComparison.Ordinal))
                _places?.AcceptSenderKey(contextId, chainKey, messageNumber, sig, senderId);
            else
                _groups?.AcceptSenderKey(contextId, chainKey, messageNumber, sig, senderId);
        };

        // Contact events
        _contacts.OnContactAdded += (userId, displayName, fp) =>
        {
            // Send own profile to the new contact so they get our avatar/accent
            SendProfileToContact(userId);
            Dispatcher.UIThread.Post(() =>
            {
                _mainVm.AddSystemMessage($"Contact added: {displayName} ({fp})");
                RefreshContacts();
            });
        };

        _contacts.OnKeyChangeWarning += (userId, oldFp, newFp) => Dispatcher.UIThread.Post(() =>
        {
            _mainVm.AddSystemMessage($"[SECURITY] Key changed for {userId}! Old: {oldFp} New: {newFp}. Use /confirm {userId} to accept.");
        });

        _contacts.OnContactsChanged += () => Dispatcher.UIThread.Post(RefreshContacts);

        _contacts.OnSystemMessage += msg => Dispatcher.UIThread.Post(() =>
        {
            _mainVm.AddSystemMessage(msg);
        });

        // Group events
        _groups.OnGroupMessageReceived += (groupId, from, text, ts, fullMsg) => Dispatcher.UIThread.Post(() =>
        {
            // Only render in chat if the matching group is selected
            if (_mainVm.SelectedConversation is GroupItemViewModel selG && selG.GroupId == groupId)
            {
                _mainVm.AddIncomingMessage(from, text, ts);
                if (fullMsg?.Attachments is { Count: > 0 } && _mainVm.Messages.Count > 0)
                    LoadAttachmentsForMessage(_mainVm.Messages[^1], fullMsg.Attachments);
            }
            MarkGroupUnread(groupId);

            // Desktop notification if not viewing this group
            if (_mainVm.SelectedConversation is not GroupItemViewModel selGn || selGn.GroupId != groupId)
            {
                var groupName = _auth?.Profile?.Groups.TryGetValue(groupId, out var g) == true
                    ? g.Name : groupId;
                _notifications.ShowGroupNotification(groupName, from, text);
            }
        });

        _groups.OnReactionUpdated += (chatKey, msgId, emoji, reactions) => Dispatcher.UIThread.Post(() =>
        {
            var msg = _mainVm.Messages.FirstOrDefault(m => m.MsgId == msgId);
            msg?.UpdateReactions(reactions, _auth?.Profile?.UserId);
        });

        _groups.OnMessageEdited += (chatKey, msgId, newText) => Dispatcher.UIThread.Post(() =>
        {
            var msg = _mainVm.Messages.FirstOrDefault(m => m.MsgId == msgId);
            if (msg is not null) { msg.Text = newText; msg.IsEdited = true; }
        });

        _groups.OnMessageDeleted += (chatKey, msgId) => Dispatcher.UIThread.Post(() =>
        {
            var msg = _mainVm.Messages.FirstOrDefault(m => m.MsgId == msgId);
            if (msg is not null) { msg.Text = ""; msg.IsDeleted = true; }
        });

        _groups.OnOwnMessageIdAssigned += (groupId, msgId) => Dispatcher.UIThread.Post(() =>
        {
            if (_mainVm.PendingAckVms.Count == 0) return;
            var vm = _mainVm.PendingAckVms.Dequeue();
            vm.MsgId = msgId;
        });

        _groups.OnGroupsChanged += () => Dispatcher.UIThread.Post(RefreshGroups);

        _groups.OnSystemMessage += msg => Dispatcher.UIThread.Post(() =>
        {
            _mainVm.AddSystemMessage(msg);
        });

        // Place events
        _places.OnChannelMessageReceived += (placeId, channelId, from, text, ts, chatMsg) => Dispatcher.UIThread.Post(() =>
        {
            var (senderRole, roleColor) = GetSenderRoleInfo(placeId, from);
            var displayFrom = _places?.GetNickname(placeId, from) ?? from;
            _mainVm.AddIncomingMessage(displayFrom, text, ts, senderRole: senderRole, roleBadgeColor: roleColor,
                msgId: chatMsg?.MsgId, replyToPreview: chatMsg?.ReplyToPreview, replyToAuthor: chatMsg?.ReplyToAuthor);
            // Load attachments for incoming message
            if (chatMsg?.Attachments is { Count: > 0 } && _mainVm.Messages.Count > 0)
                LoadAttachmentsForMessage(_mainVm.Messages[^1], chatMsg.Attachments);
            MarkChannelUnread(placeId, channelId);

            // Desktop notification if not viewing this channel
            if (_mainVm.SelectedConversation is not ChannelItemViewModel selCh
                || selCh.PlaceId != placeId || selCh.ChannelId != channelId)
            {
                var placeName = _auth?.Profile?.Places.TryGetValue(placeId, out var pl) == true
                    ? pl.Name : placeId;
                _notifications.ShowGroupNotification(placeName, from, text);
            }
        });

        _places.OnReactionUpdated += (chatKey, msgId, emoji, reactions) => Dispatcher.UIThread.Post(() =>
        {
            var msg = _mainVm.Messages.FirstOrDefault(m => m.MsgId == msgId);
            msg?.UpdateReactions(reactions, _auth?.Profile?.UserId);
        });

        _places.OnMessageEdited += (chatKey, msgId, newText) => Dispatcher.UIThread.Post(() =>
        {
            var msg = _mainVm.Messages.FirstOrDefault(m => m.MsgId == msgId);
            if (msg is not null) { msg.Text = newText; msg.IsEdited = true; }
        });

        _places.OnMessageDeleted += (chatKey, msgId) => Dispatcher.UIThread.Post(() =>
        {
            var msg = _mainVm.Messages.FirstOrDefault(m => m.MsgId == msgId);
            if (msg is not null) { msg.Text = ""; msg.IsDeleted = true; }
        });

        _places.OnOwnMessageIdAssigned += (chatKey, msgId) => Dispatcher.UIThread.Post(() =>
        {
            if (_mainVm.PendingAckVms.Count == 0) return;
            var vm = _mainVm.PendingAckVms.Dequeue();
            vm.MsgId = msgId;
        });

        _places.OnPlacesChanged += () => Dispatcher.UIThread.Post(RefreshPlaces);

        _places.OnSystemMessage += msg => Dispatcher.UIThread.Post(() =>
        {
            _mainVm.AddSystemMessage(msg);
        });

        _chat.OnPlaceKeyReceived += (placeId, metadataKey, encryptedMetadata, senderId) =>
        {
            Task.Run(async () =>
            {
                if (_places is not null)
                {
                    await _places.HandlePlaceKeyReceived(placeId, metadataKey, encryptedMetadata, senderId);
                    Dispatcher.UIThread.Post(RefreshPlaces);
                }
            });
        };

        // Device events
        _devices.OnSystemMessage += msg => Dispatcher.UIThread.Post(() =>
        {
            _mainVm.AddSystemMessage(msg);
        });

        StartIdleTimer();
    }

    /// <summary>
    /// Merge current in-memory NonceTracker state from chat/group/place services into
    /// <c>Profile.SeenNonces</c> so replay-protection survives across restarts.
    /// Called before every profile flush (Ctrl+Q, window close, explicit shutdown).
    /// </summary>
    private void ExportNoncesToProfile()
    {
        if (_auth?.Profile is null) return;
        var merged = new Dictionary<string, long>();

        void Merge(NonceTracker? t)
        {
            if (t is null) return;
            foreach (var kv in t.ExportSnapshot())
            {
                // Keep the freshest timestamp per nonce so aging works correctly.
                if (!merged.TryGetValue(kv.Key, out var existing) || kv.Value > existing)
                    merged[kv.Key] = kv.Value;
            }
        }

        Merge(_chat?.NonceTracker);
        Merge(_groups?.NonceTracker);
        Merge(_places?.NonceTracker);

        // Hard cap: never persist more than 10k entries to keep the encrypted profile
        // bounded. NonceTracker already enforces this in-memory, but a defensive check
        // here guards against drift between merged-set size and per-tracker caps.
        if (merged.Count > 10000)
        {
            merged = merged
                .OrderByDescending(kv => kv.Value)
                .Take(10000)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        _auth.Profile.SeenNonces = merged;
    }

    private void PropagateProfile()
    {
        if (_auth?.Profile is null) return;
        var p = _auth.Profile;
        var pp = _auth.Passphrase;

        if (_chat is not null) { _chat.Profile = p; _chat.Passphrase = pp; }
        if (_contacts is not null) { _contacts.Profile = p; _contacts.Passphrase = pp; }
        if (_groups is not null) { _groups.Profile = p; _groups.Passphrase = pp; }
        if (_places is not null) { _places.Profile = p; _places.Passphrase = pp; }
        if (_blobs is not null) { _blobs.Profile = p; _blobs.Passphrase = pp; }
        if (_devices is not null) { _devices.Profile = p; _devices.Passphrase = pp; }
        if (_call is not null) { _call.Profile = p; _call.Passphrase = pp; }

        // Replay-protection: rehydrate per-service NonceTrackers from the persisted
        // profile snapshot. Without this, messages received within the 1h replay
        // window before the last shutdown could be replayed after a restart.
        // We import the same merged snapshot into all three trackers — a nonce seen
        // in any context stays blocked across all contexts, which is safe (slightly
        // over-strict) since nonces are random 24-byte values with negligible collision.
        if (p.SeenNonces.Count > 0)
        {
            _chat?.NonceTracker.ImportSnapshot(p.SeenNonces);
            _groups?.NonceTracker.ImportSnapshot(p.SeenNonces);
            _places?.NonceTracker.ImportSnapshot(p.SeenNonces);
        }

        // Cleanup expired TTL messages on login
        if (pp is not null)
            Task.Run(async () => await _store.CleanupExpiredMessagesAsync(p, pp));

        // Configure notifications from profile
        _notifications.Enabled = p.NotificationsEnabled;
        _notifications.ShowContent = p.NotificationShowContent;
        _notifications.SoundEnabled = p.NotificationSoundEnabled;
        _notifications.OwnStatus = p.Status ?? "online";
        // Extract embedded notification sound to a temp file (single-file publish
        // doesn't include CopyToOutput content, so the wav must be embedded).
        ExtractNotificationSound();

        // Apply saved theme variant + accent color (live-swap color resources)
        Themes.ThemeService.Apply(p.ThemeVariant);
        Themes.ThemeService.ApplyAccent(p.AccentColor);

        // Send own status (server broadcasts to all — no contact list leaked)
        SendOwnStatus();

        Dispatcher.UIThread.Post(() =>
        {
            RefreshContacts();
            RefreshGroups();
            RefreshPlaces();
            UpdateOwnProfilePanel();
        });
    }

    /// <summary>Broadcast own profile (avatar, accent) to all contacts on login.</summary>
    private void BroadcastOwnProfile()
    {
        var p = _auth?.Profile;
        if (p is null || _chat is null) return;
        if (p.AccentColor is null && p.AvatarData is null) return;
        _chat.BroadcastProfile(p.AccentColor, p.AvatarData, p.AvatarMimeType);
    }

    /// <summary>Send own profile to a single contact.</summary>
    private void SendProfileToContact(string contactId)
    {
        var p = _auth?.Profile;
        if (p is null || _chat is null) return;
        if (p.AccentColor is null && p.AvatarData is null) return;
        _chat.SendProfileTo(contactId, p.AccentColor, p.AvatarData, p.AvatarMimeType);
    }

    // The color used for contacts / places / own profile when no per-entity
    // accent is set. Follows the user's profile accent so changing it in
    // Settings recolors every "default" avatar in the sidebar.
    private string DefaultAccent() => _auth?.Profile?.AccentColor ?? "#8b5cf6";

    // Re-apply the current default accent color to any contact/place/channel
    // VMs whose stored accent is the default. Called after the user changes
    // their profile accent so existing sidebar items update live without
    // having to reconnect.
    private void RefreshDefaultAccents()
    {
        if (_auth?.Profile is null) return;
        var def = DefaultAccent();

        foreach (var contact in _mainVm.Contacts)
        {
            if (!_auth.Profile.Contacts.TryGetValue(contact.UserId, out var c)) continue;
            if (string.IsNullOrEmpty(c.AccentColor) && contact.AccentColor != def)
                contact.AccentColor = def;
        }

        foreach (var place in _mainVm.Places)
        {
            if (!_auth.Profile.Places.TryGetValue(place.PlaceId, out var p)) continue;
            if (string.IsNullOrEmpty(p.AccentColor) && place.AccentColor != def)
                place.AccentColor = def;
        }
    }

    private string? _ownAvatarDataCache; // track own avatar data to avoid redundant bitmap decode

    private void UpdateOwnProfilePanel()
    {
        if (_auth?.Profile is null) return;
        var p = _auth.Profile;
        _mainVm.OwnDisplayName = p.DisplayName;
        _mainVm.OwnUserId = p.UserId;
        _mainVm.OwnAccentColor = p.AccentColor ?? "#8b5cf6";
        _mainVm.OwnStatus = p.Status ?? "online";
        _mainVm.OwnCustomStatus = p.CustomStatus;

        // Only re-decode avatar bitmap if data actually changed
        if (p.AvatarData != _ownAvatarDataCache)
        {
            _ownAvatarDataCache = p.AvatarData;
            if (!string.IsNullOrEmpty(p.AvatarData))
            {
                try
                {
                    var bytes = Convert.FromBase64String(p.AvatarData);
                    using var ms = new System.IO.MemoryStream(bytes);
                    var oldBmp = _mainVm.OwnAvatarImage;
                    _mainVm.OwnAvatarImage = new Avalonia.Media.Imaging.Bitmap(ms);
                    _mainVm.HasOwnAvatar = true;
                    oldBmp?.Dispose();
                }
                catch { _mainVm.OwnAvatarImage = null; _mainVm.HasOwnAvatar = false; }
            }
            else
            {
                var oldBmp = _mainVm.OwnAvatarImage;
                _mainVm.OwnAvatarImage = null;
                _mainVm.HasOwnAvatar = false;
                oldBmp?.Dispose();
            }
        }
    }

    private void SendOwnStatus()
    {
        if (_conn is null || _auth?.Profile is null) return;

        // Send own status — server broadcasts to all, no contact list sent
        var status = _auth.Profile.Status ?? "online";
        _conn.Send(Rede.Core.Protocol.Msg.StatusUpdate, new System.Text.Json.Nodes.JsonObject
        {
            ["status"] = status,
            ["customStatus"] = _auth.Profile.CustomStatus,
        });
    }

    private void WireMainViewModel()
    {
        _mainVm.OnMessageSend += (text, replyMsgId, replyPreview, replyAuthor) =>
        {
            if (_mainVm.SelectedConversation is ContactItemViewModel contact)
                _chat?.SendMessage(contact.UserId, text, _mainVm.TtlSeconds);
            else if (_mainVm.SelectedConversation is GroupItemViewModel group)
                _groups?.SendGroupMessage(group.GroupId, text, _mainVm.TtlSeconds);
            else if (_mainVm.SelectedConversation is ChannelItemViewModel channel)
                _places?.SendChannelMessage(channel.PlaceId, channel.ChannelId, text, _mainVm.TtlSeconds,
                    replyMsgId, replyPreview, replyAuthor);
        };

        _mainVm.OnMessageEdit += (msgId, newText) =>
        {
            if (_mainVm.SelectedConversation is GroupItemViewModel group)
            {
                // Groups don't have SendEdit yet — would need similar SendControlMessage pattern
            }
            else if (_mainVm.SelectedConversation is ChannelItemViewModel channel)
                _places?.SendEdit(channel.PlaceId, channel.ChannelId, msgId, newText);
        };

        _mainVm.OnMessageDelete += (msgId) =>
        {
            if (_mainVm.SelectedConversation is GroupItemViewModel group)
            {
                // Groups don't have SendDelete yet
            }
            else if (_mainVm.SelectedConversation is ChannelItemViewModel channel)
                _places?.SendDelete(channel.PlaceId, channel.ChannelId, msgId);
        };

        _mainVm.OnCommandExecuted += (cmd, args) =>
        {
            HandleCommand(cmd, args);
        };

        _mainVm.OnChatHistoryRequested += chatId =>
        {
            LoadChatHistoryForConversation(chatId);
        };

        _mainVm.OnMemberListRequested += placeId =>
        {
            LoadMemberList(placeId);
        };

        _mainVm.OnAttachFiles += (paths) =>
        {
            _ = HandleAttachFiles(paths);
        };

        _mainVm.OnPinMessage += (msgId, preview, author) =>
        {
            if (_mainVm.SelectedConversation is ChannelItemViewModel ch)
                _places?.PinMessage(ch.PlaceId, ch.ChannelId, msgId, preview, author, _chat);
        };

        _mainVm.OnReactionSend += (msgId, emoji, add) =>
        {
            if (_mainVm.SelectedConversation is ChannelItemViewModel ch)
                _places?.SendReaction(ch.PlaceId, ch.ChannelId, msgId, emoji, add);
            else if (_mainVm.SelectedConversation is GroupItemViewModel gr)
                _groups?.SendReaction(gr.GroupId, msgId, emoji, add);
            else if (_mainVm.SelectedConversation is ContactItemViewModel ct)
                _chat?.SendReaction(ct.UserId, msgId, emoji, add);
        };

        _mainVm.OnForwardMessage += (targetId, text, isGroup) =>
        {
            var fwd = $"[Forwarded] {text}";
            if (isGroup)
                _groups?.SendGroupMessage(targetId, fwd, 0);
            else
                _chat?.SendMessage(targetId, fwd, 0);
        };
    }

    private async Task HandleAttachFiles(string[] paths)
    {
        if (_blobs is null) return;
        // Snapshot the conversation up front — the user may switch chats while
        // the upload runs and we'd otherwise send into the wrong target.
        var target = _mainVm.SelectedConversation;
        if (target is null) return;

        var attachments = new List<Rede.Core.Storage.AttachmentInfo>();
        foreach (var path in paths)
        {
            try
            {
                var fileData = await System.IO.File.ReadAllBytesAsync(path);
                var fileName = System.IO.Path.GetFileName(path);
                var mimeType = GuessMimeType(path);

                Dispatcher.UIThread.Post(() => _mainVm.AddSystemMessage($"Uploading {fileName}..."));

                var att = await _blobs.UploadAsync(fileName, mimeType, fileData);
                if (att is not null) attachments.Add(att);
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => _mainVm.AddSystemMessage($"Failed to upload {System.IO.Path.GetFileName(path)}: {ex.Message}"));
            }
        }

        if (attachments.Count == 0) return;

        // Empty caption is fine — the recipient still gets the attachment list
        // through the message envelope, and the empty text bubble is hidden in
        // the chat view (HasText binding) so we don't show a placeholder chip.
        var text = _mainVm.InputText?.Trim() ?? "";
        Dispatcher.UIThread.Post(() => _mainVm.InputText = "");

        switch (target)
        {
            case ChannelItemViewModel channel:
                _places?.SendChannelMessage(channel.PlaceId, channel.ChannelId, text, _mainVm.TtlSeconds,
                    attachments: attachments);
                break;

            case ContactItemViewModel contact:
                // 1:1 chat — the server doesn't echo, so add an optimistic
                // bubble for the sender and stamp the MsgId once the ACK lands.
                Dispatcher.UIThread.Post(() =>
                {
                    if (_mainVm.SelectedConversation is ContactItemViewModel stillSel && stillSel.UserId == contact.UserId)
                    {
                        var optimistic = new ChatMessageViewModel
                        {
                            Text = text,
                            IsOwn = true,
                            Timestamp = DateTime.Now,
                        };
                        _mainVm.Messages.Add(optimistic);
                        _mainVm.PendingAckVms.Enqueue(optimistic);
                        LoadAttachmentsForMessage(optimistic, attachments);
                    }
                });
                _chat?.SendMessage(contact.UserId, text, _mainVm.TtlSeconds, attachments);
                break;

            case GroupItemViewModel group:
                Dispatcher.UIThread.Post(() =>
                {
                    if (_mainVm.SelectedConversation is GroupItemViewModel stillSel && stillSel.GroupId == group.GroupId)
                    {
                        var optimistic = new ChatMessageViewModel
                        {
                            Text = text,
                            IsOwn = true,
                            Timestamp = DateTime.Now,
                        };
                        _mainVm.Messages.Add(optimistic);
                        _mainVm.PendingAckVms.Enqueue(optimistic);
                        LoadAttachmentsForMessage(optimistic, attachments);
                    }
                });
                _groups?.SendGroupMessage(group.GroupId, text, _mainVm.TtlSeconds, attachments);
                break;
        }
    }

    private static string? GuessMimeType(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            ".mp4" => "video/mp4",
            ".mp3" => "audio/mpeg",
            ".ogg" => "audio/ogg",
            ".wav" => "audio/wav",
            ".zip" => "application/zip",
            ".txt" => "text/plain",
            _ => "application/octet-stream",
        };
    }

    private void HandleCommand(string cmd, string[] args)
    {
        // K3: Validate command arguments
        for (int i = 0; i < args.Length; i++)
            args[i] = SanitizeDisplayString(args[i], 255);

        switch (cmd.ToLowerInvariant())
        {
            case "add" when args.Length >= 1:
                if (!IsValidUserId(args[0]))
                {
                    _mainVm.AddSystemMessage("Invalid user ID format.");
                    break;
                }
                _contacts?.AddContact(args[0]);
                break;

            case "remove" or "delete" when args.Length >= 1:
                if (_contacts is not null)
                {
                    var contactId = args[0];
                    _ = _contacts.RemoveContact(contactId).ContinueWith(t =>
                    {
                        if (t.Result)
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (_mainVm.SelectedConversation is ContactItemViewModel selC && selC.UserId == contactId)
                                {
                                    _mainVm.SelectedConversation = null;
                                    _mainVm.Messages.Clear();
                                }
                                RefreshContacts();
                            });
                    }, TaskScheduler.Default);
                }
                break;

            case "resync" when args.Length >= 1:
                if (_auth?.Profile is not null && _auth.Passphrase is not null)
                {
                    var uid = args[0];
                    // Delete all ratchet states for this contact to force new X3DH
                    var keysToRemove = _auth.Profile.RatchetStates.Keys
                        .Where(k => k == uid || k.StartsWith(uid + ":")).ToList();
                    foreach (var key in keysToRemove)
                        _auth.Profile.RatchetStates.Remove(key);
                    _store.SaveProfileDebounced(_auth.Profile, _auth.Passphrase);
                    _mainVm.AddSystemMessage($"Ratchet session reset for {uid}. Next message will establish a new session.");
                }
                break;

            case "confirm" when args.Length >= 1:
                // Accept pending new devices for this contact
                AcceptPendingDevices(args[0]);
                _ = _contacts?.ConfirmKeyChange(args[0]);
                break;

            case "fingerprint" or "fp":
                var fpUserId = args.Length >= 1 ? args[0] : null;
                var fp = _contacts?.GetFingerprint(fpUserId);
                _mainVm.AddSystemMessage(fp is not null ? $"Fingerprint: {fp}" : "No fingerprint available.");
                break;

            case "group" when args.Length >= 1:
                // H6: Validate joined arg length
                var groupName = string.Join(" ", args);
                if (groupName.Length > 64) { _mainVm.AddSystemMessage("Group name too long (max 64 chars)."); break; }
                _groups?.CreateGroup(groupName);
                break;

            case "ginvite" when args.Length >= 2:
                _groups?.InviteToGroup(args[0], args[1], _chat);
                break;

            case "kick" when args.Length >= 2:
                _groups?.KickFromGroup(args[0], args[1]);
                break;

            case "ttl" when args.Length >= 1 && int.TryParse(args[0], out var ttl):
                // K3: Clamp TTL to valid range (0=off, 1-365 days)
                ttl = Math.Clamp(ttl, 0, 365);
                _mainVm.TtlSeconds = ttl;
                _mainVm.AddSystemMessage(ttl > 0 ? $"TTL set to {ttl} day(s) - messages auto-delete after {ttl}d" : "TTL disabled");
                break;

            case "link":
                if (_devices is not null)
                {
                    var linkCode = _devices.CreateDeviceLink();
                    _mainVm.AddSystemMessage("Device link code (expires in 5 min):");
                    _mainVm.AddSystemMessage($"  {linkCode}");
                }
                break;

            case "devices":
                var devId = _devices?.GetDeviceId();
                _mainVm.AddSystemMessage(devId is not null ? $"Your device: {devId}" : "No device ID.");
                break;

            case "rekey" when args.Length >= 1:
                var rekeyGroupId = FindGroupId(args[0]);
                if (rekeyGroupId is not null)
                    _groups?.RekeyGroup(rekeyGroupId, _chat);
                else
                    _mainVm.AddSystemMessage("Group not found.");
                break;

            case "place" when args.Length >= 1:
                _places?.CreatePlace(string.Join(" ", args));
                break;

            case "pprofile":
                // Handled via context menu flyout — args: placeId, accentColor, iconData?, iconMimeType?
                if (args.Length >= 2)
                    _places?.UpdatePlaceProfile(args[0], args[1],
                        args.Length > 2 ? args[2] : null,
                        args.Length > 3 ? args[3] : null, _chat);
                break;

            case "pchannel" when args.Length >= 2:
                var pcPlaceId = FindPlaceId(args[0]);
                if (pcPlaceId is not null)
                    _places?.CreateChannel(pcPlaceId, string.Join(" ", args[1..]), _chat);
                else
                    _mainVm.AddSystemMessage("Place not found.");
                break;

            // GUI-only: create a channel directly inside a category. args: placeId, category, name.
            // placeId/category are passed as discrete elements so spaces are preserved.
            case "pchannelnew" when args.Length >= 3:
                _places?.CreateChannel(args[0], string.Join(" ", args[2..]), _chat,
                    string.IsNullOrEmpty(args[1]) ? null : args[1]);
                break;

            case "pchannelrm" when args.Length >= 2:
                _places?.RemoveChannel(args[0], args[1]);
                break;

            case "pinvite" when args.Length >= 2:
                var piPlaceId = FindPlaceId(args[0]);
                if (piPlaceId is not null)
                    _places?.InviteToPlace(piPlaceId, args[1], _chat);
                else
                    _mainVm.AddSystemMessage("Place not found.");
                break;

            case "pkick" when args.Length >= 2:
                var pkPlaceId = FindPlaceId(args[0]);
                if (pkPlaceId is not null)
                {
                    _places?.KickFromPlace(pkPlaceId, args[1]);
                    _places?.RekeyPlace(pkPlaceId, _chat);
                }
                else
                    _mainVm.AddSystemMessage("Place not found.");
                break;

            case "pleave" when args.Length >= 1:
                var plPlaceId = FindPlaceId(args[0]);
                if (plPlaceId is not null)
                    _places?.LeavePlace(plPlaceId);
                else
                    _mainVm.AddSystemMessage("Place not found.");
                break;

            case "prekey" when args.Length >= 1:
                var prPlaceId = FindPlaceId(args[0]);
                if (prPlaceId is not null)
                    _places?.RekeyPlace(prPlaceId, _chat);
                else
                    _mainVm.AddSystemMessage("Place not found.");
                break;

            case "pban" when args.Length >= 2:
                var pbanPlaceId = FindPlaceId(args[0]);
                if (pbanPlaceId is not null)
                {
                    var reason = args.Length > 2 ? string.Join(" ", args[2..]) : null;
                    _places?.BanUser(pbanPlaceId, args[1], reason, _chat);
                }
                else
                    _mainVm.AddSystemMessage("Place not found.");
                break;

            case "punban" when args.Length >= 2:
                var punbanPlaceId = FindPlaceId(args[0]);
                if (punbanPlaceId is not null)
                    _places?.UnbanUser(punbanPlaceId, args[1], _chat);
                else
                    _mainVm.AddSystemMessage("Place not found.");
                break;

            case "prole" when args.Length >= 3:
                var prPlaceId2 = FindPlaceId(args[0]);
                if (prPlaceId2 is not null)
                {
                    var roleVal = args[2].ToLowerInvariant() == "admin"
                        ? Rede.Core.Storage.PlaceRole.Admin
                        : Rede.Core.Storage.PlaceRole.Member;
                    _places?.SetRole(prPlaceId2, args[1], roleVal, _chat);
                }
                else
                    _mainVm.AddSystemMessage("Place not found.");
                break;

            case "ptransfer" when args.Length >= 2:
                var ptXferPlaceId = FindPlaceId(args[0]);
                if (ptXferPlaceId is not null)
                    _places?.TransferOwnership(ptXferPlaceId, args[1], _chat);
                else
                    _mainVm.AddSystemMessage("Place not found.");
                break;

            // Custom role management
            case "pcrole" when args.Length >= 2:
            {
                if (_mainVm.SelectedConversation is not ChannelItemViewModel crCh)
                {
                    _mainVm.AddSystemMessage("Select a place channel first.");
                    break;
                }
                var sub = args[0].ToLowerInvariant();
                switch (sub)
                {
                    case "create" when args.Length >= 3:
                        // /pcrole create <name> [permissions-number]
                        var perms = args.Length >= 3 && long.TryParse(args[^1], out var p) ? p : (long)Rede.Core.Storage.PlacePermission.SendMessages;
                        var roleName = args.Length >= 4 ? string.Join(" ", args[1..^1]) : args[1];
                        _places?.CreateCustomRole(crCh.PlaceId, roleName, "#6b7280", perms, _chat);
                        break;
                    case "delete" when args.Length >= 2:
                        _places?.DeleteCustomRole(crCh.PlaceId, args[1], _chat);
                        break;
                    case "assign" when args.Length >= 3:
                        _places?.AssignRole(crCh.PlaceId, args[1], args[2], _chat);
                        break;
                    case "remove" when args.Length >= 3:
                        _places?.RemoveRole(crCh.PlaceId, args[1], args[2], _chat);
                        break;
                    case "list":
                    {
                        if (_auth?.Profile?.Places.TryGetValue(crCh.PlaceId, out var pl) == true)
                        {
                            if (pl.CustomRoles.Count == 0)
                                _mainVm.AddSystemMessage("No custom roles. Use /pcrole create <name> to create one.");
                            else
                            {
                                _mainVm.AddSystemMessage($"--- {pl.CustomRoles.Count} role(s) ---");
                                foreach (var (id, role) in pl.CustomRoles.OrderByDescending(r => r.Value.Position))
                                    _mainVm.AddSystemMessage($"  [{id}] {role.Name} (pos:{role.Position}, perms:{role.Permissions})");
                            }
                        }
                        break;
                    }
                    case "init":
                        // Initialize default roles (migration from 3-tier)
                        if (_auth?.Profile?.Places.TryGetValue(crCh.PlaceId, out var initPl) == true)
                        {
                            _places?.InitializeDefaultRoles(initPl, initPl.CreatorId);
                            _store?.SaveProfileDebounced(_auth.Profile, _auth.Passphrase);
                            _mainVm.AddSystemMessage("Default roles initialized.");
                        }
                        break;
                    default:
                        _mainVm.AddSystemMessage("Usage: /pcrole create|delete|assign|remove|list|init ...");
                        break;
                }
                break;
            }

            case "prolecolors" when args.Length >= 4:
                var prcPlaceId = FindPlaceId(args[0]);
                if (prcPlaceId is not null)
                    _places?.UpdateRoleColors(prcPlaceId, args[1], args[2], args[3], _chat);
                else
                    _mainVm.AddSystemMessage("Place not found.");
                break;

            case "ptopic" when args.Length >= 3:
                var ptPlaceId = FindPlaceId(args[0]);
                if (ptPlaceId is not null)
                    _places?.SetChannelTopic(ptPlaceId, args[1], string.Join(" ", args[2..]), _chat);
                else
                    _mainVm.AddSystemMessage("Place not found.");
                break;

            case "pcategory" when args.Length >= 2:
                var pcatPlaceId = FindPlaceId(args[0]);
                if (pcatPlaceId is not null)
                    _places?.AddCategory(pcatPlaceId, string.Join(" ", args[1..]), _chat);
                else
                    _mainVm.AddSystemMessage("Place not found.");
                break;

            case "pcategoryrm" when args.Length >= 2:
                var pcatrmPlaceId = FindPlaceId(args[0]);
                if (pcatrmPlaceId is not null)
                    _places?.RemoveCategory(pcatrmPlaceId, string.Join(" ", args[1..]), _chat);
                else
                    _mainVm.AddSystemMessage("Place not found.");
                break;

            case "pchannelcat" when args.Length >= 2:
                // args: placeId, channelId, [category]. Empty/missing category -> uncategorized (null).
                var pccCategory = args.Length >= 3 ? string.Join(" ", args[2..]) : "";
                _places?.SetChannelCategory(args[0], args[1],
                    string.IsNullOrEmpty(pccCategory) ? null : pccCategory, _chat);
                break;

            case "pnick" when args.Length >= 1:
                // /pnick <nickname> — set own nickname in current place
                // /pnick <userId> <nickname> — admin sets someone's nickname
                if (_mainVm.SelectedConversation is ChannelItemViewModel pnickCh)
                {
                    if (args.Length >= 2)
                        _places?.SetNickname(pnickCh.PlaceId, args[0], string.Join(" ", args[1..]), _chat);
                    else
                        _places?.SetNickname(pnickCh.PlaceId, _auth?.Profile?.UserId ?? "", args[0], _chat);
                }
                else _mainVm.AddSystemMessage("Select a place channel first.");
                break;

            case "ppins":
                // /ppins — show pinned messages in current channel
                if (_mainVm.SelectedConversation is ChannelItemViewModel ppinsCh)
                {
                    var pins = _places?.GetPins(ppinsCh.PlaceId, ppinsCh.ChannelId);
                    if (pins is null || pins.Count == 0)
                        _mainVm.AddSystemMessage("No pinned messages.");
                    else
                    {
                        _mainVm.AddSystemMessage($"--- {pins.Count} pinned message(s) ---");
                        foreach (var pin in pins)
                            _mainVm.AddSystemMessage($"[{pin.Author}]: {pin.Preview}");
                    }
                }
                else _mainVm.AddSystemMessage("Select a place channel first.");
                break;

            case "call" when args.Length >= 1:
            {
                if (_call is null) { _mainVm.AddSystemMessage("Call service not initialized."); break; }
                var callTarget = args[0];
                if (!IsValidUserId(callTarget)) { _mainVm.AddSystemMessage("Invalid user ID format."); break; }
                _callVm.StartOutgoingCall(callTarget);
                break;
            }

            case "call":
                // /call with no args — start a group call in the current place channel or group
                StartGroupCallForCurrentScope();
                break;

            case "hangup":
                // Group call window open? Close it. Otherwise fall through to 1:1 hangup.
                if (_groupCallWindow is not null)
                {
                    _groupCallWindow.Close();
                    break;
                }
                _call?.HangUp();
                break;

            case "mute":
                if (_call is not null)
                {
                    var newMute = !_call.IsMuted;
                    _call.SetMuted(newMute);
                    _callVm.IsMuted = newMute;
                    _mainVm.AddSystemMessage(newMute ? "Muted" : "Unmuted");
                }
                break;

            case "settings" or "key":
                ShowSettings();
                break;

            case "placesettings" when args.Length >= 1:
                ShowPlaceSettings(args[0]);
                break;

            case "discord" when args.Length >= 2:
                _ = ImportDiscordAsync(args[0], args[1]);
                break;

            case "discord":
                _mainVm.AddSystemMessage("Usage: /discord <bot-token> <guild-id>");
                break;

            case "help":
                _mainVm.AddSystemMessage("Commands: /add <id>, /remove <id>, /confirm <id>, /fingerprint [id], /group <name>, /ginvite <gid> <uid>, /kick <gid> <uid>, /ttl <days>, /link, /devices, /call <id>, /hangup, /mute, /settings, /place <name>, /pchannel <place> <name>, /pinvite <place> <uid>, /pkick <place> <uid>, /pban <place> <uid> [reason], /punban <place> <uid>, /prole <place> <uid> <admin|member>, /ptopic <place> <chId> <text>, /pcategory <place> <name>, /pcategoryrm <place> <name>, /pleave <place>, /prekey <place>, /discord <token> <guild-id>");
                break;

            default:
                _mainVm.AddSystemMessage($"Unknown command: /{cmd}");
                break;
        }
    }

    // Track avatar/icon data to avoid redundant bitmap reloads
    private readonly Dictionary<string, string?> _avatarDataCache = new();
    private readonly Dictionary<string, string?> _iconDataCache = new();
    // O(1) index for contact VMs by userId
    private readonly Dictionary<string, ContactItemViewModel> _contactIndex = new();

    private void RefreshContacts()
    {
        var contacts = _contacts?.GetContacts();
        if (contacts is null) return;

        var existingIds = new HashSet<string>();

        // Update existing + add new
        foreach (var (id, c) in contacts)
        {
            existingIds.Add(id);
            if (_contactIndex.TryGetValue(id, out var existing))
            {
                // Differential update — only set changed properties
                var newName = SanitizeDisplayString(c.DisplayName ?? id, 64);
                if (existing.DisplayName != newName) existing.DisplayName = newName;
                var newColor = string.IsNullOrEmpty(c.AccentColor) ? DefaultAccent() : c.AccentColor;
                if (existing.AccentColor != newColor) existing.AccentColor = newColor;
                if (existing.Status != (c.Status ?? "offline")) existing.Status = c.Status ?? "offline";
                if (existing.CustomStatus != c.CustomStatus) existing.CustomStatus = c.CustomStatus;
                // Reload avatar only if data changed
                _avatarDataCache.TryGetValue(id, out var cachedAvatar);
                if (cachedAvatar != c.AvatarData)
                {
                    existing.LoadAvatar(c.AvatarData);
                    _avatarDataCache[id] = c.AvatarData;
                }
            }
            else
            {
                var contactVm = new ContactItemViewModel
                {
                    UserId = id,
                    DisplayName = SanitizeDisplayString(c.DisplayName ?? id, 64),
                    AccentColor = string.IsNullOrEmpty(c.AccentColor) ? DefaultAccent() : c.AccentColor,
                    Status = c.Status ?? "offline",
                    CustomStatus = c.CustomStatus,
                };
                contactVm.LoadAvatar(c.AvatarData);
                _avatarDataCache[id] = c.AvatarData;
                _contactIndex[id] = contactVm;
                _mainVm.Contacts.Add(contactVm);
            }
        }

        // Remove deleted contacts
        for (int i = _mainVm.Contacts.Count - 1; i >= 0; i--)
        {
            var uid = _mainVm.Contacts[i].UserId;
            if (!existingIds.Contains(uid))
            {
                _mainVm.Contacts.RemoveAt(i);
                _contactIndex.Remove(uid);
                _avatarDataCache.Remove(uid);
            }
        }
    }

    private void RefreshGroups()
    {
        var groups = _groups?.GetGroups();
        if (groups is null) return;

        // Deselect if current group no longer exists
        if (_mainVm.SelectedConversation is GroupItemViewModel selectedGroup
            && !groups.ContainsKey(selectedGroup.GroupId))
        {
            _mainVm.DeselectConversation();
        }

        var existingIds = new HashSet<string>();
        foreach (var (id, g) in groups)
        {
            existingIds.Add(id);
            var existing = _mainVm.Groups.FirstOrDefault(x => x.GroupId == id);
            if (existing is not null)
            {
                var newName = SanitizeDisplayString(g.Name, 64);
                if (existing.Name != newName) existing.Name = newName;
                var mc = g.Members?.Count ?? 0;
                if (existing.MemberCount != mc) existing.MemberCount = mc;
            }
            else
            {
                _mainVm.Groups.Add(new GroupItemViewModel
                {
                    GroupId = id,
                    Name = SanitizeDisplayString(g.Name, 64),
                    MemberCount = g.Members?.Count ?? 0,
                });
            }
        }
        for (int i = _mainVm.Groups.Count - 1; i >= 0; i--)
            if (!existingIds.Contains(_mainVm.Groups[i].GroupId))
                _mainVm.Groups.RemoveAt(i);
    }

    private void RefreshPlaces()
    {
        var places = _places?.GetPlaces();
        if (places is null) return;

        // Deselect if current channel's place no longer exists
        if (_mainVm.SelectedConversation is ChannelItemViewModel selectedChannel
            && !places.ContainsKey(selectedChannel.PlaceId))
        {
            _mainVm.DeselectConversation();
        }

        var existingIds = new HashSet<string>();
        foreach (var (id, p) in places)
        {
            existingIds.Add(id);
            var isCreator = p.CreatorId == _auth?.Profile?.UserId;
            var isAdmin = isCreator || (p.Roles.TryGetValue(_auth?.Profile?.UserId ?? "", out var myRole) && myRole >= Rede.Core.Storage.PlaceRole.Admin);
            var placeName = SanitizeDisplayString(p.Name, 64);

            var existing = _mainVm.Places.FirstOrDefault(x => x.PlaceId == id);
            if (existing is not null)
            {
                // Differential update
                if (existing.Name != placeName) existing.Name = placeName;
                var mc = p.Members?.Count ?? 0;
                if (existing.MemberCount != mc) existing.MemberCount = mc;
                if (existing.IsCreator != isCreator) existing.IsCreator = isCreator;
                if (existing.IsAdmin != isAdmin) existing.IsAdmin = isAdmin;
                var ac = string.IsNullOrEmpty(p.AccentColor) ? DefaultAccent() : p.AccentColor;
                if (existing.AccentColor != ac) existing.AccentColor = ac;
                if (existing.OwnerColor != p.OwnerColor) existing.OwnerColor = p.OwnerColor;
                if (existing.AdminColor != p.AdminColor) existing.AdminColor = p.AdminColor;
                if (existing.MemberColor != p.MemberColor) existing.MemberColor = p.MemberColor;

                // Reload icon only if data changed
                _iconDataCache.TryGetValue(id, out var cachedIcon);
                if (cachedIcon != p.IconData)
                {
                    existing.LoadIcon(p.IconData);
                    _iconDataCache[id] = p.IconData;
                }

                // Update channels differentially
                var sortedChannels = p.Channels
                    .OrderBy(kv => kv.Value.Category ?? "")
                    .ThenBy(kv => kv.Value.Position)
                    .ThenBy(kv => kv.Value.CreatedAt)
                    .ToList();

                var existingChIds = new HashSet<string>();
                foreach (var (chId, ch) in sortedChannels)
                {
                    existingChIds.Add(chId);
                    var existingCh = existing.Channels.FirstOrDefault(x => x.ChannelId == chId);
                    if (existingCh is not null)
                    {
                        var chName = SanitizeDisplayString(ch.Name, 64);
                        if (existingCh.Name != chName) existingCh.Name = chName;
                        if (existingCh.Topic != (ch.Topic ?? "")) existingCh.Topic = ch.Topic ?? "";
                        if (existingCh.Category != ch.Category) existingCh.Category = ch.Category;
                        if (existingCh.IsCreator != isCreator) existingCh.IsCreator = isCreator;
                    }
                    else
                    {
                        existing.Channels.Add(new ChannelItemViewModel
                        {
                            PlaceId = id, ChannelId = chId,
                            Name = SanitizeDisplayString(ch.Name, 64),
                            PlaceName = placeName, IsCreator = isCreator,
                            Category = ch.Category, Topic = ch.Topic ?? "",
                        });
                    }
                }
                for (int i = existing.Channels.Count - 1; i >= 0; i--)
                    if (!existingChIds.Contains(existing.Channels[i].ChannelId))
                        existing.Channels.RemoveAt(i);

                existing.CategoryOrder = p.Categories.ToList();
                existing.RebuildChannelTree();
            }
            else
            {
                // New place — full create
                var channels = new System.Collections.ObjectModel.ObservableCollection<ChannelItemViewModel>();
                var sortedChannels = p.Channels
                    .OrderBy(kv => kv.Value.Category ?? "")
                    .ThenBy(kv => kv.Value.Position)
                    .ThenBy(kv => kv.Value.CreatedAt);
                foreach (var (chId, ch) in sortedChannels)
                {
                    channels.Add(new ChannelItemViewModel
                    {
                        PlaceId = id, ChannelId = chId,
                        Name = SanitizeDisplayString(ch.Name, 64),
                        PlaceName = placeName, IsCreator = isCreator,
                        Category = ch.Category, Topic = ch.Topic ?? "",
                    });
                }
                var placeVm = new PlaceItemViewModel
                {
                    PlaceId = id, Name = placeName,
                    MemberCount = p.Members?.Count ?? 0,
                    IsCreator = isCreator, IsAdmin = isAdmin,
                    Channels = channels,
                    AccentColor = string.IsNullOrEmpty(p.AccentColor) ? DefaultAccent() : p.AccentColor,
                    OwnerColor = p.OwnerColor, AdminColor = p.AdminColor,
                    MemberColor = p.MemberColor,
                };
                placeVm.CategoryOrder = p.Categories.ToList();
                placeVm.RebuildChannelTree();
                placeVm.LoadIcon(p.IconData);
                _iconDataCache[id] = p.IconData;
                _mainVm.Places.Add(placeVm);
            }
        }
        for (int i = _mainVm.Places.Count - 1; i >= 0; i--)
        {
            var pid = _mainVm.Places[i].PlaceId;
            if (!existingIds.Contains(pid))
            {
                _mainVm.Places.RemoveAt(i);
                _iconDataCache.Remove(pid);
            }
        }
    }

    private void LoadMemberList(string placeId)
    {
        if (_auth?.Profile is null) return;
        if (!_auth.Profile.Places.TryGetValue(placeId, out var place)) return;

        _mainVm.MemberList.Clear();
        foreach (var memberId in place.Members)
        {
            var role = "Member";
            var roleColor = place.MemberColor;
            if (memberId == place.CreatorId)
            {
                role = "Owner";
                roleColor = place.OwnerColor;
            }
            else if (place.Roles.TryGetValue(memberId, out var r) && r >= Rede.Core.Storage.PlaceRole.Admin)
            {
                role = "Admin";
                roleColor = place.AdminColor;
            }

            var displayName = memberId;
            var accentColor = "#8b5cf6";
            var status = "offline";
            if (_auth.Profile.Contacts.TryGetValue(memberId, out var contact))
            {
                displayName = contact.DisplayName ?? memberId;
                accentColor = string.IsNullOrEmpty(contact.AccentColor) ? DefaultAccent() : contact.AccentColor;
                status = contact.Status ?? "offline";
            }
            else if (memberId == _auth.Profile.UserId)
            {
                displayName = _auth.Profile.DisplayName;
                accentColor = _auth.Profile.AccentColor ?? "#8b5cf6";
                status = _auth.Profile.Status ?? "online";
            }

            _mainVm.MemberList.Add(new PlaceMemberViewModel
            {
                UserId = memberId,
                DisplayName = SanitizeDisplayString(displayName, 64),
                Role = role,
                RoleColor = roleColor,
                Status = status,
                AccentColor = accentColor,
            });
        }
    }

    private (string? Role, string Color) GetSenderRoleInfo(string placeId, string senderId)
    {
        if (_auth?.Profile is null) return (null, "#6b7280");
        if (!_auth.Profile.Places.TryGetValue(placeId, out var place)) return (null, "#6b7280");

        // Use custom roles if available
        if (place.CustomRoles.Count > 0)
        {
            var (roleName, roleColor, _) = PlaceService.GetHighestRole(place, senderId);
            return roleName == "Member" ? (null, roleColor) : (roleName, roleColor);
        }

        // Legacy 3-tier fallback
        if (senderId == place.CreatorId) return ("Owner", place.OwnerColor);
        if (place.Roles.TryGetValue(senderId, out var role) && role >= Rede.Core.Storage.PlaceRole.Admin)
            return ("Admin", place.AdminColor);
        return (null, place.MemberColor);
    }

    private string? FindPlaceId(string nameOrId)
    {
        var places = _places?.GetPlaces();
        if (places is null) return null;
        foreach (var (pid, p) in places)
        {
            if (p.Name == nameOrId || pid == nameOrId) return pid;
        }
        return null;
    }

    private void MarkChannelUnread(string placeId, string channelId)
    {
        foreach (var place in _mainVm.Places)
        {
            if (place.PlaceId != placeId) continue;
            var ch = place.Channels.FirstOrDefault(c => c.ChannelId == channelId);
            if (ch is not null && _mainVm.SelectedConversation != ch)
            {
                ch.HasUnread = true;
                place.HasUnread = true;
            }
            break;
        }
    }

    private string? FindGroupId(string nameOrId)
    {
        var groups = _groups?.GetGroups();
        if (groups is null) return null;
        foreach (var (gid, g) in groups)
        {
            if (g.Name == nameOrId || gid == nameOrId) return gid;
        }
        return null;
    }

    private void LoadChatHistoryForConversation(string chatId)
    {
        if (_auth?.Profile is null) return;

        if (_auth.Profile.ChatHistory.TryGetValue(chatId, out var messages))
        {
            foreach (var msg in messages)
            {
                var isOwn = msg.From == _auth.Profile.UserId;
                var ts = DateTimeOffset.FromUnixTimeMilliseconds(msg.Ts).LocalDateTime;

                // Look up sender profile for avatar/color
                var contactVm = _mainVm.Contacts.FirstOrDefault(c => c.UserId == msg.From);
                var accentColor = isOwn
                    ? (_auth.Profile.AccentColor ?? "#8b5cf6")
                    : (!string.IsNullOrEmpty(contactVm?.AccentColor) ? contactVm!.AccentColor : DefaultAccent());
                var initial = contactVm?.Initial
                    ?? (string.IsNullOrEmpty(msg.From) ? "?" : msg.From[..1].ToUpperInvariant());

                // Resolve sender role for place channels
                string? senderRole = null;
                string roleBadgeColor = "#8b5cf6";
                if (_mainVm.SelectedConversation is ChannelItemViewModel selChan)
                {
                    (senderRole, roleBadgeColor) = GetSenderRoleInfo(selChan.PlaceId, msg.From);
                }

                // Resolve nickname for places
                var displayFrom = msg.From;
                if (_mainVm.SelectedConversation is ChannelItemViewModel nickChan)
                {
                    var nick = _places?.GetNickname(nickChan.PlaceId, msg.From);
                    if (nick is not null) displayFrom = nick;
                }

                _mainVm.Messages.Add(new ChatMessageViewModel
                {
                    From = displayFrom,
                    Text = msg.Text,
                    IsOwn = isOwn,
                    Timestamp = ts,
                    Ttl = msg.Ttl,
                    SenderAccentColor = accentColor,
                    SenderInitial = initial,
                    SenderAvatar = contactVm?.AvatarImage,
                    HasSenderAvatar = contactVm?.HasAvatar ?? false,
                    SenderRole = senderRole,
                    RoleBadgeColor = roleBadgeColor,
                    MsgId = msg.MsgId,
                    ReplyToPreview = msg.ReplyToPreview,
                    ReplyToAuthor = msg.ReplyToAuthor,
                    IsEdited = msg.EditedAt.HasValue,
                    IsDeleted = msg.IsDeleted,
                });
                // Load reactions from stored message
                if (msg.Reactions is { Count: > 0 })
                    _mainVm.Messages[^1].UpdateReactions(msg.Reactions, _auth?.Profile?.UserId);
                // Load attachments
                if (msg.Attachments is { Count: > 0 })
                    LoadAttachmentsForMessage(_mainVm.Messages[^1], msg.Attachments);
            }
        }

        // Clear unread indicator
        if (_mainVm.SelectedConversation is ContactItemViewModel contact)
            contact.HasUnread = false;
        else if (_mainVm.SelectedConversation is GroupItemViewModel group)
            group.HasUnread = false;
        else if (_mainVm.SelectedConversation is ChannelItemViewModel ch)
            ch.HasUnread = false;
    }

    private void LoadAttachmentsForMessage(ChatMessageViewModel msgVm, List<Rede.Core.Storage.AttachmentInfo> attachments)
    {
        foreach (var att in attachments)
        {
            var attVm = new AttachmentViewModel
            {
                Name = att.Name,
                BlobId = att.BlobId,
                SizeDisplay = FormatFileSize(att.Size),
                IsImage = BlobService.IsImage(att),
            };
            msgVm.Attachments.Add(attVm);

            // Lazy-load image preview. Both the blob fetch AND the bitmap decode
            // run off the UI thread — decoding a multi-MB image on the UI thread
            // froze the chat for a noticeable beat. Only the assignment is posted.
            if (attVm.IsImage && _blobs is not null)
            {
                var blobService = _blobs;
                var attInfo = att;
                _ = Task.Run(async () =>
                {
                    var data = await blobService.FetchAsync(attInfo);
                    Avalonia.Media.Imaging.Bitmap? bmp = null;
                    if (data is not null)
                    {
                        try
                        {
                            using var ms = new System.IO.MemoryStream(data);
                            bmp = new Avalonia.Media.Imaging.Bitmap(ms);
                        }
                        catch { bmp = null; }
                    }
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (bmp is not null)
                        {
                            attVm.Preview = bmp;
                            attVm.HasPreview = true;
                        }
                        else
                        {
                            // Fetch or decode failed — drop back to the file chip.
                            attVm.LoadFailed = true;
                        }
                    });
                });
            }
        }
        msgVm.HasAttachments = msgVm.Attachments.Count > 0;
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    private void MarkContactUnread(string userId)
    {
        var item = _mainVm.Contacts.FirstOrDefault(c => c.UserId == userId);
        if (item is not null && _mainVm.SelectedConversation != item)
            item.HasUnread = true;
    }

    private void MarkGroupUnread(string groupId)
    {
        var item = _mainVm.Groups.FirstOrDefault(g => g.GroupId == groupId);
        if (item is not null && _mainVm.SelectedConversation != item)
            item.HasUnread = true;
    }

    private void ShowSettings()
    {
        if (_auth?.Profile is null) return;
        var p = _auth.Profile;
        var vm = new SettingsViewModel
        {
            UserId = p.UserId,
            DisplayName = p.DisplayName,
            DeviceId = p.DeviceId,
            Fingerprint = Rede.Core.Crypto.CryptoService.Fingerprint(p.PublicKey),
            PublicKey = Convert.ToBase64String(p.PublicKey),
            CallTransport = _call?.LocalMode.ToString() ?? _conn.Transport,
            AccentColor = p.AccentColor ?? "#8b5cf6",
            AvatarInitial = string.IsNullOrEmpty(p.DisplayName) ? "?" : p.DisplayName[..1].ToUpperInvariant(),
            SelectedStatus = p.Status ?? "online",
            CustomStatusText = p.CustomStatus ?? "",
            ThemeVariant = p.ThemeVariant ?? "dark",
        };
        vm.LoadAvatarFromBase64(p.AvatarData, p.AvatarMimeType);

        // Status changes — send to server immediately (live)
        vm.OnStatusChanged += () =>
        {
            if (_auth?.Profile is not null && _auth.Passphrase is not null)
            {
                _auth.Profile.Status = vm.SelectedStatus;
                _auth.Profile.CustomStatus = string.IsNullOrWhiteSpace(vm.CustomStatusText) ? null : vm.CustomStatusText;
                _notifications.OwnStatus = vm.SelectedStatus;
                _store.SaveProfileDebounced(_auth.Profile, _auth.Passphrase);
                _conn?.Send(Rede.Core.Protocol.Msg.StatusUpdate, new System.Text.Json.Nodes.JsonObject
                {
                    ["status"] = vm.SelectedStatus,
                    ["customStatus"] = _auth.Profile.CustomStatus,
                });
                UpdateOwnProfilePanel();
            }
        };

        // Notification settings
        vm.NotificationsEnabled = _auth.Profile.NotificationsEnabled;
        vm.NotificationShowContent = _auth.Profile.NotificationShowContent;
        vm.NotificationSoundEnabled = _auth.Profile.NotificationSoundEnabled;
        vm.OnNotificationSettingsChanged += () =>
        {
            if (_auth?.Profile is not null && _auth.Passphrase is not null)
            {
                _auth.Profile.NotificationsEnabled = vm.NotificationsEnabled;
                _auth.Profile.NotificationShowContent = vm.NotificationShowContent;
                _auth.Profile.NotificationSoundEnabled = vm.NotificationSoundEnabled;
                _notifications.Enabled = vm.NotificationsEnabled;
                _notifications.ShowContent = vm.NotificationShowContent;
                _notifications.SoundEnabled = vm.NotificationSoundEnabled;
                _store.SaveProfileDebounced(_auth.Profile, _auth.Passphrase);
            }
        };

        // System integration (tray + autostart)
        vm.IsAutostartSupported = Rede.Core.Services.AutostartService.IsSupported;
        vm.MinimizeToTray = _auth.Profile.MinimizeToTray;
        // Reconcile stored preference with actual OS state — user may have removed the
        // autostart entry outside of Rede.
        vm.AutostartEnabled = vm.IsAutostartSupported && Rede.Core.Services.AutostartService.IsEnabled();
        vm.StartMinimized = _auth.Profile.StartMinimized;
        _auth.Profile.Autostart = vm.AutostartEnabled;

        vm.OnSystemSettingsChanged += () =>
        {
            if (_auth?.Profile is null || _auth.Passphrase is null) return;

            _auth.Profile.MinimizeToTray = vm.MinimizeToTray;
            _auth.Profile.StartMinimized = vm.StartMinimized;

            // Apply autostart state change to the OS.
            if (vm.AutostartEnabled != _auth.Profile.Autostart ||
                (vm.AutostartEnabled && Rede.Core.Services.AutostartService.IsEnabled() == false))
            {
                bool ok;
                if (vm.AutostartEnabled)
                    ok = Rede.Core.Services.AutostartService.Enable(vm.StartMinimized);
                else
                    ok = Rede.Core.Services.AutostartService.Disable();

                if (!ok)
                {
                    _mainVm.AddSystemMessage("Failed to update OS autostart entry.");
                    // Revert VM to actual OS state
                    vm.AutostartEnabled = Rede.Core.Services.AutostartService.IsEnabled();
                }
                _auth.Profile.Autostart = vm.AutostartEnabled;
            }
            else if (vm.AutostartEnabled)
            {
                // Autostart unchanged but StartMinimized may have flipped — rewrite entry
                // so the launch args reflect the new preference.
                Rede.Core.Services.AutostartService.Enable(vm.StartMinimized);
            }

            _store.SaveProfileDebounced(_auth.Profile, _auth.Passphrase);
        };

        // Theme variant — live-apply is done in ViewModel; persist here
        vm.OnThemeChanged += () =>
        {
            if (_auth?.Profile is not null && _auth.Passphrase is not null)
            {
                _auth.Profile.ThemeVariant = vm.ThemeVariant;
                _store.SaveProfileDebounced(_auth.Profile, _auth.Passphrase);
            }
        };

        // Passphrase change
        vm.OnChangePassphraseRequested += async (currentPass, newPass) =>
        {
            if (_auth?.Profile is null || _auth.Passphrase is null) return false;

            // Verify the current passphrase matches the active one
            if (currentPass.Length != _auth.Passphrase.Length ||
                !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(currentPass, _auth.Passphrase))
                return false;

            // Re-encrypt everything with the new passphrase
            await _store.ChangePassphraseAsync(_auth.Profile, _auth.Passphrase, newPass);

            // Update the in-memory passphrase: zero old, adopt new copy
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(_auth.Passphrase);
            _passphraseLock?.Dispose();

            // Clone new passphrase (caller may zero their copy) and mlock it
            var owned = (byte[])newPass.Clone();
            _auth.Passphrase = owned;
            _passphraseLock = Rede.Core.Crypto.SecureMemory.Lock(owned);

            // Propagate to all services that hold a reference
            PropagateProfile();

            return true;
        };

        // Start a standalone audio monitor for the level meter (works without an active call)
        Rede.Core.Audio.AudioEngine? settingsMonitor = null;
        try
        {
            settingsMonitor = new Rede.Core.Audio.AudioEngine();
            settingsMonitor.InputVolume = (float)(vm.InputVolume / 100.0);
            settingsMonitor.NoiseSuppression = vm.NoiseSuppression;

            // Apply selected input device
            var monDevices = Rede.Core.Audio.AudioEngine.GetDevices();
            var monInputDevs = monDevices.Where(d => d.IsInput).ToList();
            if (p.InputDeviceName is not null)
            {
                var idx = monInputDevs.FindIndex(d => d.Name == p.InputDeviceName);
                if (idx >= 0) settingsMonitor.SelectedInputDevice = monInputDevs[idx].Index;
            }

            settingsMonitor.StartMonitor();
        }
        catch { settingsMonitor = null; }

        // Live input level meter: poll audio engine every 50ms while settings is open
        var levelTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        levelTimer.Tick += (_, _) =>
        {
            if (settingsMonitor is not null)
                vm.CurrentInputLevelDb = settingsMonitor.CurrentInputLevelDb;
            else if (_call?.Audio is not null)
                vm.CurrentInputLevelDb = _call.Audio.CurrentInputLevelDb;
        };
        levelTimer.Start();

        vm.OnBackRequested += () =>
        {
            levelTimer.Stop();
            settingsMonitor?.StopMonitor();
            settingsMonitor?.Dispose();
            settingsMonitor = null;
            // H5: Re-wire retry handler when returning from settings
            RootContent.Content = CreateMainView();
        };

        // Populate audio device lists
        try
        {
            var devices = Rede.Core.Audio.AudioEngine.GetDevices();
            var inputDevs = devices.Where(d => d.IsInput).ToList();
            var outputDevs = devices.Where(d => d.IsOutput).ToList();

            vm.InputDevices.Clear();
            vm.InputDevices.Add("System Default");
            foreach (var d in inputDevs) vm.InputDevices.Add(d.Name);

            vm.OutputDevices.Clear();
            vm.OutputDevices.Add("System Default");
            foreach (var d in outputDevs) vm.OutputDevices.Add(d.Name);

            // Select saved device by name
            if (p.InputDeviceName is not null)
            {
                var idx = inputDevs.FindIndex(d => d.Name == p.InputDeviceName);
                vm.SelectedInputDeviceIndex = idx >= 0 ? idx + 1 : 0; // +1 for "System Default"
            }
            if (p.OutputDeviceName is not null)
            {
                var idx = outputDevs.FindIndex(d => d.Name == p.OutputDeviceName);
                vm.SelectedOutputDeviceIndex = idx >= 0 ? idx + 1 : 0;
            }

            // Load saved volume/gate (profile stores 0-2 float, UI uses 0-200 percentage)
            vm.InputVolume = p.InputVolume * 100;
            vm.OutputVolume = p.OutputVolume * 100;
            // Convert linear threshold (0-1) to dB (-100..0) for UI
            vm.NoiseGateThreshold = p.NoiseGateThreshold > 0
                ? Math.Max(-100.0, 20.0 * Math.Log10(p.NoiseGateThreshold))
                : -100.0;
            vm.NoiseSuppression = p.NoiseSuppression;
            vm.AutoInputSensitivity = p.AutoInputSensitivity;
            vm.AutoGainControl = p.AutoGainControl;
            vm.EchoCancellation = p.EchoCancellation;
            vm.IsNoiseSuppressionAvailable = Rede.Core.Audio.AudioEngine.IsNoiseSuppressionAvailable;
        }
        catch { /* PortAudio not available */ }

        vm.OnInstallRnnoise = async () =>
        {
            vm.IsRnnoiseInstalling = true;
            vm.RnnoiseInstallStatus = "Downloading...";
            try
            {
                var libsDir = Rede.Core.Audio.RNNoise.LibsDirectory;
                Directory.CreateDirectory(libsDir);
                var libFile = Rede.Core.Audio.RNNoise.LibFileName;
                var destPath = Path.Combine(libsDir, libFile);

                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(60);
                // GitHub's /latest/ only considers non-prerelease — use API to find actual latest
                http.DefaultRequestHeaders.UserAgent.ParseAdd("REDE-Updater");
                var apiUrl = "https://api.github.com/repos/caaatto/rede/releases";
                var releasesJson = await http.GetStringAsync(apiUrl);
                string? downloadUrl = null;
                string? releaseTag = null;
                using (var doc = System.Text.Json.JsonDocument.Parse(releasesJson))
                {
                    foreach (var release in doc.RootElement.EnumerateArray())
                    {
                        if (release.TryGetProperty("assets", out var assets))
                        {
                            foreach (var asset in assets.EnumerateArray())
                            {
                                if (asset.GetProperty("name").GetString() == libFile &&
                                    asset.TryGetProperty("browser_download_url", out var urlProp))
                                {
                                    downloadUrl = urlProp.GetString();
                                    releaseTag = release.GetProperty("tag_name").GetString();
                                    break;
                                }
                            }
                        }
                        if (downloadUrl is not null) break;
                    }
                }
                if (downloadUrl is null || releaseTag is null)
                    throw new Exception($"Download for {libFile} isn't available yet.");
                var bytes = await http.GetByteArrayAsync(downloadUrl);

                // RNNoise is a native library loaded into our process — a tampered blob
                // executes arbitrary code with access to the unlocked profile + microphone.
                // Verify Ed25519 signature + SHA256SUMS via the same path as binary updates
                // before writing anything to disk.
                string? verifyErr = null;
                var verified = await Rede.Core.Services.UpdateService.VerifyReleaseAssetAsync(
                    releaseTag, libFile, bytes, s => verifyErr = s);
                if (!verified)
                    throw new Exception(verifyErr ?? "Signature or hash check failed; not installing.");

                await File.WriteAllBytesAsync(destPath, bytes);

                Rede.Core.Audio.RNNoise.TryReload();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    vm.IsNoiseSuppressionAvailable = Rede.Core.Audio.RNNoise.IsAvailable;
                    vm.IsRnnoiseInstalling = false;
                    vm.RnnoiseInstallStatus = Rede.Core.Audio.RNNoise.IsAvailable
                        ? "Installed!"
                        : "Downloaded but failed to load";
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    vm.IsRnnoiseInstalling = false;
                    vm.RnnoiseInstallStatus = $"Failed: {ex.Message}";
                });
            }
        };

        // --- Security keys (FIDO2) ---
        void RefreshFidoKeys()
        {
            var hash = Rede.Core.Crypto.Fido2.Fido2SidecarStore.HashForUserId(p.UserId);
            var keys = _fido.ListKeys(hash).Select(k => new SettingsViewModel.Fido2KeyItem
            {
                Name = k.Name,
                CredentialId = k.CredentialId,
                Added = "Added " + DateTimeOffset.FromUnixTimeMilliseconds(k.AddedAt).LocalDateTime.ToString("yyyy-MM-dd"),
            }).ToList();
            vm.SetSecurityKeys(keys, _fido.HasRecovery(hash), _fido.BackendAvailable);
            vm.FidoDiagnostics = _fidoAuth.DescribeBackend();
        }
        RefreshFidoKeys();

        vm.OnInstallFido2 = async () =>
        {
            vm.IsFidoBusy = true;
            vm.FidoStatus = "Downloading security-key support…";
            try
            {
                var libsDir = Rede.Core.Crypto.Fido2.LibFido2Authenticator.LibsDirectory;
                Directory.CreateDirectory(libsDir);
                var libFile = Rede.Core.Crypto.Fido2.LibFido2Authenticator.LibFileName;
                var destPath = Path.Combine(libsDir, libFile);

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("REDE-Updater");
                var releasesJson = await http.GetStringAsync("https://api.github.com/repos/caaatto/rede/releases");
                string? downloadUrl = null, releaseTag = null;
                using (var doc = System.Text.Json.JsonDocument.Parse(releasesJson))
                {
                    foreach (var release in doc.RootElement.EnumerateArray())
                    {
                        if (release.TryGetProperty("assets", out var assets))
                        {
                            foreach (var asset in assets.EnumerateArray())
                            {
                                if (asset.GetProperty("name").GetString() == libFile &&
                                    asset.TryGetProperty("browser_download_url", out var urlProp))
                                {
                                    downloadUrl = urlProp.GetString();
                                    releaseTag = release.GetProperty("tag_name").GetString();
                                    break;
                                }
                            }
                        }
                        if (downloadUrl is not null) break;
                    }
                }
                if (downloadUrl is null || releaseTag is null)
                    throw new Exception($"Download for {libFile} isn't available yet.");
                var bytes = await http.GetByteArrayAsync(downloadUrl);

                // libfido2 is native code loaded into our process with access to the unlocked
                // profile + the security key — verify Ed25519 sig + SHA256SUMS before writing.
                string? verifyErr = null;
                var verified = await Rede.Core.Services.UpdateService.VerifyReleaseAssetAsync(
                    releaseTag, libFile, bytes, s => verifyErr = s);
                if (!verified)
                    throw new Exception(verifyErr ?? "Signature or hash check failed; not installing.");

                await File.WriteAllBytesAsync(destPath, bytes);
                Rede.Core.Crypto.Fido2.LibFido2Authenticator.TryReload();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    vm.IsFidoBusy = false;
                    vm.FidoStatus = _fido.BackendAvailable ? "Installed!" : "Downloaded but failed to load";
                    RefreshFidoKeys();
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    vm.IsFidoBusy = false;
                    vm.FidoStatus = $"Failed: {ex.Message}";
                });
            }
        };

        vm.OnEnrollKeyRequested += async (keyName, pin, progress) =>
        {
            if (_auth?.Profile is null || _auth.Passphrase is null) return false;
            // Run the native ceremony off the UI thread (the OS security-key dialog blocks the
            // calling thread; doing it on the UI thread can prevent the dialog from showing).
            var profile = _auth.Profile;
            var pass = _auth.Passphrase;
            var cred = await Task.Run(() => _fido.EnrollKeyAsync(profile, pass, keyName, pin, progress));
            _lastFidoPin = pin;
            // Register the credential's public key with the server for login 2FA (best-effort —
            // local unlock works regardless of whether the server enrollment lands).
            if (cred.CredentialPublicKeyCose.Length == 64 && _conn is not null && _mainVm.IsConnected)
            {
                _conn.Send(Rede.Core.Protocol.Msg.Fido2Enroll, new System.Text.Json.Nodes.JsonObject
                {
                    ["credentialId"] = Convert.ToBase64String(cred.CredentialId),
                    ["publicKey"] = Convert.ToBase64String(cred.CredentialPublicKeyCose),
                });
            }
            Dispatcher.UIThread.Post(RefreshFidoKeys);
            return true;
        };

        vm.OnGenerateRecoveryRequested += () =>
        {
            if (_auth?.Profile is null) return Task.FromResult<string?>(null);
            var hash = Rede.Core.Crypto.Fido2.Fido2SidecarStore.HashForUserId(_auth.Profile.UserId);
            var code = _fido.GenerateRecovery(hash);
            Dispatcher.UIThread.Post(RefreshFidoKeys);
            return Task.FromResult<string?>(code);
        };

        vm.OnRemoveKeyRequested += async (credentialId) =>
        {
            if (_auth?.Profile is null || _auth.Passphrase is null) return;
            await _fido.RemoveKeyAsync(_auth.Profile, _auth.Passphrase, credentialId);
            // Also drop the server-side 2FA credential (best-effort).
            if (_conn is not null && _mainVm.IsConnected)
            {
                _conn.Send(Rede.Core.Protocol.Msg.Fido2Remove, new System.Text.Json.Nodes.JsonObject
                {
                    ["credentialId"] = credentialId,
                });
            }
            Dispatcher.UIThread.Post(RefreshFidoKeys);
        };

        // Cache device list to avoid re-enumerating on every slider change
        var cachedDevices = Rede.Core.Audio.AudioEngine.GetDevices();
        var cachedInputDevs = cachedDevices.Where(d => d.IsInput).ToList();
        var cachedOutputDevs = cachedDevices.Where(d => d.IsOutput).ToList();

        vm.OnAudioSettingsChanged += () =>
        {
            if (_auth?.Profile is not null && _auth.Passphrase is not null)
            {
                // Save device names (not indices — indices change across runs)
                var inIdx = vm.SelectedInputDeviceIndex - 1; // -1 for "System Default"
                var outIdx = vm.SelectedOutputDeviceIndex - 1;
                _auth.Profile.InputDeviceName = inIdx >= 0 && inIdx < cachedInputDevs.Count ? cachedInputDevs[inIdx].Name : null;
                _auth.Profile.OutputDeviceName = outIdx >= 0 && outIdx < cachedOutputDevs.Count ? cachedOutputDevs[outIdx].Name : null;

                // Convert UI percentage (0-200) to engine float (0-2)
                _auth.Profile.InputVolume = (float)(vm.InputVolume / 100.0);
                _auth.Profile.OutputVolume = (float)(vm.OutputVolume / 100.0);
                // Convert dB (-100..0) to linear (0-1) for engine
                _auth.Profile.NoiseGateThreshold = (float)Math.Pow(10.0, vm.NoiseGateThreshold / 20.0);
                _auth.Profile.NoiseSuppression = vm.NoiseSuppression;
                _auth.Profile.AutoInputSensitivity = vm.AutoInputSensitivity;
                _auth.Profile.AutoGainControl = vm.AutoGainControl;
                _auth.Profile.EchoCancellation = vm.EchoCancellation;

                _store.SaveProfileDebounced(_auth.Profile, _auth.Passphrase);

                // Apply to running audio engine
                if (_call?.Audio is not null)
                {
                    _call.Audio.InputVolume = _auth.Profile.InputVolume;
                    _call.Audio.OutputVolume = _auth.Profile.OutputVolume;
                    _call.Audio.NoiseGateThreshold = _auth.Profile.NoiseGateThreshold;
                    _call.Audio.NoiseSuppression = _auth.Profile.NoiseSuppression;
                    _call.Audio.AutoInputSensitivity = _auth.Profile.AutoInputSensitivity;
                    _call.Audio.AutoGainControl = _auth.Profile.AutoGainControl;
                    _call.Audio.EchoCancellation = _auth.Profile.EchoCancellation;

                    if (inIdx >= 0 && inIdx < cachedInputDevs.Count)
                        _call.Audio.SelectedInputDevice = cachedInputDevs[inIdx].Index;
                    else
                        _call.Audio.SelectedInputDevice = -1;

                    if (outIdx >= 0 && outIdx < cachedOutputDevs.Count)
                        _call.Audio.SelectedOutputDevice = cachedOutputDevs[outIdx].Index;
                    else
                        _call.Audio.SelectedOutputDevice = -1;
                }
            }
        };

        // Profile customization — save + broadcast only on Apply
        vm.OnProfileApplied += () =>
        {
            if (_auth?.Profile is not null && _auth.Passphrase is not null)
            {
                _auth.Profile.AccentColor = vm.AccentColor;
                _auth.Profile.AvatarData = vm.AvatarData;
                _auth.Profile.AvatarMimeType = vm.AvatarMimeType;
                _store.SaveProfileDebounced(_auth.Profile, _auth.Passphrase);
                _chat?.BroadcastProfile(vm.AccentColor, vm.AvatarData, vm.AvatarMimeType);
                // Live-swap the global accent brush so buttons/highlights
                // pick up the new color without a restart. Also refresh the
                // default accent on existing contact/place VMs that didn't
                // have a per-contact color set.
                Themes.ThemeService.ApplyAccent(vm.AccentColor);
                RefreshDefaultAccents();
                UpdateOwnProfilePanel();
            }
        };

        vm.OnAvatarPickRequested += async () =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Choose Avatar",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Images")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif" },
                        MimeTypes = new[] { "image/png", "image/jpeg", "image/gif" },
                    },
                },
            });

            if (files.Count == 0) return;

            var file = files[0];
            await using var stream = await file.OpenReadAsync();
            using var ms = new System.IO.MemoryStream();
            await stream.CopyToAsync(ms);
            var data = ms.ToArray();

            if (data.Length > 256 * 1024)
            {
                _mainVm.AddSystemMessage("Avatar too large (max 256KB).");
                return;
            }

            // M2: Validate extension — reject unknown formats instead of silent fallback
            var fileName = System.IO.Path.GetFileName(file.Name);
            var ext = fileName.Split('.').LastOrDefault()?.ToLowerInvariant();
            var mime = ext switch
            {
                "png" => "image/png",
                "gif" => "image/gif",
                "jpg" or "jpeg" => "image/jpeg",
                _ => (string?)null,
            };
            if (mime is null)
            {
                _mainVm.AddSystemMessage("Invalid avatar format. Use PNG, GIF, or JPEG.");
                return;
            }

            vm.SetAvatarFromBytes(data, mime);
        };

        var settingsView = new SettingsView { DataContext = vm };
        RootContent.Content = settingsView;
    }

    private void ShowPlaceSettings(string placeId)
    {
        if (_auth?.Profile is null) return;
        if (!_auth.Profile.Places.TryGetValue(placeId, out var place)) return;

        var vm = new PlaceSettingsViewModel();
        vm.LoadFromPlace(place, placeId, _auth.Profile.UserId);

        vm.OnBackRequested += () =>
        {
            RootContent.Content = CreateMainView();
        };

        vm.OnProfileChanged += (pid, color) =>
        {
            _places?.UpdatePlaceProfile(pid, color, null, null, _chat);
            _store?.SaveProfileDebounced(_auth.Profile, _auth.Passphrase);
        };

        vm.OnRoleColorsChanged += (pid, owner, admin, member) =>
        {
            _places?.UpdateRoleColors(pid, owner, admin, member, _chat);
            _store?.SaveProfileDebounced(_auth.Profile, _auth.Passphrase);
        };

        vm.OnCreateRole += (pid, name, color, perms) =>
        {
            _places?.CreateCustomRole(pid, name, color, perms, _chat);
            _store?.SaveProfileDebounced(_auth.Profile, _auth.Passphrase);
            // Refresh the view
            if (_auth.Profile.Places.TryGetValue(pid, out var p))
                vm.LoadFromPlace(p, pid, _auth.Profile.UserId);
        };

        vm.OnDeleteRole += (pid, roleId) =>
        {
            _places?.DeleteCustomRole(pid, roleId, _chat);
            _store?.SaveProfileDebounced(_auth.Profile, _auth.Passphrase);
            if (_auth.Profile.Places.TryGetValue(pid, out var p))
                vm.LoadFromPlace(p, pid, _auth.Profile.UserId);
        };

        vm.OnAssignRole += (pid, userId, roleId) =>
        {
            _places?.AssignRole(pid, userId, roleId, _chat);
            _store?.SaveProfileDebounced(_auth.Profile, _auth.Passphrase);
            if (_auth.Profile.Places.TryGetValue(pid, out var p))
                vm.LoadFromPlace(p, pid, _auth.Profile.UserId);
        };

        vm.OnRemoveRole += (pid, userId, roleId) =>
        {
            _places?.RemoveRole(pid, userId, roleId, _chat);
            _store?.SaveProfileDebounced(_auth.Profile, _auth.Passphrase);
            if (_auth.Profile.Places.TryGetValue(pid, out var p))
                vm.LoadFromPlace(p, pid, _auth.Profile.UserId);
        };

        vm.OnInitRoles += (pid) =>
        {
            if (_auth.Profile.Places.TryGetValue(pid, out var p))
            {
                _places?.InitializeDefaultRoles(p, p.CreatorId);
                _store?.SaveProfileDebounced(_auth.Profile, _auth.Passphrase);
                vm.LoadFromPlace(p, pid, _auth.Profile.UserId);
            }
        };

        vm.OnKickMember += (pid, userId) =>
        {
            _mainVm.ExecuteCommand("pkick", new[] { pid, userId });
        };

        vm.OnBanMember += (pid, userId, reason) =>
        {
            var args = string.IsNullOrEmpty(reason)
                ? new[] { pid, userId }
                : new[] { pid, userId, reason };
            _mainVm.ExecuteCommand("pban", args);
        };

        vm.OnUnbanMember += (pid, userId) =>
        {
            _mainVm.ExecuteCommand("punban", new[] { pid, userId });
            // Refresh after unban
            if (_auth.Profile.Places.TryGetValue(pid, out var p))
                vm.LoadFromPlace(p, pid, _auth.Profile.UserId);
        };

        vm.OnCreateChannel += (pid, name) =>
        {
            _mainVm.ExecuteCommand("pchannel", new[] { pid, name });
            // Refresh after short delay (metadata distribution is async)
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                await System.Threading.Tasks.Task.Delay(500);
                if (_auth.Profile.Places.TryGetValue(pid, out var p))
                    vm.LoadFromPlace(p, pid, _auth.Profile.UserId);
            });
        };

        vm.OnCreateCategory += (pid, name) =>
        {
            _mainVm.ExecuteCommand("pcategory", new[] { pid, name });
            if (_auth.Profile.Places.TryGetValue(pid, out var p))
                vm.LoadFromPlace(p, pid, _auth.Profile.UserId);
        };

        vm.OnDeleteChannel += (pid, chId) =>
        {
            _mainVm.ExecuteCommand("pchannelrm", new[] { pid, chId });
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                await System.Threading.Tasks.Task.Delay(500);
                if (_auth.Profile.Places.TryGetValue(pid, out var p))
                    vm.LoadFromPlace(p, pid, _auth.Profile.UserId);
            });
        };

        var psView = new PlaceSettingsView { DataContext = vm };
        RootContent.Content = psView;
    }

    private async void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        // Escape closes the image lightbox first — it's a modal overlay and
        // should swallow Escape before the chat-level handler interprets it
        // as "close reply / collapse sidebar".
        if (e.Key == Key.Escape && _mainVm.IsLightboxOpen)
        {
            _mainVm.CloseLightboxCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Q && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Ctrl+Q = real quit, bypass minimize-to-tray.
            if (_auth?.Profile is not null && _auth?.Passphrase is not null)
            {
                ExportNoncesToProfile();
                await _store.FlushAsync(_auth.Profile, _auth.Passphrase);
                _auth.Profile.ZeroSecrets();
                _fido.ClearSession();
                _lastFidoPin = null;
            }
            _conn?.Dispose();
            ForceQuit();
        }

        // Alt+↑/↓ — navigate channels within the currently selected place.
        if ((e.Key == Key.Up || e.Key == Key.Down) && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            if (NavigateChannelInPlace(e.Key == Key.Down ? 1 : -1))
                e.Handled = true;
        }

        // Ctrl+Tab / Ctrl+Shift+Tab — cycle through all conversations.
        if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var dir = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
            if (CycleConversation(dir))
                e.Handled = true;
        }

        // Ctrl+K — quick switcher overlay (Discord/Slack/Linear style).
        if (e.Key == Key.K && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (RootContent.Content is Views.MainView mv)
            {
                mv.FocusQuickSwitcher();
                e.Handled = true;
            }
        }

        // Ctrl+F — in-chat message search (only when a chat is open).
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (RootContent.Content is Views.MainView mv && _mainVm.SelectedConversation is not null)
            {
                mv.FocusMessageSearch();
                e.Handled = true;
            }
        }
    }

    private bool NavigateChannelInPlace(int delta)
    {
        if (_mainVm.SelectedConversation is not ChannelItemViewModel current) return false;
        var place = _mainVm.Places.FirstOrDefault(p => p.PlaceId == current.PlaceId);
        if (place is null || place.Channels.Count == 0) return false;
        var idx = -1;
        for (int i = 0; i < place.Channels.Count; i++)
            if (place.Channels[i].ChannelId == current.ChannelId) { idx = i; break; }
        if (idx < 0) return false;
        var next = (idx + delta + place.Channels.Count) % place.Channels.Count;
        if (next == idx) return false;
        _mainVm.SelectConversationCommand.Execute(place.Channels[next]);
        return true;
    }

    private bool CycleConversation(int delta)
    {
        var all = new List<object>();
        foreach (var c in _mainVm.Contacts) all.Add(c);
        foreach (var g in _mainVm.Groups) all.Add(g);
        foreach (var p in _mainVm.Places)
            foreach (var ch in p.Channels) all.Add(ch);
        if (all.Count == 0) return false;

        var cur = _mainVm.SelectedConversation;
        var idx = cur is null ? -1 : all.IndexOf(cur);
        var next = idx < 0
            ? (delta > 0 ? 0 : all.Count - 1)
            : (idx + delta + all.Count) % all.Count;
        _mainVm.SelectConversationCommand.Execute(all[next]);
        return true;
    }

    // K2: Strip file paths, stack traces, and internal details from error messages
    private static string SanitizeErrorMessage(Exception ex)
    {
        var msg = ex.Message;
        // Known user-facing messages — pass through
        if (msg.Contains("läuft nicht") || msg.Contains("nicht erreichbar") ||
            msg.Contains("Connection failed") || msg.Contains("Refusing unencrypted"))
            return msg;
        // If it looks like a raw exception, show generic message
        if (msg.Contains("Exception") || msg.Contains("StackTrace") || msg.Contains("   at "))
            return "Connection failed. Check your network and try again.";
        // Strip file paths (M6: more targeted regex)
        msg = System.Text.RegularExpressions.Regex.Replace(msg, @"[A-Za-z]:\\[^\s""']+", "[path]");
        msg = System.Text.RegularExpressions.Regex.Replace(msg, @"(?<!\w)/(?:home|usr|tmp|var|etc)/[\w./\-]+", "[path]");
        // Truncate
        if (msg.Length > 200) msg = msg[..200] + "...";
        return msg;
    }

    // H3: Sanitize server-provided messages — strip HTML, limit length, no URLs
    private static string SanitizeServerMessage(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return "Unknown error.";
        // Strip HTML tags
        var s = System.Text.RegularExpressions.Regex.Replace(msg, @"<[^>]+>", "");
        // Strip URLs that could be phishing
        s = System.Text.RegularExpressions.Regex.Replace(s, @"https?://\S+", "[link removed]");
        // Strip control chars
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[\x00-\x1f\x7f]", "");
        if (s.Length > 200) s = s[..200] + "...";
        return s;
    }

    // K3/H2: Sanitize display strings — strip control chars, limit length
    private static string SanitizeDisplayString(string input, int maxLength = 255)
    {
        if (string.IsNullOrEmpty(input)) return input;
        // M4: Strip control characters AND Unicode bidi overrides (anti-spoofing)
        var s = System.Text.RegularExpressions.Regex.Replace(input,
            @"[\x00-\x1f\x7f\u200E\u200F\u202A-\u202E\u2066-\u2069]", "");
        if (s.Length > maxLength) s = s[..maxLength];
        return s.Trim();
    }

    // Accept pending new devices for a contact after user confirmation
    private void AcceptPendingDevices(string userId)
    {
        var profile = _auth?.Profile;
        var passphrase = _auth?.Passphrase;
        if (profile is null || passphrase is null) return;

        if (!profile.Contacts.TryGetValue(userId, out var contact)) return;

        var prefix = $"{userId}:";
        var accepted = new System.Collections.Generic.List<string>();
        foreach (var (key, (publicKey, signingKey)) in _pendingDevices)
        {
            if (!key.StartsWith(prefix)) continue;
            var deviceId = key[prefix.Length..];
            byte[] pkBytes, skBytes;
            try
            {
                pkBytes = Convert.FromBase64String(publicKey);
                skBytes = Convert.FromBase64String(signingKey);
            }
            catch { continue; }
            contact.Devices[deviceId] = new DeviceKeys { PublicKey = pkBytes, SigningKey = skBytes };
            accepted.Add(key);
            _mainVm.AddSystemMessage($"Device {deviceId} for {userId} accepted. Verify fingerprint out-of-band!");
        }

        foreach (var key in accepted)
            _pendingDevices.TryRemove(key, out _);

        if (accepted.Count > 0)
            _store.SaveProfileDebounced(profile, passphrase);
    }

    // K3: Validate user ID format (name#hash, max 255 chars, no control chars)
    private static bool IsValidUserId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 255) return false;
        // Must not contain control characters
        foreach (var c in id)
            if (char.IsControl(c)) return false;
        return true;
    }

    private async Task ImportDiscordAsync(string token, string guildId)
    {
        if (_places is null)
        {
            _mainVm.AddSystemMessage("Place service not initialized.");
            return;
        }

        var importer = new DiscordImportService();
        importer.OnStatus += msg => Dispatcher.UIThread.Post(() => _mainVm.AddSystemMessage(msg));
        importer.OnError += msg => Dispatcher.UIThread.Post(() => _mainVm.AddSystemMessage($"Error: {msg}"));

        _mainVm.AddSystemMessage("Starting Discord import...");

        var data = await importer.FetchServerAsync(token, guildId);
        if (data is null) return;

        await importer.ImportToPlaceAsync(data, _places, _chat);
        Dispatcher.UIThread.Post(RefreshPlaces);
    }
}
