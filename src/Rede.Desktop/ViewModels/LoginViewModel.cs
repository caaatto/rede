using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Rede.Desktop.ViewModels;

public partial class LoginViewModel : ViewModelBase
{
    [ObservableProperty] private string _userId = "";
    [ObservableProperty] private string _selectedServer = "IP Direct";
    [ObservableProperty] private string _inviteCode = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private bool _isRegistering;
    [ObservableProperty] private bool _isRegisterMode;

    // When checked, the last-profile hint is written on successful login so
    // the next app start shows quick-login with only the passphrase field.
    // When unchecked, any existing hint is cleared on successful login.
    [ObservableProperty] private bool _staySignedIn = true;

    // Quick login: set when a profile hint exists from a previous session.
    // Only the passphrase field is shown — the userId is recovered from the
    // decrypted profile file so it never needs to be entered again.
    [ObservableProperty] private bool _hasQuickLogin;
    [ObservableProperty] private string _quickLoginHash = "";

    public static readonly (string Name, string Url, string Transport)[] Servers =
    {
        ("I2Pd Nürnberg", "ws://ifq6tbaob6tepx33yj5ldawwystnggcpqdbmfavmla635wekrwlq.b32.i2p", "I2P"),
        ("IP Direct", "wss://clip.jetzt/rede", "Direct"),
    };

    public string[] ServerOptions { get; } = Servers.Select(s => s.Name).ToArray();

    public string ServerUrl => Servers.FirstOrDefault(s => s.Name == SelectedServer).Url
                               ?? Servers[0].Url;

    public string Transport => Servers.FirstOrDefault(s => s.Name == SelectedServer).Transport
                               ?? Servers[0].Transport;

    public string UserIdWatermark => IsRegisterMode ? "alice" : "alice#a3f1";

    public bool IsDirectTransport => Transport == "Direct";

    partial void OnSelectedServerChanged(string value)
    {
        OnPropertyChanged(nameof(ServerUrl));
        OnPropertyChanged(nameof(Transport));
        OnPropertyChanged(nameof(IsDirectTransport));
    }

    partial void OnIsRegisterModeChanged(bool value)
    {
        OnPropertyChanged(nameof(UserIdWatermark));
    }

    // Passphrase is supplied as a byte[] at submit time by the view code-behind
    // (SecureTextBox.ExtractPassphrase). The VM never holds it.
    //
    // Contract: the byte[] is OWNED by the event subscriber after the invocation —
    // the subscriber is responsible for mlock/zero. The VM does not retain a
    // reference.

    // (userId, passphraseBytes, serverUrl, transport)
    public event Action<string, byte[], string, string>? OnLoginRequested;
    // (hashHex, passphraseBytes, serverUrl, transport)
    public event Action<string, byte[], string, string>? OnQuickLoginRequested;
    // (displayName, passphraseBytes, serverUrl, transport, inviteCode)
    public event Action<string, byte[], string, string, string>? OnRegisterRequested;
    public event Action? OnUpdateRequested;

    public LoginViewModel()
    {
        LoadEnvDefaults();
    }

    private void LoadEnvDefaults()
    {
        // .env can override the selected server by name
        string?[] candidates = {
            FindRepoEnv(),
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Rede", "rede-client", ".env"),
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Rede", ".env"),
        };

        var path = candidates.FirstOrDefault(p => p is not null && System.IO.File.Exists(p));
        if (path is null) return;

        foreach (var line in System.IO.File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#') || !trimmed.Contains('=')) continue;
            var eq = trimmed.IndexOf('=');
            var key = trimmed[..eq].Trim();
            var val = trimmed[(eq + 1)..].Trim();

            if (key == "REDE_SERVER_NAME" && ServerOptions.Contains(val))
                SelectedServer = val;
        }
    }

    [RelayCommand]
    private void ToggleMode()
    {
        IsRegisterMode = !IsRegisterMode;
        ErrorMessage = "";
    }

    [RelayCommand]
    private void TriggerUpdate()
    {
        OnUpdateRequested?.Invoke();
    }

