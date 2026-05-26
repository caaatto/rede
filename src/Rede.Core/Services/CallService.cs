using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rede.Core.Audio;
using Rede.Core.Crypto;
using Rede.Core.Networking;
using Rede.Core.Protocol;
using Rede.Core.Storage;

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
    Direct, // Binary WS over WSS — low latency, server sees IP
    I2P,    // Binary WS over I2P — high latency, anonymous
    Tor,    // Binary WS over Tor — high latency, anonymous
}

public class CallService : IDisposable
{
    private readonly RedeConnection _connection;
    private readonly ProfileStore _store;
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

    public Profile? Profile { get; set; }
    public byte[]? Passphrase { get; set; }

    public CallState State => _state;
    public string? CallId => _callId;
    public string? RemoteUserId => _remoteUserId;
    public CallMode Mode => _callMode;
    public AudioEngine? Audio => _audioEngine;
    public bool IsMuted => _audioEngine?.IsMuted ?? false;
    public TimeSpan Duration => _state == CallState.Connected ? DateTime.UtcNow - _callStartTime : TimeSpan.Zero;

    /// <summary>
    /// The call mode derived from the connection transport.
    /// </summary>
    public CallMode LocalMode => _connection.Transport switch
    {
        "I2P" => CallMode.I2P,
        "Tor" => CallMode.Tor,
        _ => CallMode.Direct,
    };

    // Events
    public event Action<string, string, CallMode>? OnIncomingCall;  // callId, callerId, mode
    public event Action? OnCallConnected;
    public event Action<string>? OnCallEnded;  // reason
    public event Action<string, bool>? OnRemoteMuted;  // userId, isMuted
    public event Action<string>? OnError;

