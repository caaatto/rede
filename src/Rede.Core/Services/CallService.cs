using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Rede.Core.Audio;
using Rede.Core.Networking;
using Rede.Core.Protocol;

namespace Rede.Core.Services;

public enum CallState
{
    Idle,
    Offering,
    Ringing,
    Connecting,
    Connected,
}

public enum CallMode
{
    Fast,   // WebRTC/UDP — low latency, SFU sees IP
    Secure, // Binary WS over I2P — high latency, anonymous
}

public class CallService : IDisposable
{
    private readonly RedeConnection _connection;
    private AudioEngine? _audioEngine;
    private SrtpSession? _srtpSession;

    private CallState _state = CallState.Idle;
    private string? _callId;
    private string? _remoteUserId;
    private CallMode _callMode;
    private byte[]? _srtpMasterKey;
    private byte[]? _srtpMasterSalt;
    private DateTime _callStartTime;
    private System.Timers.Timer? _offerTimeout;

    public CallState State => _state;
    public string? CallId => _callId;
    public string? RemoteUserId => _remoteUserId;
    public CallMode Mode => _callMode;
    public bool IsMuted => _audioEngine?.IsMuted ?? false;
    public TimeSpan Duration => _state == CallState.Connected ? DateTime.UtcNow - _callStartTime : TimeSpan.Zero;

    // Events
    public event Action<string, string, CallMode>? OnIncomingCall;  // callId, callerId, mode
    public event Action? OnCallConnected;
    public event Action<string>? OnCallEnded;  // reason
    public event Action<string, bool>? OnRemoteMuted;  // userId, isMuted
    public event Action<string>? OnError;

    /// <summary>
    /// Default call mode for outgoing calls. User can change in settings.
    /// </summary>
    public CallMode DefaultMode { get; set; } = CallMode.Secure;

    /// <summary>
    /// If false, incoming fast-mode calls are auto-rejected.
    /// </summary>
    public bool AllowFastCalls { get; set; } = true;

    public CallService(RedeConnection connection)
    {
        _connection = connection;
        RegisterHandlers();
    }

    private void RegisterHandlers()
    {
        _connection.On(Msg.CallOffer, HandleCallOffer);
        _connection.On(Msg.CallAnswer, HandleCallAnswer);
        _connection.On(Msg.CallReject, HandleCallReject);
        _connection.On(Msg.CallBusy, HandleCallBusy);
        _connection.On(Msg.CallRinging, HandleCallRinging);
        _connection.On(Msg.CallHangup, HandleCallHangup);
        _connection.On(Msg.CallIce, HandleCallIce);
        _connection.On(Msg.CallMute, HandleCallMute);
        _connection.OnBinaryMessage += HandleBinaryFrame;
    }

    /// <summary>
    /// Initiate a call to a target user.
    /// SRTP key material is generated locally and must be encrypted via Double Ratchet before sending.
    /// The encrypted srtpParams should be set by the caller on the payload.
    /// </summary>
    public bool StartCall(string targetUserId, CallMode? mode = null)
    {
        if (_state != CallState.Idle)
        {
            OnError?.Invoke("Already in a call");
            return false;
        }

        _callMode = mode ?? DefaultMode;
        _callId = GenerateCallId();
        _remoteUserId = targetUserId;
        _state = CallState.Offering;

        // Generate SRTP key material
        (_srtpMasterKey, _srtpMasterSalt) = SrtpCrypto.GenerateKeyMaterial();

        var payload = new JsonObject
        {
            ["to"] = targetUserId,
            ["callId"] = _callId,
            ["mode"] = _callMode == CallMode.Fast ? "fast" : "secure",
            ["srtpKey"] = Convert.ToBase64String(_srtpMasterKey),
            ["srtpSalt"] = Convert.ToBase64String(_srtpMasterSalt),
        };

        _connection.Send(Msg.CallOffer, payload);

        // 30s timeout for offer
        _offerTimeout = new System.Timers.Timer(30000);
        _offerTimeout.AutoReset = false;
        _offerTimeout.Elapsed += (_, _) =>
        {
            if (_state == CallState.Offering)
            {
                EndCall("Timeout — no answer");
            }
        };
        _offerTimeout.Start();

        return true;
    }

    /// <summary>
    /// Accept an incoming call.
    /// </summary>
    public bool AcceptCall()
    {
        if (_state != CallState.Ringing || _callId is null)
            return false;

        var payload = new JsonObject
        {
            ["to"] = _remoteUserId,
            ["callId"] = _callId,
        };

        _connection.Send(Msg.CallAnswer, payload);
        StartAudio();
        return true;
    }

