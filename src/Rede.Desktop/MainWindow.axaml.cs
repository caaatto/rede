using System;
using System.Linq;
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
                    Dispatcher.UIThread.Post(() =>
                        _loginVm.StatusMessage = $"Update available ({remote[..8]})");

                    var success = await updater.PullAndBuildAsync();
                    if (success)
                        Dispatcher.UIThread.Post(() =>
                            _loginVm.StatusMessage = "Updated! Restart to apply.");
                }
                return;
            }

            // Standalone exe — check GitHub Releases API
            var release = await UpdateService.CheckGitHubReleaseAsync();
            if (release is not null)
            {
                Dispatcher.UIThread.Post(() =>
                    _loginVm.StatusMessage = $"Update available: {release.Tag}");

                var success = await UpdateService.DownloadAndReplaceAsync(release,
                    status => Dispatcher.UIThread.Post(() => _loginVm.StatusMessage = status));

                if (success)
                    Dispatcher.UIThread.Post(() =>
                        _loginVm.StatusMessage = $"Updated to {release.Tag}! Restart to apply.");
            }
        }
        catch { }
    }

    private void ShowLogin()
    {
        _loginVm.ErrorMessage = "";
        _loginVm.IsLoading = false;
        var loginView = new LoginView { DataContext = _loginVm };
        _loginVm.OnLoginRequested += OnLogin;
        _loginVm.OnRegisterRequested += OnRegister;
        RootContent.Content = loginView;
    }

    private void ShowMain()
    {
        WireMainViewModel();
        var mainView = new MainView { DataContext = _mainVm };
        mainView.OnRetryConnection += () =>
        {
            if (_conn is not null)
            {
                _mainVm.ConnectionStatus = "Reconnecting...";
                _ = _conn.ConnectAsync();
            }
        };
        RootContent.Content = mainView;
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

            PropagateProfile();
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _loginVm.ErrorMessage = ex.Message;
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
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _loginVm.ErrorMessage = ex.Message;
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
            _loginVm.ErrorMessage = err;
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
        switch (cmd.ToLowerInvariant())
        {
            case "add" when args.Length >= 1:
                _contacts?.AddContact(args[0]);
                break;

            case "confirm" when args.Length >= 1:
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
                _groups?.InviteToGroup(args[0], args[1]);
                break;

            case "kick" when args.Length >= 2:
                _groups?.KickFromGroup(args[0], args[1]);
                break;

            case "ttl" when args.Length >= 1 && int.TryParse(args[0], out var ttl):
                _mainVm.TtlSeconds = ttl;
                _mainVm.AddSystemMessage($"TTL set to {ttl}s");
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
                _mainVm.AddSystemMessage("Commands: /add <id>, /confirm <id>, /fingerprint [id], /group <name>, /ginvite <gid> <uid>, /kick <gid> <uid>, /ttl <seconds>, /link, /devices, /settings");
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
            _mainVm.Contacts.Add(new ContactItemViewModel
            {
                UserId = id,
                DisplayName = c.DisplayName ?? id,
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
            var mainView = new MainView { DataContext = _mainVm };
            RootContent.Content = mainView;
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
}