    public CallService(RedeConnection connection, ProfileStore store)
    {
        _connection = connection;
        _store = store;
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
    /// SRTP key material is encrypted via Double Ratchet before sending.
    /// </summary>
    public bool StartCall(string targetUserId)
    {
        if (_state != CallState.Idle)
        {
            OnError?.Invoke("Already in a call");
            return false;
        }

        if (Profile is null || Passphrase is null)
        {
            OnError?.Invoke("Not authenticated");
            return false;
        }

        if (!Profile.Contacts.TryGetValue(targetUserId, out var contact))
        {
            OnError?.Invoke("Contact not found");
            return false;
        }

        _callMode = LocalMode;
        _callId = GenerateCallId();
        _remoteUserId = targetUserId;
        _state = CallState.Offering;

        // Generate SRTP key material
        (_srtpMasterKey, _srtpMasterSalt) = SrtpCrypto.GenerateKeyMaterial();

        // Encrypt the SRTP key material via Double Ratchet for EVERY device the
        // contact has a session with. The server fans CALL_OFFER out to all of
        // the callee's devices — if we only encrypted for one, the call would
        // only connect when the callee happened to answer on that exact device,
        // and every other device would fail to decrypt. The per-device entries
        // are nested under `srtpParams.perDevice`; the first one is also lifted
        // to the top level so an older callee build still finds a single entry.
        var perDevice = new JsonArray();
        foreach (var devId in contact.Devices.Keys)
        {
            var stateJson = _store.LoadRatchetState(Profile, targetUserId, devId);
            if (stateJson is null) continue;

            var ratchetState = JsonSerializer.Deserialize<DoubleRatchet.RatchetState>(stateJson.Value);
            if (ratchetState is null) continue;

            var srtpPlaintext = JsonSerializer.Serialize(new
            {
                srtpKey = Convert.ToBase64String(_srtpMasterKey),
                srtpSalt = Convert.ToBase64String(_srtpMasterSalt),
            });

            var result = DoubleRatchet.Encrypt(ratchetState, srtpPlaintext);

            perDevice.Add(new JsonObject
            {
                ["encrypted"] = result.Ciphertext,
                ["nonce"] = result.Nonce,
                ["header"] = new JsonObject
                {
                    ["dh"] = result.Header.Dh,
                    ["pn"] = result.Header.Pn,
                    ["n"] = result.Header.N,
                },
                ["toDeviceId"] = devId,
            });

            // Save updated ratchet state (debounced) — each device's ratchet advanced.
            var stateElement = JsonSerializer.SerializeToElement(ratchetState);
            _store.SaveRatchetStateAsync(Profile, targetUserId, stateElement, Passphrase, devId);
        }

        // Legacy fallback path. ChatService.SendMessageAsync transparently sends
        // sealed messages through a no-deviceId ratchet state when the contact's
        // Devices map is empty; without the same path here, a contact who can
        // chat fine still can't be called ("No secure session"). We send the
        // SRTP key encrypted under the legacy ratchet at the top level, with no
        // perDevice array — that's exactly the legacy single-device branch the
        // receiver already handles in HandleCallOffer.
        JsonObject? legacySrtpParams = null;
        if (perDevice.Count == 0)
        {
            var legacyStateJson = _store.LoadRatchetState(Profile, targetUserId, null);
            if (legacyStateJson is not null)
            {
                var legacyState = JsonSerializer.Deserialize<DoubleRatchet.RatchetState>(legacyStateJson.Value);
                if (legacyState is not null)
                {
                    var srtpPlaintext = JsonSerializer.Serialize(new
                    {
                        srtpKey = Convert.ToBase64String(_srtpMasterKey),
                        srtpSalt = Convert.ToBase64String(_srtpMasterSalt),
                    });
                    var result = DoubleRatchet.Encrypt(legacyState, srtpPlaintext);
                    legacySrtpParams = new JsonObject
                    {
                        ["encrypted"] = result.Ciphertext,
                        ["nonce"] = result.Nonce,
                        ["header"] = new JsonObject
                        {
                            ["dh"] = result.Header.Dh,
                            ["pn"] = result.Header.Pn,
                            ["n"] = result.Header.N,
                        },
                    };
                    var legacyStateElement = JsonSerializer.SerializeToElement(legacyState);
                    _store.SaveRatchetStateAsync(Profile, targetUserId, legacyStateElement, Passphrase, null);
                }
            }
        }

        if (perDevice.Count == 0 && legacySrtpParams is null)
        {
            _state = CallState.Idle;
            _callId = null;
            _remoteUserId = null;
            OnError?.Invoke("No secure session with this contact. Exchange messages first.");
            return false;
        }

        // Top-level fields = first device (legacy single-device callees);
        // `perDevice` carries all of them for multi-device callees.
        // Legacy fallback: when Devices map is empty we already filled
        // legacySrtpParams (single-device, no perDevice) — send that as-is.
        JsonObject srtpParamsEncrypted;
        if (legacySrtpParams is not null)
        {
            srtpParamsEncrypted = legacySrtpParams;
        }
        else
        {
            var firstEntry = (JsonObject)perDevice[0]!;
            srtpParamsEncrypted = new JsonObject
            {
                ["encrypted"] = firstEntry["encrypted"]!.GetValue<string>(),
                ["nonce"] = firstEntry["nonce"]!.GetValue<string>(),
                ["header"] = (JsonObject)firstEntry["header"]!.DeepClone(),
                ["toDeviceId"] = firstEntry["toDeviceId"]!.GetValue<string>(),
                ["perDevice"] = perDevice,
            };
        }

        var payload = new JsonObject
        {
            ["to"] = targetUserId,
            ["callId"] = _callId,
            ["mode"] = _callMode.ToString().ToLowerInvariant(),
            ["srtpParams"] = srtpParamsEncrypted,
        };

        _connection.Send(Msg.CallOffer, payload);

        // 30s timeout for offer
        _offerTimeout = new System.Timers.Timer(30000);
        _offerTimeout.AutoReset = false;
        _offerTimeout.Elapsed += (_, _) =>
        {
            if (_state == CallState.Offering)
            {
                EndCall("Timeout - no answer");
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
        // EndCall (not Reset) — Reset only clears state and never fires
        // OnCallEnded, so the CallView stayed visible until the user accepted
        // and then hung up to trigger a real teardown.
        EndCall("Rejected");
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

        // C4: Reject calls from non-contacts
        if (string.IsNullOrEmpty(incomingFrom) || Profile is null || !Profile.Contacts.ContainsKey(incomingFrom))
            return;

        var modeStr = msg["mode"]?.GetValue<string>();
        var incomingMode = modeStr switch
        {
            "i2p" => CallMode.I2P,
            "tor" => CallMode.Tor,
            // Accept legacy "fast"/"secure" and "direct"
            "direct" or "fast" => CallMode.Direct,
            _ => CallMode.I2P,
        };

        // Decrypt SRTP params from Double Ratchet envelope
        byte[]? srtpKey = null;
        byte[]? srtpSalt = null;

        var srtpParams = msg["srtpParams"]?.AsObject();

        // Multi-device: the caller encrypts the SRTP key once per callee device.
        // Pick the entry meant for THIS device out of srtpParams.perDevice. A
        // device that has no entry simply isn't the target — it must stay
        // completely silent. If it auto-rejected with crypto_error instead, that
        // reject would race the real CALL_ANSWER and tear the call down on the
        // caller's side (caller dropped, callee connected — the reported bug).
        var ownDeviceId = Profile?.DeviceId;
        var perDevice = srtpParams?["perDevice"]?.AsArray();
        if (perDevice is not null)
        {
            JsonObject? mine = null;
            foreach (var node in perDevice)
            {
                if (node is JsonObject entry
                    && entry["toDeviceId"]?.GetValue<string>() == ownDeviceId)
                {
                    mine = entry;
                    break;
                }
            }
            if (mine is null) return; // Offer carries no entry for this device.
            srtpParams = mine;
        }
        else
        {
            // Legacy single-device offer — ignore silently if it targets another device.
            var legacyTarget = srtpParams?["toDeviceId"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(legacyTarget)
                && !string.IsNullOrEmpty(ownDeviceId)
                && legacyTarget != ownDeviceId)
            {
                return;
            }
        }

        if (srtpParams is not null && Profile is not null && Passphrase is not null && incomingFrom is not null)
        {
            var encrypted = srtpParams["encrypted"]?.GetValue<string>();
            var nonce = srtpParams["nonce"]?.GetValue<string>();
            var headerNode = srtpParams["header"];
            var fromDeviceId = msg["fromDeviceId"]?.GetValue<string>();

            if (encrypted is not null && nonce is not null && headerNode is not null)
            {
                // H9: Validate DH header field
                var dhVal = headerNode["dh"]?.GetValue<string>();
                if (string.IsNullOrEmpty(dhVal)) return;

                var header = new DoubleRatchet.RatchetHeader(
                    dhVal,
                    headerNode["pn"]?.GetValue<int>() ?? 0,
                    headerNode["n"]?.GetValue<int>() ?? 0
                );

                // Try to decrypt with ratchet state for the caller
                var stateJson = _store.LoadRatchetState(Profile, incomingFrom, fromDeviceId);
                if (stateJson is not null)
                {
                    var ratchetState = JsonSerializer.Deserialize<DoubleRatchet.RatchetState>(stateJson.Value);
                    if (ratchetState is not null)
                    {
                        var backup = ratchetState.DeepClone();
                        var plaintext = DoubleRatchet.Decrypt(ratchetState, header, encrypted, nonce);

                        if (plaintext is not null)
                        {
                            try
                            {
                                var doc = JsonDocument.Parse(plaintext);
                                var keyB64 = doc.RootElement.GetProperty("srtpKey").GetString();
                                var saltB64 = doc.RootElement.GetProperty("srtpSalt").GetString();
                                if (keyB64 is not null && saltB64 is not null)
                                {
                                    srtpKey = Convert.FromBase64String(keyB64);
                                    srtpSalt = Convert.FromBase64String(saltB64);
                                    // H4: Validate SRTP key/salt lengths
                                    if (srtpKey.Length < 16 || srtpSalt.Length < 14)
                                    {
                                        srtpKey = null;
                                        srtpSalt = null;
                                    }
                                }
                            }
                            catch { }

                            // Save updated ratchet state (debounced)
                            var stateElement = JsonSerializer.SerializeToElement(ratchetState);
                            _store.SaveRatchetStateAsync(Profile, incomingFrom, stateElement, Passphrase, fromDeviceId);
                        }
                        else
                        {
                            // Decrypt failed — restore backup
                            var backupJson = JsonSerializer.SerializeToElement(backup);
                            _store.SaveRatchetStateAsync(Profile, incomingFrom, backupJson, Passphrase, fromDeviceId);
                        }
                    }
                }
            }
        }

        if (srtpKey is null || srtpSalt is null)
        {
            // Can't decrypt SRTP keys — reject call
            _connection.Send(Msg.CallReject, new JsonObject
            {
                ["to"] = incomingFrom,
                ["callId"] = incomingCallId,
                ["reason"] = "crypto_error",
            });
            return;
        }

        _callId = incomingCallId;
        _remoteUserId = incomingFrom;
        _callMode = incomingMode;
        _srtpMasterKey = srtpKey;
        _srtpMasterSalt = srtpSalt;

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
        // A reject is only meaningful while we're still waiting for an answer.
        // The callee's devices ring in parallel; once one has answered and we're
        // Connected, a late reject from a *different* device of the callee (the
        // user dismissing the stale ring) must not tear down the live call.
        if (_state != CallState.Offering) return;
        EndCall("Call rejected");
    }

    private void HandleCallBusy(JsonObject msg)
    {
        var callId = msg["callId"]?.GetValue<string>();
        if (callId != _callId) return;
        if (_state != CallState.Offering) return;
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
        if (userId != _remoteUserId) return; // Verify sender matches call peer
        var muted = msg["muted"]?.GetValue<bool>() ?? false;
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

        // Apply saved audio settings from profile
        if (Profile is not null)
        {
            _audioEngine.InputVolume = Profile.InputVolume;
            _audioEngine.OutputVolume = Profile.OutputVolume;
            _audioEngine.NoiseGateThreshold = Profile.NoiseGateThreshold;
            _audioEngine.NoiseSuppression = Profile.NoiseSuppression;
            _audioEngine.AutoInputSensitivity = Profile.AutoInputSensitivity;
            _audioEngine.AutoGainControl = Profile.AutoGainControl;
            _audioEngine.EchoCancellation = Profile.EchoCancellation;
        }

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