    /// <summary>
    /// Reject an incoming call.
    /// </summary>
    public bool RejectCall()
    {
        if (_state != CallState.Ringing || _callId is null)
            return false;

        var payload = new JsonObject
        {
            ["to"] = _remoteUserId,
            ["callId"] = _callId,
        };

        _connection.Send(Msg.CallReject, payload);
        Reset("Rejected");
        return true;
    }

    /// <summary>
    /// Hang up the current call.
    /// </summary>
    public void HangUp()
    {
        if (_state == CallState.Idle) return;

        if (_callId is not null)
        {
            var payload = new JsonObject
            {
                ["to"] = _remoteUserId,
                ["callId"] = _callId,
            };
            _connection.Send(Msg.CallHangup, payload);
        }

        EndCall("You hung up");
    }

    /// <summary>
    /// Toggle mute state.
    /// </summary>
    public void SetMuted(bool muted)
    {
        if (_audioEngine is not null)
            _audioEngine.IsMuted = muted;

        if (_callId is not null && _state == CallState.Connected)
        {
            _connection.Send(Msg.CallMute, new JsonObject
            {
                ["to"] = _remoteUserId,
                ["callId"] = _callId,
                ["muted"] = muted,
            });
        }
    }

    // --- Incoming message handlers ---

    private void HandleCallOffer(JsonObject msg)
    {
        if (_state != CallState.Idle)
        {
            // Already in a call — send busy
            var busyPayload = new JsonObject
            {
                ["to"] = msg["from"]?.GetValue<string>(),
                ["callId"] = msg["callId"]?.GetValue<string>(),
            };
            _connection.Send(Msg.CallBusy, busyPayload);
            return;
        }

        var incomingCallId = msg["callId"]?.GetValue<string>();
        var incomingFrom = msg["from"]?.GetValue<string>();
        var modeStr = msg["mode"]?.GetValue<string>();
        var incomingMode = modeStr == "fast" ? CallMode.Fast : CallMode.Secure;

        // Auto-reject fast calls if user disabled them
        if (incomingMode == CallMode.Fast && !AllowFastCalls)
        {
            _connection.Send(Msg.CallReject, new JsonObject
            {
                ["to"] = incomingFrom,
                ["callId"] = incomingCallId,
                ["reason"] = "mode_mismatch",
            });
            return;
        }

        _callId = incomingCallId;
        _remoteUserId = incomingFrom;
        _callMode = incomingMode;

        // Extract SRTP params (in real use, these would be encrypted in the Double Ratchet payload)
        var keyB64 = msg["srtpKey"]?.GetValue<string>();
        var saltB64 = msg["srtpSalt"]?.GetValue<string>();
        if (keyB64 is not null && saltB64 is not null)
        {
            _srtpMasterKey = Convert.FromBase64String(keyB64);
            _srtpMasterSalt = Convert.FromBase64String(saltB64);
        }

        _state = CallState.Ringing;

        // Send ringing notification
        _connection.Send(Msg.CallRinging, new JsonObject
        {
            ["to"] = _remoteUserId,
            ["callId"] = _callId,
        });

        OnIncomingCall?.Invoke(_callId!, _remoteUserId!, _callMode);
    }

    private void HandleCallAnswer(JsonObject msg)
    {
        var callId = msg["callId"]?.GetValue<string>();
        if (_state != CallState.Offering || callId != _callId) return;

        _offerTimeout?.Stop();
        StartAudio();
    }

    private void HandleCallReject(JsonObject msg)
    {
        var callId = msg["callId"]?.GetValue<string>();
        if (callId != _callId) return;
        EndCall("Call rejected");
    }

    private void HandleCallBusy(JsonObject msg)
    {
        var callId = msg["callId"]?.GetValue<string>();
        if (callId != _callId) return;
        EndCall("User is busy");
    }

    private void HandleCallRinging(JsonObject msg)
    {
        var callId = msg["callId"]?.GetValue<string>();
        if (_state != CallState.Offering || callId != _callId) return;
        // Ringing confirmation — UI can show "Ringing..." indicator
    }

    private void HandleCallHangup(JsonObject msg)
    {
        var callId = msg["callId"]?.GetValue<string>();
        if (callId != _callId) return;
        EndCall("Remote hung up");
    }

    private void HandleCallIce(JsonObject msg)
    {
        // ICE candidates for fast mode (WebRTC) — future implementation
    }

    private void HandleCallMute(JsonObject msg)
    {
        var callId = msg["callId"]?.GetValue<string>();
        if (callId != _callId) return;

        var userId = msg["from"]?.GetValue<string>();
        var muted = msg["muted"]?.GetValue<bool>() ?? false;
        if (userId is not null)
            OnRemoteMuted?.Invoke(userId, muted);
    }

