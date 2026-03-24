using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Rede.Desktop.ViewModels;

public partial class LoginViewModel : ViewModelBase
{
    [ObservableProperty] private string _userId = "";
    [ObservableProperty] private string _passphrase = "";
    [ObservableProperty] private string _serverUrl = "wss://localhost:9377";
    [ObservableProperty] private string _transport = "Direct";
    [ObservableProperty] private string _inviteCode = "";
    [ObservableProperty] private string _errorMessage = "";
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
        var envFile = System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".env");
        // Also try project root
        var altFile = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Rede", "rede-client", ".env");

        var path = System.IO.File.Exists(envFile) ? envFile : System.IO.File.Exists(altFile) ? altFile : null;
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
}
