using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
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

    private RedeConnection? _conn;
    private AuthService? _auth;
    private ChatService? _chat;
    private ContactService? _contacts;
    private GroupService? _groups;
    private DeviceService? _devices;

    // Pending new devices awaiting user confirmation: key = "userId:deviceId"
    private readonly System.Collections.Generic.Dictionary<string, (string PublicKey, string SigningKey)> _pendingDevices = new();
    private UpdateService.ReleaseInfo? _pendingRelease;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ShowLogin();
            CheckForUpdatesAsync();
        };
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
                }
                return;
            }

            // Standalone exe — check GitHub Releases API
            var release = await UpdateService.CheckGitHubReleaseAsync();
            if (release is not null)
            {
                _pendingRelease = release;
                Dispatcher.UIThread.Post(() =>
                {
                    _loginVm.StatusMessage = release.DownloadUrl is not null
                        ? $"Update available: {release.Tag} — click to install"
                        : $"Update available: {release.Tag} (no binary for this platform)";
                    _loginVm.IsUpdateAvailable = release.DownloadUrl is not null;
                });
            }
        }
        catch { }
    }

    private void ShowLogin()
    {
        _loginVm.ErrorMessage = "";
        _loginVm.IsLoading = false;
        // K1: Unsubscribe before subscribing to prevent duplicate handlers on repeated ShowLogin calls
        _loginVm.OnLoginRequested -= OnLogin;
        _loginVm.OnRegisterRequested -= OnRegister;
        _loginVm.OnUpdateRequested -= OnUpdateRequested;
        _loginVm.OnLoginRequested += OnLogin;
        _loginVm.OnRegisterRequested += OnRegister;
        _loginVm.OnUpdateRequested += OnUpdateRequested;
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
    private DateTime _lastRetryTime;

    private void ShowMain()
    {
        // K1: Only wire once to prevent handler accumulation on reconnect
        if (!_mainVmWired)
        {
            WireMainViewModel();
            _mainVmWired = true;
        }
        var mainView = CreateMainView();
        RootContent.Content = mainView;
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
        return mainView;
    }

    private void OnLogin(string userId, string passphrase, string serverUrl, string transport)
    {
        LoginAsync(userId, passphrase, serverUrl, transport);
    }

    private void OnRegister(string displayName, string passphrase, string serverUrl, string transport, string inviteCode)
    {
        RegisterAsync(displayName, passphrase, serverUrl, transport, inviteCode);
    }

    private async void LoginAsync(string userId, string passphrase, string serverUrl, string transport)
    {
        try
        {
            _loginVm.IsLoading = true;
            _loginVm.ErrorMessage = "";

            InitServices(serverUrl, transport);

            await _conn!.ConnectAsync();
            var ok = await _auth!.LoginAsync(userId, passphrase);
            if (!ok)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _loginVm.ErrorMessage = "Profile not found or wrong passphrase.";
                    _loginVm.IsLoading = false;
                });
                return;
            }

            // H1: Clear passphrase from login VM after successful auth
            Dispatcher.UIThread.Post(() => _loginVm.Passphrase = "");
            PropagateProfile();
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _loginVm.ErrorMessage = SanitizeErrorMessage(ex);
                _loginVm.IsLoading = false;
            });
        }
    }

    private async void RegisterAsync(string displayName, string passphrase, string serverUrl, string transport, string inviteCode)
    {
        try
        {
            _loginVm.IsLoading = true;
            _loginVm.ErrorMessage = "";

            InitServices(serverUrl, transport);

            await _conn!.ConnectAsync();
            await _auth!.RegisterAsync(displayName, passphrase, inviteCode);

            // H1: Clear passphrase from login VM after successful registration
            Dispatcher.UIThread.Post(() => _loginVm.Passphrase = "");
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _loginVm.ErrorMessage = SanitizeErrorMessage(ex);
                _loginVm.IsLoading = false;
            });
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
                if (key == "REDE_I2P_PROXY" && !string.IsNullOrEmpty(val)) proxy.I2PProxy = val;
                if (key == "REDE_TOR_PROXY" && !string.IsNullOrEmpty(val)) proxy.TorProxy = val;
            }
        }

        _conn?.Dispose();
        _conn = new RedeConnection(serverUrl, proxy);
        _auth = new AuthService(_conn, _store);
        _chat = new ChatService(_conn, _store);
        _contacts = new ContactService(_conn, _store);
        _groups = new GroupService(_conn, _store);
        _devices = new DeviceService(_conn, _store);

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
        });

        _conn.OnReconnecting += () => Dispatcher.UIThread.Post(() =>
        {
            _mainVm.ConnectionStatus = "Reconnecting...";
            _mainVm.IsConnected = false;
        });

        _conn.OnError += err => Dispatcher.UIThread.Post(() =>
        {
            _mainVm.AddSystemMessage($"[Error] {err}");
        });

        // Auth events
        _auth.OnAuthSuccess += () => Dispatcher.UIThread.Post(() =>
        {
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
        _chat.OnMessageReceived += (from, text, chatId, ts, isSealed) => Dispatcher.UIThread.Post(() =>
        {
            _mainVm.AddIncomingMessage(from, text, ts);
            MarkContactUnread(from);
        });

        _chat.OnSystemMessage += msg => Dispatcher.UIThread.Post(() =>
        {
            _mainVm.AddSystemMessage(msg);
        });

        _chat.OnNewDeviceDetected += (targetUserId, deviceId, publicKey, signingKey) =>
        {
            // Store pending device for user confirmation
            _pendingDevices[$"{targetUserId}:{deviceId}"] = (publicKey, signingKey);
        };

        _chat.OnGroupKeyReceived += (groupId, name, key, sig, senderId) =>
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
                    _mainVm.AddSystemMessage($"[SECURITY] Group key without sender/signature — rejected."));
                return;
            }

            if (!profile.Contacts.TryGetValue(senderId, out var sender) || sender.SigningKey is null)
            {
                Dispatcher.UIThread.Post(() =>
                    _mainVm.AddSystemMessage($"[SECURITY] Group key from unknown sender {senderId} — rejected."));
                return;
            }

            if (!Rede.Core.Crypto.CryptoService.VerifyGroupKey(groupId, name, key, sig, sender.SigningKey))
            {
                Dispatcher.UIThread.Post(() =>
                    _mainVm.AddSystemMessage($"[SECURITY] Invalid group key signature from {senderId}! Key rejected."));
                return;
            }

            Task.Run(async () =>
            {
                await _store.AddGroupAsync(profile, groupId, safeName, key, null, passphrase);
                Dispatcher.UIThread.Post(() =>
                {
                    _mainVm.AddSystemMessage($"Received group key for \"{safeName}\"");
                    RefreshGroups();
                });
            });
        };

        // Contact events
        _contacts.OnContactAdded += (userId, displayName, fp) => Dispatcher.UIThread.Post(() =>
        {
            _mainVm.AddSystemMessage($"Contact added: {displayName} ({fp})");
            RefreshContacts();
        });

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
        _groups.OnGroupMessageReceived += (groupId, from, text, ts) => Dispatcher.UIThread.Post(() =>
        {
            _mainVm.AddIncomingMessage(from, text, ts);
            MarkGroupUnread(groupId);
        });

        _groups.OnGroupsChanged += () => Dispatcher.UIThread.Post(RefreshGroups);

        _groups.OnSystemMessage += msg => Dispatcher.UIThread.Post(() =>
        {
            _mainVm.AddSystemMessage(msg);
        });

        // Device events
        _devices.OnSystemMessage += msg => Dispatcher.UIThread.Post(() =>
        {
            _mainVm.AddSystemMessage(msg);
        });
    }

    private void PropagateProfile()
    {
        if (_auth?.Profile is null) return;
        var p = _auth.Profile;
        var pp = _auth.Passphrase;

        if (_chat is not null) { _chat.Profile = p; _chat.Passphrase = pp; }
        if (_contacts is not null) { _contacts.Profile = p; _contacts.Passphrase = pp; }
        if (_groups is not null) { _groups.Profile = p; _groups.Passphrase = pp; }
        if (_devices is not null) { _devices.Profile = p; _devices.Passphrase = pp; }

        // Cleanup expired TTL messages on login
        if (pp is not null)
            Task.Run(async () => await _store.CleanupExpiredMessagesAsync(p, pp));

        Dispatcher.UIThread.Post(() =>
        {
            RefreshContacts();
            RefreshGroups();
        });
    }

    private void WireMainViewModel()
    {
        _mainVm.OnMessageSend += text =>
        {
            if (_mainVm.SelectedConversation is ContactItemViewModel contact)
                _chat?.SendMessage(contact.UserId, text, _mainVm.TtlSeconds);
            else if (_mainVm.SelectedConversation is GroupItemViewModel group)
                _groups?.SendGroupMessage(group.GroupId, text, _mainVm.TtlSeconds);
        };

        _mainVm.OnCommandExecuted += (cmd, args) =>
        {
            HandleCommand(cmd, args);
        };

        _mainVm.OnChatHistoryRequested += chatId =>
        {
            LoadChatHistoryForConversation(chatId);
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
                _groups?.CreateGroup(string.Join(" ", args));
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
                _mainVm.AddSystemMessage(ttl > 0 ? $"TTL set to {ttl} day(s) — messages auto-delete after {ttl}d" : "TTL disabled");
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

            case "settings" or "key":
                ShowSettings();
                break;

            case "help":
                _mainVm.AddSystemMessage("Commands: /add <id>, /confirm <id>, /fingerprint [id], /group <name>, /ginvite <gid> <uid>, /kick <gid> <uid>, /ttl <days>, /link, /devices, /settings");
                break;

            default:
                _mainVm.AddSystemMessage($"Unknown command: /{cmd}");
                break;
        }
    }

    private void RefreshContacts()
    {
        var contacts = _contacts?.GetContacts();
        if (contacts is null) return;

        _mainVm.Contacts.Clear();
        foreach (var (id, c) in contacts)
        {
            // M3: Sanitize server-provided display names
            _mainVm.Contacts.Add(new ContactItemViewModel
            {
                UserId = id,
                DisplayName = SanitizeDisplayString(c.DisplayName ?? id, 64),
            });
        }
    }

    private void RefreshGroups()
    {
        var groups = _groups?.GetGroups();
        if (groups is null) return;

        _mainVm.Groups.Clear();
        foreach (var (id, g) in groups)
        {
            _mainVm.Groups.Add(new GroupItemViewModel
            {
                GroupId = id,
                Name = g.Name,
                MemberCount = g.Members?.Count ?? 0,
            });
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
                _mainVm.Messages.Add(new ChatMessageViewModel
                {
                    From = msg.From,
                    Text = msg.Text,
                    IsOwn = isOwn,
                    Timestamp = ts,
                    Ttl = msg.Ttl,
                });
            }
        }

        // Clear unread indicator
        if (_mainVm.SelectedConversation is ContactItemViewModel contact)
            contact.HasUnread = false;
        else if (_mainVm.SelectedConversation is GroupItemViewModel group)
            group.HasUnread = false;
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
            PublicKey = p.PublicKey,
        };
        vm.OnBackRequested += () =>
        {
            // H5: Re-wire retry handler when returning from settings
            RootContent.Content = CreateMainView();
        };
        var settingsView = new SettingsView { DataContext = vm };
        RootContent.Content = settingsView;
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Q && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _conn?.Dispose();
            Close();
        }
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
        // Strip control characters
        var s = System.Text.RegularExpressions.Regex.Replace(input, @"[\x00-\x1f\x7f]", "");
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
            contact.Devices[deviceId] = new DeviceKeys { PublicKey = publicKey, SigningKey = signingKey };
            accepted.Add(key);
            _mainVm.AddSystemMessage($"Device {deviceId} for {userId} accepted. Verify fingerprint out-of-band!");
        }

        foreach (var key in accepted)
            _pendingDevices.Remove(key);

        if (accepted.Count > 0)
            Task.Run(async () => await _store.SaveProfileAsync(profile, passphrase));
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
}
