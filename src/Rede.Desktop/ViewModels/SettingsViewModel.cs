using System;
using System.Collections.Generic;
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

    // Voice call settings
    [ObservableProperty] private int _selectedCallModeIndex;
    [ObservableProperty] private bool _allowFastCalls = true;

    public List<string> CallModeOptions { get; } = new() { "Secure (I2P)", "Fast (Direct)" };

    public event Action? OnBackRequested;
    public event Action<string, bool>? OnCallSettingsChanged;

    partial void OnSelectedCallModeIndexChanged(int value)
    {
        var mode = value == 1 ? "fast" : "secure";
        OnCallSettingsChanged?.Invoke(mode, AllowFastCalls);
    }

    partial void OnAllowFastCallsChanged(bool value)
    {
        var mode = SelectedCallModeIndex == 1 ? "fast" : "secure";
        OnCallSettingsChanged?.Invoke(mode, value);
    }

    [RelayCommand]
    private void Back()
    {
        OnBackRequested?.Invoke();
    }
}
