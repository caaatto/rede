using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Rede.Desktop.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty] private string _userId = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _deviceId = "";
    [ObservableProperty] private string _fingerprint = "";
    [ObservableProperty] private string _publicKey = "";

    public event Action? OnBackRequested;

    [RelayCommand]
    private void Back()
    {
        OnBackRequested?.Invoke();
    }
}
