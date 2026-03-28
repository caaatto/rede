using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

    // Voice call transport (read-only, derived from connection)
    [ObservableProperty] private string _callTransport = "Direct";

    // Audio settings
    [ObservableProperty] private ObservableCollection<string> _inputDevices = new();
    [ObservableProperty] private ObservableCollection<string> _outputDevices = new();
    [ObservableProperty] private int _selectedInputDeviceIndex;
    [ObservableProperty] private int _selectedOutputDeviceIndex;
    [ObservableProperty] private double _inputVolume = 100;       // 0-200 (percentage)
    [ObservableProperty] private double _outputVolume = 100;      // 0-200 (percentage)
    [ObservableProperty] private double _noiseGateThreshold = 2;  // 0-100 (percentage, 0=off)

    public string InputVolumeText => $"{(int)InputVolume}%";
    public string OutputVolumeText => $"{(int)OutputVolume}%";
    public string NoiseGateText => NoiseGateThreshold < 1 ? "Off" : $"{(int)NoiseGateThreshold}%";

    public event Action? OnBackRequested;
    public event Action? OnAudioSettingsChanged;

    partial void OnSelectedInputDeviceIndexChanged(int value) => OnAudioSettingsChanged?.Invoke();
    partial void OnSelectedOutputDeviceIndexChanged(int value) => OnAudioSettingsChanged?.Invoke();
    partial void OnInputVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(InputVolumeText));
        OnAudioSettingsChanged?.Invoke();
    }
    partial void OnOutputVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(OutputVolumeText));
        OnAudioSettingsChanged?.Invoke();
    }
    partial void OnNoiseGateThresholdChanged(double value)
    {
        OnPropertyChanged(nameof(NoiseGateText));
        OnAudioSettingsChanged?.Invoke();
    }

    [RelayCommand]
    private void Back()
    {
        OnBackRequested?.Invoke();
    }
}
