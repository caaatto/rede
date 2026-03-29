using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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

    // Profile customization
    [ObservableProperty] private string _accentColor = "#8b5cf6";
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _avatarImage;
    [ObservableProperty] private bool _hasAvatar;
    [ObservableProperty] private string _avatarInitial = "?";

    // Raw avatar data for saving
    public string? AvatarData { get; set; }
    public string? AvatarMimeType { get; set; }

    // Preset accent colors
    public static readonly string[] PresetColors = new[]
    {
        "#8b5cf6", // violet (default)
        "#6366f1", // indigo
        "#3b82f6", // blue
        "#2dd4bf", // teal
        "#22c55e", // green
        "#eab308", // yellow
        "#f97316", // orange
        "#ef4444", // red
        "#ec4899", // pink
        "#a855f7", // purple
        "#06b6d4", // cyan
        "#f43f5e", // rose
    };

    public IBrush AccentBrush => Brush.Parse(AccentColor);

    partial void OnAccentColorChanged(string value)
    {
        OnPropertyChanged(nameof(AccentBrush));
        OnProfileChanged?.Invoke();
    }

    public event Action? OnProfileChanged;
    public event Action? OnAvatarPickRequested;

    [RelayCommand]
    private void PickAvatar()
    {
        OnAvatarPickRequested?.Invoke();
    }

    [RelayCommand]
    private void RemoveAvatar()
    {
        AvatarImage = null;
        AvatarData = null;
        AvatarMimeType = null;
        HasAvatar = false;
        OnProfileChanged?.Invoke();
    }

    public void SetAvatarFromBytes(byte[] data, string mimeType)
    {
        // Max 256KB
        if (data.Length > 256 * 1024) return;

        AvatarData = Convert.ToBase64String(data);
        AvatarMimeType = mimeType;

        using var ms = new MemoryStream(data);
        AvatarImage = new Bitmap(ms);
        HasAvatar = true;
        OnProfileChanged?.Invoke();
    }

    public void LoadAvatarFromBase64(string? base64, string? mimeType)
    {
        if (string.IsNullOrEmpty(base64))
        {
            AvatarImage = null;
            HasAvatar = false;
            return;
        }

        try
        {
            var bytes = Convert.FromBase64String(base64);
            using var ms = new MemoryStream(bytes);
            AvatarImage = new Bitmap(ms);
            AvatarData = base64;
            AvatarMimeType = mimeType;
            HasAvatar = true;
        }
        catch
        {
            AvatarImage = null;
            HasAvatar = false;
        }
    }

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

    private CancellationTokenSource? _debounce;

    private void DebouncedAudioChange()
    {
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        var token = _debounce.Token;
        System.Threading.Tasks.Task.Delay(300, token).ContinueWith(_ =>
        {
            if (!token.IsCancellationRequested)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => OnAudioSettingsChanged?.Invoke());
        }, token, System.Threading.Tasks.TaskContinuationOptions.OnlyOnRanToCompletion,
            System.Threading.Tasks.TaskScheduler.Default);
    }

    partial void OnSelectedInputDeviceIndexChanged(int value) => OnAudioSettingsChanged?.Invoke();
    partial void OnSelectedOutputDeviceIndexChanged(int value) => OnAudioSettingsChanged?.Invoke();
    partial void OnInputVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(InputVolumeText));
        DebouncedAudioChange();
    }
    partial void OnOutputVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(OutputVolumeText));
        DebouncedAudioChange();
    }
    partial void OnNoiseGateThresholdChanged(double value)
    {
        OnPropertyChanged(nameof(NoiseGateText));
        DebouncedAudioChange();
    }

    [RelayCommand]
    private void Back()
    {
        OnBackRequested?.Invoke();
    }
}
