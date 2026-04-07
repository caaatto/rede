using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
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

    // Settings categories
    [ObservableProperty] private int _selectedCategoryIndex;

    public bool IsProfileCategory => SelectedCategoryIndex == 0;
    public bool IsAppearanceCategory => SelectedCategoryIndex == 1;
    public bool IsPresenceCategory => SelectedCategoryIndex == 2;
    public bool IsNotificationsCategory => SelectedCategoryIndex == 3;
    public bool IsVoiceCategory => SelectedCategoryIndex == 4;
    public bool IsSecurityCategory => SelectedCategoryIndex == 5;
    public bool IsSystemCategory => SelectedCategoryIndex == 6;

    partial void OnSelectedCategoryIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsProfileCategory));
        OnPropertyChanged(nameof(IsAppearanceCategory));
        OnPropertyChanged(nameof(IsPresenceCategory));
        OnPropertyChanged(nameof(IsNotificationsCategory));
        OnPropertyChanged(nameof(IsVoiceCategory));
        OnPropertyChanged(nameof(IsSecurityCategory));
        OnPropertyChanged(nameof(IsSystemCategory));
    }

    // System integration (minimize-to-tray, autostart)
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _autostartEnabled;
    [ObservableProperty] private bool _startMinimized;

    public bool IsAutostartSupported { get; set; } = true;

    partial void OnMinimizeToTrayChanged(bool value) => OnSystemSettingsChanged?.Invoke();
    partial void OnAutostartEnabledChanged(bool value) => OnSystemSettingsChanged?.Invoke();
    partial void OnStartMinimizedChanged(bool value) => OnSystemSettingsChanged?.Invoke();

    public event Action? OnSystemSettingsChanged;

    // Appearance: theme variant (live-applies on change, saved via OnThemeChanged)
    [ObservableProperty] private string _themeVariant = "dark";

    public static readonly string[] ThemeVariants = new[] { "dark", "midnight", "dim", "light" };
    public static readonly string[] ThemeVariantLabels = new[] { "Dark (default)", "Midnight", "Dim", "Light" };

    public int SelectedThemeIndex
    {
        get => Math.Max(0, Array.IndexOf(ThemeVariants, ThemeVariant));
        set
        {
            if (value >= 0 && value < ThemeVariants.Length)
                ThemeVariant = ThemeVariants[value];
        }
    }

    partial void OnThemeVariantChanged(string value)
    {
        Themes.ThemeService.Apply(value);
        OnThemeChanged?.Invoke();
    }

    public event Action? OnThemeChanged;

    // Profile customization (local-only until Apply)
    [ObservableProperty] private string _accentColor = "#8b5cf6";
    [ObservableProperty] private Bitmap? _avatarImage;
    [ObservableProperty] private bool _hasAvatar;
    [ObservableProperty] private string _avatarInitial = "?";
    [ObservableProperty] private bool _profileDirty;

    public string? AvatarData { get; set; }
    public string? AvatarMimeType { get; set; }

    // Status / Presence
    [ObservableProperty] private string _selectedStatus = "online";
    [ObservableProperty] private string _customStatusText = "";

    public static readonly string[] StatusOptions = new[] { "online", "away", "dnd", "invisible" };
    public static readonly string[] StatusLabels = new[] { "Online", "Away", "Do Not Disturb", "Invisible" };

    public int SelectedStatusIndex
    {
        get => Array.IndexOf(StatusOptions, SelectedStatus);
        set
        {
            if (value >= 0 && value < StatusOptions.Length)
                SelectedStatus = StatusOptions[value];
        }
    }

    partial void OnSelectedStatusChanged(string value) => OnStatusChanged?.Invoke();
    partial void OnCustomStatusTextChanged(string value)
    {
        // M6: Enforce 128 char max for custom status
        if (value is not null && value.Length > 128)
        {
            CustomStatusText = value[..128];
            return; // setter re-triggers this handler with truncated value
        }
        OnStatusChanged?.Invoke();
    }

    public event Action? OnStatusChanged;

    // Notifications
    [ObservableProperty] private bool _notificationsEnabled = true;
    [ObservableProperty] private bool _notificationShowContent; // false = privacy mode (default)
    [ObservableProperty] private bool _notificationSoundEnabled = true;

    partial void OnNotificationsEnabledChanged(bool value) => OnNotificationSettingsChanged?.Invoke();
    partial void OnNotificationShowContentChanged(bool value) => OnNotificationSettingsChanged?.Invoke();
    partial void OnNotificationSoundEnabledChanged(bool value) => OnNotificationSettingsChanged?.Invoke();

    public event Action? OnNotificationSettingsChanged;

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

    public IBrush AccentBrush => ColorHelper.SafeParse(AccentColor);

    partial void OnAccentColorChanged(string value)
    {
        OnPropertyChanged(nameof(AccentBrush));
        ProfileDirty = true;
    }

    public event Action? OnProfileApplied; // save + broadcast
    public event Action? OnAvatarPickRequested;

    [RelayCommand]
    private void PickAvatar() => OnAvatarPickRequested?.Invoke();

    [RelayCommand]
    private void RemoveAvatar()
    {
        AvatarImage = null;
        AvatarData = null;
        AvatarMimeType = null;
        HasAvatar = false;
        ProfileDirty = true;
    }

    [RelayCommand]
    private void ApplyProfile()
    {
        if (!ProfileDirty) return;
        OnProfileApplied?.Invoke();
        ProfileDirty = false;
    }

    public void SetAvatarFromBytes(byte[] data, string mimeType)
    {
        if (data.Length > 256 * 1024) return;

        AvatarData = Convert.ToBase64String(data);
        AvatarMimeType = mimeType;

        using var ms = new MemoryStream(data);
        var oldBmp = AvatarImage;
        AvatarImage = new Bitmap(ms);
        HasAvatar = true;
        oldBmp?.Dispose(); // M4: Dispose previous bitmap
        ProfileDirty = true;
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
            // H2: Reject oversized avatars from network
            if (bytes.Length > 256 * 1024) { AvatarImage = null; HasAvatar = false; return; }
            using var ms = new MemoryStream(bytes);
            var oldBmp = AvatarImage;
            AvatarImage = new Bitmap(ms);
            oldBmp?.Dispose(); // M5: Dispose previous bitmap
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

    // Passphrase change
    [ObservableProperty] private string _passphraseChangeStatus = "";
    [ObservableProperty] private bool _isChangingPassphrase;

    public event Func<byte[], byte[], System.Threading.Tasks.Task<bool>>? OnChangePassphraseRequested;

    public async System.Threading.Tasks.Task ChangePassphraseAsync(byte[] currentPassphrase, byte[] newPassphrase)
    {
        if (IsChangingPassphrase) return;
        IsChangingPassphrase = true;
        PassphraseChangeStatus = "";

        try
        {
            if (OnChangePassphraseRequested is not null)
            {
                var success = await OnChangePassphraseRequested.Invoke(currentPassphrase, newPassphrase);
                PassphraseChangeStatus = success ? "Passphrase changed." : "Wrong current passphrase.";
            }
        }
        catch (Exception ex)
        {
            PassphraseChangeStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsChangingPassphrase = false;
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
    [ObservableProperty] private double _noiseGateThreshold = -60;  // dB: -100 to 0
    [ObservableProperty] private bool _noiseSuppression;
    [ObservableProperty] private bool _isNoiseSuppressionAvailable;
    [ObservableProperty] private bool _autoInputSensitivity = true;
    [ObservableProperty] private bool _autoGainControl;
    [ObservableProperty] private bool _echoCancellation = true;
    [ObservableProperty] private double _currentInputLevelDb = -100; // live mic level for UI meter

    public string InputVolumeText => $"{(int)InputVolume}%";
    public string OutputVolumeText => $"{(int)OutputVolume}%";
    public string NoiseGateText => AutoInputSensitivity ? "Auto" : $"{(int)NoiseGateThreshold} dB";
    public string NoiseSuppressionStatus => IsNoiseSuppressionAvailable
        ? "RNNoise - removes background noise from your mic"
        : "RNNoise unavailable on this platform";

    public event Action? OnBackRequested;
    public event Action? OnAudioSettingsChanged;

    private CancellationTokenSource? _debounce;

    private void DebouncedAudioChange()
    {
        // M7: Dispose previous CTS to prevent accumulation
        _debounce?.Cancel();
        _debounce?.Dispose();
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
    partial void OnNoiseSuppressionChanged(bool value)
    {
        DebouncedAudioChange();
    }
    partial void OnAutoInputSensitivityChanged(bool value)
    {
        OnPropertyChanged(nameof(NoiseGateText));
        DebouncedAudioChange();
    }
    partial void OnAutoGainControlChanged(bool value)
    {
        DebouncedAudioChange();
    }
    partial void OnEchoCancellationChanged(bool value)
    {
        DebouncedAudioChange();
    }

    [RelayCommand]
    private void Back()
    {
        OnBackRequested?.Invoke();
    }
}
