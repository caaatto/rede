using System;
using System.Timers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rede.Core.Services;

namespace Rede.Desktop.ViewModels;

public partial class CallViewModel : ViewModelBase
{
    private CallService? _callService;
    private Timer? _durationTimer;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _isIncoming;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private string _remoteUser = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _durationText = "00:00";
    [ObservableProperty] private string _modeIndicator = "";
    [ObservableProperty] private string _modeTooltip = "";

    public void Init(CallService callService)
    {
        _callService = callService;
        _callService.OnIncomingCall += HandleIncomingCall;
        _callService.OnCallConnected += HandleCallConnected;
        _callService.OnCallEnded += HandleCallEnded;
        _callService.OnRemoteMuted += HandleRemoteMuted;
    }

    private void HandleIncomingCall(string callId, string callerId, CallMode mode)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RemoteUser = callerId;
            ModeIndicator = mode == CallMode.Secure ? "\ud83d\udd12" : "\u26a1";
            ModeTooltip = mode == CallMode.Secure ? "Secure (I2P)" : "Fast (Direct)";
            StatusText = "Incoming call...";
            IsIncoming = true;
            IsConnected = false;
            IsVisible = true;
        });
    }

    private void HandleCallConnected()
    {
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
        _callService?.AcceptCall();
    }

    [RelayCommand]
    private void RejectCall()
    {
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

    public void StartOutgoingCall(string targetUser, CallMode mode)
    {
        if (_callService is null) return;

        RemoteUser = targetUser;
        ModeIndicator = mode == CallMode.Secure ? "\ud83d\udd12" : "\u26a1";
        ModeTooltip = mode == CallMode.Secure ? "Secure (I2P)" : "Fast (Direct)";
        StatusText = "Calling...";
        IsIncoming = false;
        IsConnected = false;
        IsVisible = true;

        _callService.StartCall(targetUser, mode);
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
