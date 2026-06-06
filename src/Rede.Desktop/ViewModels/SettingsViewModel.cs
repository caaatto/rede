using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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

    // Max base64 length for AvatarData on the wire. Profile control messages are
    // ratcheted, and DoubleRatchet.Encrypt pads to fixed buckets capped at 16382
    // bytes of plaintext (MessagePadding). The JSON envelope around the avatar
    // (__rede_ctrl, accentColor, mime, field names) eats ~100 bytes, so we cap
    // the base64 payload at 12 KB to leave comfortable headroom.
    public const int MaxAvatarBase64 = 12 * 1024;
    private const int MaxAvatarRawBytes = MaxAvatarBase64 * 3 / 4; // 9216

    public void SetAvatarFromBytes(byte[] data, string mimeType)
    {
        if (data.Length > 256 * 1024) return;

        byte[] finalBytes = data;
        string finalMime = mimeType;

        if (data.Length > MaxAvatarRawBytes)
        {
            // Re-encode at decreasing dimensions until the PNG fits the wire cap.
            // Returns null if even the smallest size won't fit (extremely unlikely
            // for real photos).
            var compressed = TryCompressAvatar(data);
            if (compressed is null) return;
            finalBytes = compressed;
            finalMime = "image/png";
        }

        AvatarData = Convert.ToBase64String(finalBytes);
        AvatarMimeType = finalMime;

        using var ms = new MemoryStream(finalBytes);
        var oldBmp = AvatarImage;
        AvatarImage = new Bitmap(ms);
        HasAvatar = true;
        oldBmp?.Dispose(); // M4: Dispose previous bitmap
        ProfileDirty = true;
    }

    private static byte[]? TryCompressAvatar(byte[] sourceBytes)
    {
        try
        {
            using var srcMs = new MemoryStream(sourceBytes);
            using var srcBmp = new Bitmap(srcMs);

            // Avalonia's Bitmap.Save writes PNG. PNGs of small photo-style content
            // typically land between 4-12 KB at 64-96 px; we step down until we fit.
            int[] sizes = { 96, 64, 48, 32 };
            foreach (var size in sizes)
            {
                using var scaled = srcBmp.CreateScaledBitmap(
                    new Avalonia.PixelSize(size, size),
                    BitmapInterpolationMode.HighQuality);
                using var outMs = new MemoryStream();
                scaled.Save(outMs);
                var bytes = outMs.ToArray();
                if (bytes.Length <= MaxAvatarRawBytes)
                    return bytes;
            }
            return null;
        }
        catch
        {
            return null;
        }
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

    // --- Security keys (FIDO2) ---
    public sealed class Fido2KeyItem
    {
        public string Name { get; init; } = "";
        public string CredentialId { get; init; } = "";
        public string Added { get; init; } = "";
    }

    [ObservableProperty] private ObservableCollection<Fido2KeyItem> _securityKeys = new();
    [ObservableProperty] private bool _fidoBackendAvailable;
    [ObservableProperty] private bool _isFidoBusy;
    [ObservableProperty] private string _fidoStatus = "";
    [ObservableProperty] private bool _hasRecoveryCode;
    [ObservableProperty] private string _generatedRecoveryCode = ""; // shown ONCE right after generation

    public bool ShowFidoInstall => !FidoBackendAvailable && !IsFidoBusy;
    public bool HasSecurityKeys => SecurityKeys.Count > 0;

    partial void OnFidoBackendAvailableChanged(bool value) => OnPropertyChanged(nameof(ShowFidoInstall));
    partial void OnIsFidoBusyChanged(bool value) => OnPropertyChanged(nameof(ShowFidoInstall));

    /// <summary>Download + verify the native libfido2 (handled by MainWindow, mirrors RNNoise install).</summary>
    public Func<Task>? OnInstallFido2;
    /// <summary>(keyName, pin) → success. MainWindow performs make-credential + PMS wrap.</summary>
    public event Func<string, string?, Task<bool>>? OnEnrollKeyRequested;
    /// <summary>Returns the one-time recovery code (grouped) or null on failure.</summary>
    public event Func<Task<string?>>? OnGenerateRecoveryRequested;
    /// <summary>credentialId (base64) of the key to remove.</summary>
    public event Func<string, Task>? OnRemoveKeyRequested;

    [RelayCommand]
    private async Task InstallFido2()
    {
        if (OnInstallFido2 is not null) await OnInstallFido2();
    }

    public async Task EnrollKeyAsync(string keyName, string? pin)
    {
        if (IsFidoBusy || OnEnrollKeyRequested is null) return;
        IsFidoBusy = true;
        FidoStatus = "Touch your security key…";
        GeneratedRecoveryCode = "";
        try
        {
            var ok = await OnEnrollKeyRequested.Invoke(keyName, pin);
            FidoStatus = ok
                ? (HasRecoveryCode
                    ? "Security key enrolled."
                    : "Security key enrolled. Generate a recovery code now so you can't get locked out.")
                : "Enrollment failed.";
        }
        catch (Exception ex) { FidoStatus = ex.Message; }
        finally { IsFidoBusy = false; }
    }

    public async Task GenerateRecoveryAsync()
    {
        if (IsFidoBusy || OnGenerateRecoveryRequested is null) return;
        IsFidoBusy = true;
        FidoStatus = "";
        try
        {
            var code = await OnGenerateRecoveryRequested.Invoke();
            if (code is not null)
            {
                GeneratedRecoveryCode = code;
                HasRecoveryCode = true;
                FidoStatus = "Write this code down now. It will not be shown again.";
            }
            else FidoStatus = "Could not generate a recovery code.";
        }
        catch (Exception ex) { FidoStatus = ex.Message; }
        finally { IsFidoBusy = false; }
    }

    [RelayCommand]
    private async Task RemoveSecurityKey(string credentialId)
    {
        if (IsFidoBusy || OnRemoveKeyRequested is null || string.IsNullOrEmpty(credentialId)) return;
        IsFidoBusy = true;
        try
        {
            await OnRemoveKeyRequested.Invoke(credentialId);
            FidoStatus = "Security key removed.";
        }
        catch (Exception ex) { FidoStatus = ex.Message; }
        finally { IsFidoBusy = false; }
    }

    /// <summary>Repopulate the enrolled-key list + recovery flag (called by MainWindow on open / after ops).</summary>
    public void SetSecurityKeys(IEnumerable<Fido2KeyItem> keys, bool hasRecovery, bool backendAvailable)
    {
        SecurityKeys = new ObservableCollection<Fido2KeyItem>(keys);
        HasRecoveryCode = hasRecovery;
        FidoBackendAvailable = backendAvailable;
        OnPropertyChanged(nameof(HasSecurityKeys));
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
    [ObservableProperty] private bool _isRnnoiseInstalling;
    [ObservableProperty] private string _rnnoiseInstallStatus = "";
    [ObservableProperty] private bool _autoInputSensitivity = true;
    [ObservableProperty] private bool _autoGainControl;
    [ObservableProperty] private bool _echoCancellation = true;
    [ObservableProperty] private double _currentInputLevelDb = -100; // live mic level for UI meter

    public string InputVolumeText => $"{(int)InputVolume}%";
    public string OutputVolumeText => $"{(int)OutputVolume}%";
    public string NoiseGateText => AutoInputSensitivity ? "Auto" : $"{(int)NoiseGateThreshold} dB";
    public string NoiseSuppressionStatus => IsNoiseSuppressionAvailable
        ? "RNNoise - removes background noise from your mic"
        : "Not installed";

    public bool ShowRnnoiseInstall => !IsNoiseSuppressionAvailable && !IsRnnoiseInstalling;

    partial void OnIsNoiseSuppressionAvailableChanged(bool value) => OnPropertyChanged(nameof(ShowRnnoiseInstall));
    partial void OnIsRnnoiseInstallingChanged(bool value) => OnPropertyChanged(nameof(ShowRnnoiseInstall));

    public Func<Task>? OnInstallRnnoise;

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
