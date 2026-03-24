using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Rede.Desktop.ViewModels;

public partial class LoginViewModel : ViewModelBase
{
    [ObservableProperty] private string _userId = "";
    [ObservableProperty] private string _passphrase = "";
    [ObservableProperty] private string _serverUrl = "ws://ifq6tbaob6tepx33yj5ldawwystnggcpqdbmfavmla635wekrwlq.b32.i2p";
    [ObservableProperty] private string _transport = "I2P";
    [ObservableProperty] private string _inviteCode = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isRegistering;

    public string[] TransportOptions { get; } = { "Direct", "Tor", "I2P" };

    // (userId, passphrase, serverUrl, transport)
    public event Action<string, string, string, string>? OnLoginRequested;
    // (displayName, passphrase, serverUrl, transport, inviteCode)
    public event Action<string, string, string, string, string>? OnRegisterRequested;

    public LoginViewModel()
    {
        LoadEnvDefaults();
    }

    private void LoadEnvDefaults()
    {
        // Try multiple locations for .env
        string?[] candidates = {
            // Repo root (when running from source via dotnet run)
            FindRepoEnv(),
            // User home ~/Rede/rede-client/.env
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Rede", "rede-client", ".env"),
            // Installed location
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

            switch (key)
            {
                case "REDE_SERVER":
                    ServerUrl = val;
                    break;
                case "REDE_TRANSPORT":
                    Transport = val switch
                    {
                        "i2p" or "I2P" => "I2P",
                        "tor" or "Tor" => "Tor",
                        _ => "Direct",
                    };
                    break;
            }
        }
    }

    [RelayCommand]
    private void Login()
    {
        if (string.IsNullOrWhiteSpace(UserId) || string.IsNullOrWhiteSpace(Passphrase))
        {
            ErrorMessage = "User ID and passphrase are required.";
            return;
        }

        ErrorMessage = "";
        IsLoading = true;

        try
        {
            OnLoginRequested?.Invoke(UserId, Passphrase, ServerUrl, Transport);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Register()
    {
        if (string.IsNullOrWhiteSpace(UserId) || string.IsNullOrWhiteSpace(Passphrase))
        {
            ErrorMessage = "Display name and passphrase are required.";
            return;
        }

        if (Passphrase.Length < 12)
        {
            ErrorMessage = "Passphrase must be at least 12 characters.";
            return;
        }

        if (string.IsNullOrWhiteSpace(InviteCode))
        {
            ErrorMessage = "Invite code is required for registration.";
            return;
        }

        ErrorMessage = "";
        IsLoading = true;
        IsRegistering = true;

        try
        {
            OnRegisterRequested?.Invoke(UserId, Passphrase, ServerUrl, Transport, InviteCode);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
            IsRegistering = false;
        }
    }

    private static string? FindRepoEnv()
    {
        // Walk up from executable looking for .env in a rede-client dir
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var envPath = System.IO.Path.Combine(dir, ".env");
            if (System.IO.File.Exists(envPath))
                return envPath;
            dir = System.IO.Directory.GetParent(dir)?.FullName;
        }
        return null;
    }
}