    [RelayCommand]
    private void UseDifferentAccount()
    {
        HasQuickLogin = false;
        QuickLoginHash = "";
        ErrorMessage = "";
    }

    /// <summary>
    /// Submit a quick login. <paramref name="passphrase"/> ownership transfers
    /// to the subscriber of <see cref="OnQuickLoginRequested"/>.
    /// </summary>
    public void SubmitQuickLogin(byte[] passphrase)
    {
        if (passphrase.Length == 0)
        {
            ErrorMessage = "Passphrase required.";
            return;
        }
        ErrorMessage = "";
        IsLoading = true;
        try
        {
            OnQuickLoginRequested?.Invoke(QuickLoginHash, passphrase, ServerUrl, Transport);
        }
        catch
        {
            ErrorMessage = "Login failed. Please try again.";
            IsLoading = false;
        }
    }

    /// <summary>
    /// Submit a login. <paramref name="passphrase"/> ownership transfers to the
    /// subscriber of <see cref="OnLoginRequested"/>.
    /// </summary>
    public void SubmitLogin(byte[] passphrase)
    {
        if (string.IsNullOrWhiteSpace(UserId) || passphrase.Length == 0)
        {
            ErrorMessage = "User ID and passphrase are required.";
            return;
        }

        if (UserId.Length > 255 || ContainsControlChars(UserId))
        {
            ErrorMessage = "Invalid User ID.";
            return;
        }

        ErrorMessage = "";
        IsLoading = true;

        try
        {
            OnLoginRequested?.Invoke(UserId.Trim(), passphrase, ServerUrl, Transport);
        }
        catch
        {
            ErrorMessage = "Login failed. Please try again.";
            IsLoading = false;
        }
    }

    /// <summary>
    /// Submit a registration. <paramref name="passphrase"/> ownership transfers
    /// to the subscriber; <paramref name="passphraseConfirm"/> is zeroed by the
    /// caller after return.
    /// </summary>
    public bool SubmitRegister(byte[] passphrase, byte[] passphraseConfirm, int passphraseCharCount)
    {
        if (string.IsNullOrWhiteSpace(UserId) || passphrase.Length == 0)
        {
            ErrorMessage = "Display name and passphrase are required.";
            return false;
        }

        if (UserId.Length > 64 || ContainsControlChars(UserId))
        {
            ErrorMessage = "Display name must be 1-64 characters, no special characters.";
            return false;
        }

        if (passphraseCharCount < 12)
        {
            ErrorMessage = "Passphrase must be at least 12 characters.";
            return false;
        }

        if (passphrase.Length != passphraseConfirm.Length ||
            !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(passphrase, passphraseConfirm))
        {
            ErrorMessage = "Passphrases do not match.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(InviteCode))
        {
            ErrorMessage = "Invite code is required for registration.";
            return false;
        }

        if (InviteCode.Length > 128 || ContainsControlChars(InviteCode))
        {
            ErrorMessage = "Invalid invite code.";
            return false;
        }

        ErrorMessage = "";
        IsLoading = true;
        IsRegistering = true;

        try
        {
            OnRegisterRequested?.Invoke(UserId.Trim(), passphrase, ServerUrl, Transport, InviteCode.Trim());
            return true;
        }
        catch
        {
            ErrorMessage = "Registration failed. Please try again.";
            IsLoading = false;
            IsRegistering = false;
            return false;
        }
    }

    private static bool ContainsControlChars(string s)
    {
        foreach (var c in s)
            if (char.IsControl(c)) return true;
        return false;
    }

    private static string? FindRepoEnv()
    {
        // Walk up from executable looking for .env in a rede-client dir
        // M5: Limit depth to prevent hangs on slow/network mounts
        var dir = AppContext.BaseDirectory;
        for (int depth = 0; dir is not null && depth < 10; depth++)
        {
            var envPath = System.IO.Path.Combine(dir, ".env");
            if (System.IO.File.Exists(envPath))
                return envPath;
            dir = System.IO.Directory.GetParent(dir)?.FullName;
        }
        return null;
    }
}