    private void HandleBinaryFrame(byte[] data)
    {
        if (_state != CallState.Connected || _srtpSession is null || _audioEngine is null)
            return;

        // Decrypt SRTP → RTP, extract Opus payload, queue for playback
        var rtpPacket = _srtpSession.Unprotect(data);
        if (rtpPacket is null) return;

        // RTP payload starts after header
        int headerLen = GetRtpHeaderLength(rtpPacket);
        if (headerLen >= rtpPacket.Length) return;

        var opusFrame = new byte[rtpPacket.Length - headerLen];
        Buffer.BlockCopy(rtpPacket, headerLen, opusFrame, 0, opusFrame.Length);
        _audioEngine.QueuePlayback(opusFrame);
    }

    // --- Audio pipeline ---

    private void StartAudio()
    {
        _state = CallState.Connecting;

        if (_srtpMasterKey is null || _srtpMasterSalt is null)
        {
            EndCall("Missing SRTP key material");
            return;
        }

        _srtpSession = new SrtpSession(_srtpMasterKey, _srtpMasterSalt);
        _audioEngine = new AudioEngine();
        _audioEngine.OnEncodedFrame += OnLocalAudioFrame;

        try
        {
            _audioEngine.Start();
        }
        catch (Exception ex)
        {
            EndCall($"Audio init failed: {ex.Message}");
            return;
        }

        _state = CallState.Connected;
        _callStartTime = DateTime.UtcNow;
        OnCallConnected?.Invoke();
    }

    private ushort _rtpSeq;
    private uint _rtpTimestamp;
    private uint _rtpSsrc = (uint)Random.Shared.Next();

    private void OnLocalAudioFrame(byte[] opusFrame)
    {
        if (_srtpSession is null) return;

        // Build RTP packet: 12-byte header + Opus payload
        var rtp = new byte[12 + opusFrame.Length];
        rtp[0] = 0x80; // V=2, no padding/extension/CSRC
        rtp[1] = 111;  // PT=111 (Opus dynamic)
        rtp[2] = (byte)(_rtpSeq >> 8);
        rtp[3] = (byte)(_rtpSeq);
        _rtpTimestamp += AudioEngine.FrameSize;
        rtp[4] = (byte)(_rtpTimestamp >> 24);
        rtp[5] = (byte)(_rtpTimestamp >> 16);
        rtp[6] = (byte)(_rtpTimestamp >> 8);
        rtp[7] = (byte)(_rtpTimestamp);
        rtp[8] = (byte)(_rtpSsrc >> 24);
        rtp[9] = (byte)(_rtpSsrc >> 16);
        rtp[10] = (byte)(_rtpSsrc >> 8);
        rtp[11] = (byte)(_rtpSsrc);
        Buffer.BlockCopy(opusFrame, 0, rtp, 12, opusFrame.Length);
        _rtpSeq++;

        // Encrypt with SRTP
        var srtpPacket = _srtpSession.Protect(rtp);

        // Send as binary WebSocket frame
        _ = _connection.SendBinaryAsync(srtpPacket);
    }

    // --- Cleanup ---

    private void EndCall(string reason)
    {
        _offerTimeout?.Stop();
        _offerTimeout?.Dispose();
        _offerTimeout = null;

        _audioEngine?.Stop();
        _audioEngine?.Dispose();
        _audioEngine = null;

        _srtpSession?.Dispose();
        _srtpSession = null;

        if (_srtpMasterKey is not null) Array.Clear(_srtpMasterKey);
        if (_srtpMasterSalt is not null) Array.Clear(_srtpMasterSalt);

        var wasConnected = _state == CallState.Connected;
        Reset(reason);

        OnCallEnded?.Invoke(reason);
    }

    private void Reset(string reason)
    {
        _state = CallState.Idle;
        _callId = null;
        _remoteUserId = null;
        _srtpMasterKey = null;
        _srtpMasterSalt = null;
        _rtpSeq = 0;
        _rtpTimestamp = 0;
    }

    private static string GenerateCallId()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    private static int GetRtpHeaderLength(byte[] packet)
    {
        if (packet.Length < 12) return 12;
        int cc = packet[0] & 0x0F;
        int len = 12 + cc * 4;
        if ((packet[0] & 0x10) != 0 && packet.Length >= len + 4)
        {
            int extLen = (packet[len + 2] << 8) | packet[len + 3];
            len += 4 + extLen * 4;
        }
        return Math.Min(len, packet.Length);
    }

    public void Dispose()
    {
        if (_state != CallState.Idle)
            HangUp();
        _audioEngine?.Dispose();
        _srtpSession?.Dispose();
        _offerTimeout?.Dispose();
    }
}
