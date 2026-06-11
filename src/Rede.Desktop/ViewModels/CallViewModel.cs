using System;
using System.Timers;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rede.Core.Services;

namespace Rede.Desktop.ViewModels;

public partial class CallViewModel : ViewModelBase
{
    private CallService? _callService;
    private NotificationService? _notifications;
    private Timer? _durationTimer;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _isIncoming;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private string _remoteUser = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _durationText = "00:00";
    [ObservableProperty] private IImage? _modeIcon;
    [ObservableProperty] private string _modeTooltip = "";

    public void Init(CallService callService, NotificationService? notifications = null)
    {
        _callService = callService;
        _notifications = notifications;
        _callService.OnIncomingCall += HandleIncomingCall;
        _callService.OnCallConnected += HandleCallConnected;
        _callService.OnCallEnded += HandleCallEnded;
        _callService.OnRemoteMuted += HandleRemoteMuted;
    }

    private static (IImage? Icon, string Tooltip) ModeDisplay(CallMode mode)
    {
        var tooltip = mode switch
        {
            CallMode.I2P => "Anonymous (I2P)",
            CallMode.Tor => "Anonymous (Tor)",
            CallMode.Direct => "Direct (WSS)",
            _ => "Encrypted",
        };
        var iconKey = mode == CallMode.Direct ? "IconSignal" : "IconLock";
        Application.Current!.Resources.TryGetResource(iconKey, null, out var res);
        return (res as IImage, tooltip);
    }

    private void HandleIncomingCall(string callId, string callerId, CallMode mode)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RemoteUser = callerId;
            var (icon, tip) = ModeDisplay(mode);
            ModeIcon = icon;
            ModeTooltip = tip;
            StatusText = "Incoming call...";
            IsIncoming = true;
            IsConnected = false;
            IsVisible = true;
        });
        // Ring until the call is answered, declined, or ends/times out.
        _notifications?.StartRingtone();
    }

    private void HandleCallConnected()
    {
        _notifications?.StopRingtone();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            StatusText = "Connected";
            IsIncoming = false;
            IsConnected = true;
            StartDurationTimer();
        });
    }

    private void HandleCallEnded(string reason)
    {
        _notifications?.StopRingtone();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            StopDurationTimer();
            IsVisible = false;
            IsConnected = false;
            IsIncoming = false;
            IsMuted = false;
            DurationText = "00:00";
        });
    }

    private void HandleRemoteMuted(string userId, bool muted)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (muted)
                StatusText = $"{userId} muted";
            else
                StatusText = "Connected";
        });
    }

    [RelayCommand]
    private void AcceptCall()
    {
        _notifications?.StopRingtone();
        _callService?.AcceptCall();
    }

    [RelayCommand]
    private void RejectCall()
    {
        _notifications?.StopRingtone();
        _callService?.RejectCall();
    }

    [RelayCommand]
    private void HangUp()
    {
        _callService?.HangUp();
    }

    [RelayCommand]
    private void ToggleMute()
    {
        if (_callService is null) return;
        IsMuted = !IsMuted;
        _callService.SetMuted(IsMuted);
    }

    public void StartOutgoingCall(string targetUser)
    {
        if (_callService is null) return;

        RemoteUser = targetUser;
        var (icon, tip) = ModeDisplay(_callService.LocalMode);
        ModeIcon = icon;
        ModeTooltip = tip;
        StatusText = "Calling...";
        IsIncoming = false;
        IsConnected = false;
        IsVisible = true;

        _callService.StartCall(targetUser);
    }

    private void StartDurationTimer()
    {
        _durationTimer = new Timer(1000);
        _durationTimer.Elapsed += (_, _) =>
        {
            if (_callService is null) return;
            var d = _callService.Duration;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                DurationText = d.Hours > 0
                    ? $"{d.Hours:D2}:{d.Minutes:D2}:{d.Seconds:D2}"
                    : $"{d.Minutes:D2}:{d.Seconds:D2}";
            });
        };
        _durationTimer.Start();
    }

    private void StopDurationTimer()
    {
        _durationTimer?.Stop();
        _durationTimer?.Dispose();
        _durationTimer = null;
    }
}
