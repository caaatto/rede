using System.Text.Json;
using System.Text.Json.Nodes;
using Rede.Core.Crypto;
using Rede.Core.Networking;
using Rede.Core.Protocol;
using Rede.Core.Storage;

namespace Rede.Core.Services;

/// <summary>
/// Handles 1:1 messaging (send/receive with Double Ratchet) and sealed sender.
/// Mirrors: sendRatcheted, receiveRatcheted, trySealedSend, MESSAGE handler in index.js
/// </summary>
public class ChatService : IDisposable
{
    private readonly RedeConnection _conn;
    private readonly ProfileStore _store;
    private readonly NonceTracker _nonceTracker = new();

    /// <summary>
    /// Replay-protection tracker. Exposed so MainWindow can import/export the
    /// persisted nonce snapshot across restarts.
    /// </summary>
    public NonceTracker NonceTracker => _nonceTracker;

    public void Dispose()
    {
        // Services are re-created per login. The underlying RedeConnection owns the
        // message-handler delegates and is disposed separately, which drops strong
        // references back to this service. Dispose is provided so InitServices can
        // signal end-of-life deterministically and so future refactors can attach
        // per-service cleanup (CTS, timers, file handles) without API changes.
        GC.SuppressFinalize(this);
    }

    public Profile? Profile { get; set; }
    public byte[]? Passphrase { get; set; }

    // H7: Queue per target — multiple messages can be pending before bundle arrives
    private readonly Dictionary<string, List<(string Text, int Ttl)>> _pendingOutgoing = new();

    // ACK FIFO: every successful WS send (per device, including control messages) pushes
    // one entry. On each msgId ACK we dequeue the head and stamp the referenced ChatMessage's
    // MsgId — this is the only reliable pairing since sealed ACKs carry no `to` field and
    // scanning ChatHistory collides with legacy orphans.
    // Control messages push (chatKey, Msg=null) sentinels so they consume their own ACK slot
    // and don't steal from the next user-visible message. Multi-device fan-out pushes N
    // entries all pointing at the same ChatMessage — the first ACK stamps MsgId, subsequent
    // ACKs are consumed but skip stamping (MsgId already set).
    private readonly Queue<(string ChatKey, ChatMessage? Msg, bool Sealed)> _pendingAck = new();
    private readonly object _pendingAckLock = new();
    private const int MaxPendingAck = 500;

    /// <summary>Drop all pending ACK slots — called on disconnect so post-reconnect ACKs
    /// can't mispair with stale entries from the previous session.</summary>
    public void ClearPendingAcks()
    {
        lock (_pendingAckLock) _pendingAck.Clear();
    }

    public event Action<string, string, string, DateTime, bool, string?>? OnMessageReceived; // from, text, chatId, timestamp, isSealed, msgId
    public event Action<string>? OnSystemMessage;
    public event Action<string, string, int>? OnMessageSent; // chatId, text, ttl
    public event Action<string, string>? OnOwnMessageIdAssigned; // contactId, msgId
    public event Action<string, string, string, Dictionary<string, List<string>>>? OnReactionUpdated; // chatId, msgId, emoji, reactions
    public event Action<string, string, string, string, string>? OnGroupKeyReceived; // groupId, name, key, sig, senderId
    public event Action<string, string, string, string>? OnPlaceKeyReceived; // placeId, metadataKey, encryptedMetadata, senderId
    public event Action<string, string?, string?, string?>? OnProfileReceived; // senderId, accentColor, avatarData, avatarMimeType
    public event Action<string, string, string, string>? OnNewDeviceDetected; // targetUserId, deviceId, publicKey, signingKey

    public ChatService(RedeConnection conn, ProfileStore store)
    {
        _conn = conn;
        _store = store;
        RegisterHandlers();
    }

    private void RegisterHandlers()
    {
        _conn.On(Msg.Message, HandleMessage);
        _conn.On(Msg.MessageAck, HandleMessageAck);
        _conn.On(Msg.SealedMessage, HandleSealedMessage);
        _conn.On(Msg.SealedMessageAck, HandleSealedMessageAck);
        _conn.On(Msg.PrekeyBundle, HandlePrekeyBundle);
        _conn.On(Msg.PrekeyBundleFail, HandlePrekeyBundleFail);
        _conn.On(Msg.PendingMessages, HandlePendingMessages);
    }

    /// <summary>
    /// Send a 1:1 message using Double Ratchet with multi-device fan-out.
    /// Mirrors: sendRatcheted(targetId, text, ttl) in index.js
    /// </summary>
    /// <summary>
    /// Broadcast profile customization (accent color, avatar) to all contacts as a control message.
    /// </summary>
    public void BroadcastProfile(string? accentColor, string? avatarData, string? avatarMimeType)
    {
        if (Profile is null || Passphrase is null) return;

        var text = BuildProfilePayload(accentColor, avatarData, avatarMimeType);
        if (text is null) return;

        // Send to all contacts in background — avoids blocking UI with many contacts
        var contactIds = Profile.Contacts.Keys.ToList();
        Task.Run(() =>
        {
            foreach (var contactId in contactIds)
                SendMessage(contactId, text, 0);
        });
    }

    /// <summary>Send own profile to a single contact (e.g. after adding them).</summary>
    public void SendProfileTo(string contactId, string? accentColor, string? avatarData, string? avatarMimeType)
    {
        if (Profile is null || Passphrase is null) return;

        var text = BuildProfilePayload(accentColor, avatarData, avatarMimeType);
        if (text is null) return;

        Task.Run(() => SendMessage(contactId, text, 0));
    }

    private static string? BuildProfilePayload(string? accentColor, string? avatarData, string? avatarMimeType)
    {
        // M12: Validate avatar size before broadcasting
        if (avatarData is not null && avatarData.Length > 350_000) // ~256KB decoded = ~350KB base64
            return null;

        var payload = new System.Text.Json.Nodes.JsonObject
        {
            ["__rede_ctrl"] = "profile",
        };
        if (accentColor is not null) payload["accentColor"] = accentColor;
        if (avatarData is not null) payload["avatarData"] = avatarData;
        if (avatarMimeType is not null) payload["avatarMimeType"] = avatarMimeType;

        return payload.ToJsonString();
    }

    /// <summary>Send a reaction on a 1:1 message.</summary>
    public void SendReaction(string contactId, string msgId, string emoji, bool add)
    {
        if (Profile is null || Passphrase is null) return;
        var controlText = MessageEnvelope.EncodeReaction(msgId, emoji, add);
        SendMessage(contactId, controlText, 0);
        ApplyReaction(contactId, msgId, emoji, Profile.UserId, add);
    }

    private void ApplyReaction(string chatKey, string msgId, string emoji, string userId, bool add)
    {
        if (Profile is null || Passphrase is null) return;
        if (!Profile.ChatHistory.TryGetValue(chatKey, out var messages)) return;

        var target = messages.FirstOrDefault(m => m.MsgId == msgId);
        if (target is null) return;

        target.Reactions ??= new();

        if (add)
        {
            if (!target.Reactions.TryGetValue(emoji, out var users))
            {
                users = new List<string>();
                target.Reactions[emoji] = users;
            }
            if (!users.Contains(userId))
                users.Add(userId);
        }
        else
        {
            if (target.Reactions.TryGetValue(emoji, out var users))
            {
                users.Remove(userId);
                if (users.Count == 0)
                    target.Reactions.Remove(emoji);
            }
        }

        _store.SaveChatHistoryDebounced(Profile, Passphrase);
        OnReactionUpdated?.Invoke(chatKey, msgId, emoji, target.Reactions);
    }

    public void SendMessage(string targetId, string text, int ttl = 0)
    {
        if (Profile is null || Passphrase is null) return;

        if (!Profile.Contacts.TryGetValue(targetId, out var contact))
        {
            OnSystemMessage?.Invoke("Contact not found");
            return;
        }

        var devices = contact.Devices;
        var deviceIds = devices.Keys.ToList();

        var needBundle = new List<string>();
        var haveSessions = new List<string?>();

        foreach (var devId in deviceIds)
        {
            if (_store.LoadRatchetState(Profile, targetId, devId) is not null)
                haveSessions.Add(devId);
            else
                needBundle.Add(devId);
        }

        // Legacy (no device ID) ratchet check
        if (deviceIds.Count == 0 && _store.LoadRatchetState(Profile, targetId) is not null)
            haveSessions.Add(null);

        // Pre-create the persisted message (null for control messages so ACKs consume
        // their own slot without stamping anything). The same reference is pushed onto
        // _pendingAck once per successful WS send — first ACK stamps MsgId, rest are no-ops.
        bool isControl = text.Contains("\"__rede_ctrl\"");
        ChatMessage? persistedMsg = null;
        if (haveSessions.Count > 0 && !isControl)
        {
            persistedMsg = new ChatMessage
            {
                From = Profile.UserId,
                Text = text,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Ttl = ttl,
            };
        }

        bool sentAny = false;
        foreach (var devId in haveSessions)
        {
            if (SendToDevice(targetId, devId, text, ttl, contact, persistedMsg))
                sentAny = true;
        }

        // Persist once after the fan-out loop (only if at least one device actually sent).
        if (sentAny && persistedMsg is not null)
        {
            _store.AddChatMessage(Profile, targetId, persistedMsg, Passphrase);
            OnMessageSent?.Invoke(targetId, text, ttl);
        }

        // Fetch pre-key bundles for devices without sessions
        if (needBundle.Count > 0)
        {
            if (!_pendingOutgoing.ContainsKey(targetId))
                _pendingOutgoing[targetId] = new();
            // H9: Bound pending queue to prevent memory exhaustion
            if (_pendingOutgoing[targetId].Count >= 100)
            {
                OnSystemMessage?.Invoke("Too many pending messages - wait for session to establish.");
                return;
            }
            _pendingOutgoing[targetId].Add((text, ttl));
            _conn.Send(Msg.FetchPrekeyBundle, ProtocolSerializer.Payload(
                ("targetUserId", JsonValue.Create(targetId))
            ));
            if (haveSessions.Count == 0)
                OnSystemMessage?.Invoke("Establishing secure session...");
        }
    }

    private bool SendToDevice(string targetId, string? devId, string text, int ttl, Contact contact, ChatMessage? persistedMsg)
    {
        if (Profile is null || Passphrase is null) return false;

        var stateJson = _store.LoadRatchetState(Profile, targetId, devId);
        if (stateJson is null) return false;

        var state = JsonSerializer.Deserialize<DoubleRatchet.RatchetState>(stateJson.Value);
        if (state is null) return false;

        var backup = state.DeepClone();
        var result = DoubleRatchet.Encrypt(state, text);

        var msgPayload = ProtocolSerializer.Payload(
            ("encrypted", JsonValue.Create(result.Ciphertext)),
            ("nonce", JsonValue.Create(result.Nonce)),
            ("header", new JsonObject
            {
                ["dh"] = result.Header.Dh,
                ["pn"] = result.Header.Pn,
                ["n"] = result.Header.N,
            })
        );

        // Try sealed sender first
        bool sent = TrySealedSend(targetId, devId, msgPayload, ttl, contact);
        bool wasSealed = sent;
        if (!sent)
        {
            // Fallback to normal send
            msgPayload["to"] = targetId;
            if (devId is not null) msgPayload["toDeviceId"] = devId;
            if (ttl > 0) msgPayload["ttl"] = ttl;
            sent = _conn.Send(Msg.Message, msgPayload);
        }

        if (!sent)
        {
            // Restore ratchet state on failure
            var backupJson = JsonSerializer.SerializeToElement(backup);
            _store.SaveRatchetStateAsync(Profile, targetId, backupJson, Passphrase, devId);
            OnSystemMessage?.Invoke("Message not sent - connection lost. Ratchet state preserved.");
            return false;
        }

        var stateElement = JsonSerializer.SerializeToElement(state);
        _store.SaveRatchetStateAsync(Profile, targetId, stateElement, Passphrase, devId);

        // One queue entry per WS send — each MESSAGE / SEALED_MESSAGE gets its own ACK.
        // Cap protects against unbounded growth if ACKs never arrive (broken transport).
        lock (_pendingAckLock)
        {
            if (_pendingAck.Count < MaxPendingAck)
                _pendingAck.Enqueue((targetId, persistedMsg, wasSealed));
        }
        return true;
    }

    private bool TrySealedSend(string targetId, string? devId, JsonObject innerPayload, int ttl, Contact contact)
    {
        if (Profile?.DeliveryToken is null) return false;

        byte[]? recipPubKey = null;
        if (devId is not null && contact.Devices.TryGetValue(devId, out var devKeys))
            recipPubKey = devKeys.PublicKey;
        else
            recipPubKey = contact.PublicKey;

        if (recipPubKey is null || recipPubKey.Length == 0) return false;

        var inner = new JsonObject
        {
            ["from"] = Profile.UserId,
            ["fromDeviceId"] = Profile.DeviceId,
        };
        // Copy all payload fields
        foreach (var (key, value) in innerPayload)
            inner[key] = value?.DeepClone();

        var envelope = SealedSender.Seal(inner.ToJsonString(), recipPubKey);

        return _conn.Send(Msg.SealedMessage, ProtocolSerializer.Payload(
            ("to", JsonValue.Create(targetId)),
            ("toDeviceId", devId is not null ? JsonValue.Create(devId) : null),
            ("sealedPayload", new JsonObject
            {
                ["ephemeralKey"] = envelope.EphemeralKey,
                ["nonce"] = envelope.Nonce,
                ["ciphertext"] = envelope.Ciphertext,
            }),
            ("deliveryToken", JsonValue.Create(Profile.DeliveryToken)),
            ("ttl", ttl > 0 ? JsonValue.Create(ttl) : null)
        ));
    }

    // --- Handlers ---

    private void HandleMessage(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var from = ProtocolSerializer.GetString(msg, "from");
        if (from is null) return;

        // H5: Nonce is required — reject messages without it
        var nonce = ProtocolSerializer.GetString(msg, "nonce");
        if (nonce is null) return;
        if (!_nonceTracker.Check(nonce)) return; // Replay

        var plaintext = ReceiveRatcheted(msg, from);
        if (plaintext is null) return;

        // Check for control messages (group key distribution, reactions)
        if (TryHandleControlMessage(plaintext, from)) return;

        var sanitized = EscapeContent(plaintext);
        var ts = DateTimeOffset.FromUnixTimeMilliseconds(ProtocolSerializer.GetLong(msg, "ts")).LocalDateTime;
        var ttl = ProtocolSerializer.GetInt(msg, "ttl");
        var msgId = ProtocolSerializer.GetString(msg, "msgId");

        // Store chat history (debounced)
        _store.AddChatMessage(Profile, from, new ChatMessage
        {
            From = from, Text = sanitized, Ts = ProtocolSerializer.GetLong(msg, "ts"), Ttl = ttl, MsgId = msgId,
        }, Passphrase);

        OnMessageReceived?.Invoke(from, sanitized, from, ts, false, msgId);
    }

    private void HandleSealedMessage(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var sealedNode = msg["sealedPayload"];
        if (sealedNode is null) return;

        // H6: Nonce required for sealed messages — reject without it
        var sealedNonce = sealedNode["nonce"]?.GetValue<string>();
        if (sealedNonce is null) return;
        if (!_nonceTracker.Check(sealedNonce)) return;

        // M13: Fail fast on missing fields instead of passing empty strings
        var ephKey = sealedNode["ephemeralKey"]?.GetValue<string>();
        var sealedCt = sealedNode["ciphertext"]?.GetValue<string>();
        if (string.IsNullOrEmpty(ephKey) || string.IsNullOrEmpty(sealedCt)) return;

        var envelope = new SealedSender.SealedEnvelope(ephKey, sealedNonce, sealedCt);

        var inner = SealedSender.Unseal(envelope, Profile.SecretKey);
        if (inner is null) return;

        var innerObj = JsonObject.Create(inner.Value);
        if (innerObj is null) return;

        // M14: Also check inner message nonce to prevent cross-type replay
        var innerNonce = ProtocolSerializer.GetString(innerObj, "nonce");
        if (innerNonce is not null && !_nonceTracker.Check(innerNonce)) return;

        // Process as normal message
        var from = ProtocolSerializer.GetString(innerObj, "from");
        if (from is null) return;

        var plaintext = ReceiveRatcheted(innerObj, from);
        if (plaintext is null) return;

        // Check for control messages (group key distribution, reactions)
        if (TryHandleControlMessage(plaintext, from)) return;

        var sanitized = EscapeContent(plaintext);
        var ts = DateTime.Now;
        // The server stamps msgId on the outer sealed envelope — the inner
        // (sender-built) payload never carries one. Reading it from innerObj
        // leaves stored messages with MsgId = null, so reactions/edit/delete
        // can never find the target.
        var msgId = ProtocolSerializer.GetString(msg, "msgId");

        _store.AddChatMessage(Profile, from, new ChatMessage
        {
            From = from, Text = sanitized, Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Ttl = 0, MsgId = msgId,
        }, Passphrase);

        OnMessageReceived?.Invoke(from, sanitized, from, ts, true, msgId);
    }

    private string? ReceiveRatcheted(JsonObject msg, string from)
    {
        if (Profile is null || Passphrase is null) return null;

        if (!Profile.Contacts.TryGetValue(from, out var contact))
            return null;

        var fromDeviceId = ProtocolSerializer.GetString(msg, "fromDeviceId");
        var headerNode = msg["header"];

        // X3DH initial message
        var x3dhNode = msg["x3dh"];
        if (x3dhNode is not null)
            return HandleX3dhMessage(msg, from, fromDeviceId, contact);

        // Existing ratchet session
        if (headerNode is not null)
            return HandleRatchetMessage(msg, from, fromDeviceId);

        return null;
    }

    private string? HandleX3dhMessage(JsonObject msg, string from, string? fromDeviceId, Contact contact)
    {
        if (Profile is null || Passphrase is null) return null;

        var x3dh = msg["x3dh"]!;
        var identityKeyB64 = x3dh["identityKey"]?.GetValue<string>();
        var ephemeralKeyB64 = x3dh["ephemeralKey"]?.GetValue<string>();
        if (identityKeyB64 is null || ephemeralKeyB64 is null) return null;

        byte[] identityKey, ephemeralKey;
        try
        {
            identityKey = Convert.FromBase64String(identityKeyB64);
            ephemeralKey = Convert.FromBase64String(ephemeralKeyB64);
            if (identityKey.Length != 32 || ephemeralKey.Length != 32) return null;
        }
        catch { return null; }

        // Verify identity key — constant-time compare against known devices
        bool verified = contact.Devices.Values.Any(d =>
                            d.PublicKey.Length == 32 &&
                            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(d.PublicKey, identityKey))
                        || (contact.PublicKey.Length == 32 &&
                            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(contact.PublicKey, identityKey));
        if (!verified)
        {
            OnSystemMessage?.Invoke($"[SECURITY] X3DH identity key mismatch from {from}! Message rejected.");
            return null;
        }

        var usedOtpkPubB64 = x3dh["usedOTPKPub"]?.GetValue<string>();
        byte[]? usedOtpkPub = null;
        if (usedOtpkPubB64 is not null)
        {
            try { usedOtpkPub = Convert.FromBase64String(usedOtpkPubB64); }
            catch { usedOtpkPub = null; }
        }
        byte[]? otpkSecret = null;
        if (usedOtpkPub is not null)
        {
            var idx = Profile.OneTimePreKeys.FindIndex(k =>
                k.PublicKey.Length == usedOtpkPub.Length &&
                System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(k.PublicKey, usedOtpkPub));
            if (idx >= 0)
            {
                otpkSecret = Profile.OneTimePreKeys[idx].SecretKey;
                Profile.OneTimePreKeys.RemoveAt(idx);
                _store.SaveProfileDebounced(Profile, Passphrase);
            }
        }

        if (Profile.SignedPreKey is null)
        {
            OnSystemMessage?.Invoke("Cannot establish session - no signed pre-key.");
            return null;
        }

        // Try current signed pre-key, then archived
        var spkCandidates = new List<KeyPairData> { Profile.SignedPreKey };
        if (Profile.PreviousSignedPreKeys is not null)
        {
            foreach (var old in Profile.PreviousSignedPreKeys)
                spkCandidates.Add(new KeyPairData { PublicKey = old.PublicKey, SecretKey = old.SecretKey });
        }

        var otpkAttempts = new List<byte[]?>();
        if (usedOtpkPub is not null) otpkAttempts.Add(otpkSecret);
        otpkAttempts.Add(null);

        foreach (var otpk in otpkAttempts)
        {
            foreach (var spk in spkCandidates)
            {
                var x3dhResult = X3dh.Respond(
                    Profile.SecretKey, spk.SecretKey, otpk, identityKey, ephemeralKey);
                // H5: Respond() now returns null on invalid key lengths
                if (x3dhResult is null) continue;

                var ratchetState = DoubleRatchet.InitReceiver(
                    x3dhResult.SharedSecret,
                    new DoubleRatchet.KeyPairBytes(spk.PublicKey, spk.SecretKey));

                var headerNode = msg["header"];
                if (headerNode is null) continue;

                // H9: Validate all header fields are present
                var dhVal = headerNode["dh"]?.GetValue<string>();
                if (string.IsNullOrEmpty(dhVal)) continue;
                var header = new DoubleRatchet.RatchetHeader(
                    dhVal,
                    headerNode["pn"]?.GetValue<int>() ?? 0,
                    headerNode["n"]?.GetValue<int>() ?? 0
                );

                var encrypted = ProtocolSerializer.GetString(msg, "encrypted");
                var nonce = ProtocolSerializer.GetString(msg, "nonce");
                if (encrypted is null || nonce is null) continue;

                var plaintext = DoubleRatchet.Decrypt(ratchetState, header, encrypted, nonce);
                if (plaintext is not null)
                {
                    var stateJson = JsonSerializer.SerializeToElement(ratchetState);
                    _store.SaveRatchetStateAsync(Profile, from, stateJson, Passphrase, fromDeviceId);
                    return plaintext;
                }
            }
        }

        OnSystemMessage?.Invoke("X3DH key agreement failed.");
        return null;
    }

    private string? HandleRatchetMessage(JsonObject msg, string from, string? fromDeviceId)
    {
        if (Profile is null || Passphrase is null) return null;

        var stateJson = _store.LoadRatchetState(Profile, from, fromDeviceId);
        if (stateJson is null)
        {
            OnSystemMessage?.Invoke($"No ratchet session with {from}. Message dropped.");
            return null;
        }

        var state = JsonSerializer.Deserialize<DoubleRatchet.RatchetState>(stateJson.Value);
        if (state is null) return null;

        var backup = state.DeepClone();

        var headerNode = msg["header"];
        if (headerNode is null) return null;

        // H9: Validate all header fields are present
        var dhVal = headerNode["dh"]?.GetValue<string>();
        if (string.IsNullOrEmpty(dhVal)) return null;
        var header = new DoubleRatchet.RatchetHeader(
            dhVal,
            headerNode["pn"]?.GetValue<int>() ?? 0,
            headerNode["n"]?.GetValue<int>() ?? 0
        );

        var encrypted = ProtocolSerializer.GetString(msg, "encrypted");
        var nonce = ProtocolSerializer.GetString(msg, "nonce");
        if (encrypted is null || nonce is null) return null;

        string? plaintext = null;
        try
        {
            plaintext = DoubleRatchet.Decrypt(state, header, encrypted, nonce);
        }
        catch { }

        // K2 fix: rollback on failed decrypt
        var toSave = plaintext is not null ? state : backup;
        var saveJson = JsonSerializer.SerializeToElement(toSave);
        _store.SaveRatchetStateAsync(Profile, from, saveJson, Passphrase, fromDeviceId);

        return plaintext;
    }

    private void HandleMessageAck(JsonObject msg) => AssignAckMsgId(msg, sealed_: false);
    private void HandleSealedMessageAck(JsonObject msg) => AssignAckMsgId(msg, sealed_: true);

    private void AssignAckMsgId(JsonObject msg, bool sealed_)
    {
        if (Profile is null || Passphrase is null) return;
        var msgId = ProtocolSerializer.GetString(msg, "msgId");
        if (msgId is null) return;

        // Dequeue exactly one slot per ACK. Slots are pushed 1:1 with WS sends so ACK
        // order matches. Control-message slots have Msg=null (consume their own ACK,
        // no stamping). Multi-device fan-out pushes N slots for the same ChatMessage —
        // first ACK stamps MsgId, subsequent ACKs find it already set and skip.
        (string ChatKey, ChatMessage? Msg, bool Sealed) entry;
        lock (_pendingAckLock)
        {
            if (_pendingAck.Count == 0) return;
            entry = _pendingAck.Dequeue();
        }

        if (entry.Msg is null) return; // control-message sentinel — nothing to stamp
        if (entry.Msg.MsgId is not null) return; // already stamped by an earlier device ACK

        entry.Msg.MsgId = msgId;
        _store.SaveChatHistoryDebounced(Profile, Passphrase);
        OnOwnMessageIdAssigned?.Invoke(entry.ChatKey, msgId);
    }

    private void HandlePrekeyBundle(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var targetUserId = ProtocolSerializer.GetString(msg, "targetUserId");
        if (targetUserId is null || !_pendingOutgoing.TryGetValue(targetUserId, out var pendingList) || pendingList.Count == 0) return;
        _pendingOutgoing.Remove(targetUserId);
        // H7: Use first message for session establishment, send rest via existing session after
        var pending = pendingList[0];

        if (!Profile.Contacts.TryGetValue(targetUserId, out var contact))
        {
            OnSystemMessage?.Invoke($"Cannot establish session - {targetUserId} is not a contact.");
            return;
        }

        // Parse per-device bundles or single legacy bundle — decode keys to byte[] at wire boundary
        var deviceBundles = new List<(string? DevId, byte[] IdentityKey, byte[]? SigningKey, byte[] SignedPreKey, byte[] SignedPreKeySig, byte[]? OneTimePreKey, string? OneTimePreKeyB64)>();

        static bool TryDecode(string? b64, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (string.IsNullOrEmpty(b64)) return false;
            try { bytes = Convert.FromBase64String(b64); return true; }
            catch { return false; }
        }

        void AddBundle(string? dId, string? ikB64, string? skB64, string? spkB64, string? spkSigB64, string? otpkB64)
        {
            if (!TryDecode(ikB64, out var ik)) return;
            if (!TryDecode(spkB64, out var spk)) return;
            if (!TryDecode(spkSigB64, out var spkSig)) return;
            byte[]? sk = null;
            if (skB64 is not null && TryDecode(skB64, out var skBytes)) sk = skBytes;
            byte[]? otpk = null;
            if (otpkB64 is not null && TryDecode(otpkB64, out var otpkBytes)) otpk = otpkBytes;
            deviceBundles.Add((dId, ik, sk, spk, spkSig, otpk, otpkB64));
        }

        var devicesNode = msg["devices"];
        if (devicesNode is JsonArray devArr)
        {
            foreach (var d in devArr)
            {
                if (d is not JsonObject dObj) continue;
                AddBundle(
                    dObj["deviceId"]?.GetValue<string>(),
                    dObj["identityKey"]?.GetValue<string>(),
                    dObj["signingKey"]?.GetValue<string>(),
                    dObj["signedPreKey"]?.GetValue<string>(),
                    dObj["signedPreKeySig"]?.GetValue<string>(),
                    dObj["oneTimePreKey"]?.GetValue<string>()
                );
            }
        }
        else
        {
            AddBundle(
                null,
                ProtocolSerializer.GetString(msg, "identityKey"),
                ProtocolSerializer.GetString(msg, "signingKey"),
                ProtocolSerializer.GetString(msg, "signedPreKey"),
                ProtocolSerializer.GetString(msg, "signedPreKeySig"),
                ProtocolSerializer.GetString(msg, "oneTimePreKey")
            );
        }

        // Pre-create the persisted message for this initial send (control messages
        // don't get persisted but still need per-send ACK slots in _pendingAck).
        bool initIsControl = pending.Text.Contains("\"__rede_ctrl\"");
        ChatMessage? initPersistedMsg = initIsControl ? null : new ChatMessage
        {
            From = Profile.UserId,
            Text = pending.Text,
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Ttl = pending.Ttl,
        };

        int successCount = 0;
        foreach (var bundle in deviceBundles)
        {
            var devId = bundle.DevId;

            // Skip devices with existing sessions
            if (_store.LoadRatchetState(Profile, targetUserId, devId) is not null)
            {
                successCount++;
                continue;
            }

            // Verify identity key matches known contact (constant-time compare)
            bool keyValid = false;
            if (contact.Devices.TryGetValue(devId ?? "primary", out var dev)
                && dev.PublicKey.Length == bundle.IdentityKey.Length
                && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(dev.PublicKey, bundle.IdentityKey))
                keyValid = true;
            if (!keyValid
                && contact.PublicKey.Length == bundle.IdentityKey.Length
                && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(contact.PublicKey, bundle.IdentityKey))
                keyValid = true;

            // New device — do NOT auto-accept (server could inject phantom devices).
            // Notify user and require explicit confirmation before trusting.
            if (!keyValid && devId is not null && bundle.SigningKey is not null)
            {
                OnNewDeviceDetected?.Invoke(
                    targetUserId, devId,
                    Convert.ToBase64String(bundle.IdentityKey),
                    Convert.ToBase64String(bundle.SigningKey));
                OnSystemMessage?.Invoke($"[SECURITY] Unknown device {devId} for {targetUserId}. Use /confirm {targetUserId} to accept new devices.");
            }

            if (!keyValid)
            {
                OnSystemMessage?.Invoke($"[SECURITY] Pre-key bundle identity key mismatch for {targetUserId} device {devId ?? "primary"}! Skipped.");
                continue;
            }

            var otpk = bundle.OneTimePreKey is not null ? new X3dh.OneTimePreKeyBytes(0, bundle.OneTimePreKey) : null;
            var x3dhResult = X3dh.Initiate(Profile.SecretKey, new X3dh.RecipientBundle(
                bundle.IdentityKey, bundle.SignedPreKey, bundle.SignedPreKeySig, bundle.SigningKey ?? Array.Empty<byte>(), otpk));

            if (x3dhResult is null)
            {
                OnSystemMessage?.Invoke($"X3DH failed for device {devId ?? "primary"} - invalid pre-key signature.");
                continue;
            }

            var ratchetState = DoubleRatchet.InitSender(x3dhResult.SharedSecret, bundle.SignedPreKey);
            var result = DoubleRatchet.Encrypt(ratchetState, pending.Text);

            var stateJson = JsonSerializer.SerializeToElement(ratchetState);
            _store.SaveRatchetStateAsync(Profile, targetUserId, stateJson, Passphrase, devId);

            var payload = ProtocolSerializer.Payload(
                ("to", JsonValue.Create(targetUserId)),
                ("toDeviceId", devId is not null ? JsonValue.Create(devId) : null),
                ("encrypted", JsonValue.Create(result.Ciphertext)),
                ("nonce", JsonValue.Create(result.Nonce)),
                ("header", new JsonObject
                {
                    ["dh"] = result.Header.Dh,
                    ["pn"] = result.Header.Pn,
                    ["n"] = result.Header.N,
                }),
                ("x3dh", new JsonObject
                {
                    ["identityKey"] = Convert.ToBase64String(Profile.PublicKey),
                    ["ephemeralKey"] = Convert.ToBase64String(x3dhResult.EphemeralPublic),
                    ["usedOTPKPub"] = bundle.OneTimePreKeyB64,
                })
            );
            if (pending.Ttl > 0) payload["ttl"] = pending.Ttl;

            if (_conn.Send(Msg.Message, payload))
            {
                // One ACK slot per WS send — first ACK stamps MsgId on initPersistedMsg,
                // subsequent (other devices) are consumed as no-ops.
                lock (_pendingAckLock)
                {
                    if (_pendingAck.Count < MaxPendingAck)
                        _pendingAck.Enqueue((targetUserId, initPersistedMsg, false));
                }
                successCount++;
            }
        }

        if (successCount > 0)
        {
            if (initPersistedMsg is not null)
            {
                _store.AddChatMessage(Profile, targetUserId, initPersistedMsg, Passphrase);
                OnMessageSent?.Invoke(targetUserId, pending.Text, pending.Ttl);
            }
            OnSystemMessage?.Invoke($"Secure session established ({successCount} device(s)).");

            // H7: Send remaining queued messages via the now-established session
            for (int i = 1; i < pendingList.Count; i++)
            {
                var queued = pendingList[i];
                SendMessage(targetUserId, queued.Text, queued.Ttl);
            }
        }
        else
        {
            OnSystemMessage?.Invoke("Failed to establish session with any device.");
        }
    }

    private void HandlePrekeyBundleFail(JsonObject msg)
    {
        var targetId = ProtocolSerializer.GetString(msg, "targetUserId");
        if (targetId is not null) _pendingOutgoing.Remove(targetId);
        var raw = ProtocolSerializer.GetString(msg, "error") ?? "Failed to fetch pre-key bundle";
        // H3: Sanitize server error — strip HTML, URLs, control chars, cap length
        var error = SanitizeServerError(raw);
        OnSystemMessage?.Invoke(error);
    }

    private static string SanitizeServerError(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return "Unknown error.";
        var s = System.Text.RegularExpressions.Regex.Replace(msg, @"<[^>]+>", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"https?://\S+", "[link]");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[\x00-\x1f\x7f]", "");
        return s.Length > 200 ? s[..200] + "..." : s;
    }

    private void HandlePendingMessages(JsonObject msg)
    {
        // Server sends queued messages on connect — process each
        var messagesNode = msg["messages"];
        if (messagesNode is not JsonArray arr) return;

        foreach (var item in arr)
        {
            if (item is not JsonObject innerMsg) continue;
            var type = ProtocolSerializer.GetType(innerMsg);
            if (type == Msg.Message)
                HandleMessage(innerMsg);
            else if (type == Msg.SealedMessage)
                HandleSealedMessage(innerMsg);
        }
    }

    private bool TryHandleControlMessage(string text, string from)
    {
        try
        {
            // M7: Only parse if it starts with { and contains the control marker
            if (!text.StartsWith('{') || !text.Contains("\"__rede_ctrl\"")) return false;
            var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("__rede_ctrl", out var ctrl)) return false;

            if (ctrl.GetString() == "groupkey")
            {
                var groupId = root.GetProperty("groupId").GetString();
                var name = root.GetProperty("name").GetString();
                var key = root.GetProperty("key").GetString();
                var sig = root.GetProperty("sig").GetString();
                if (groupId is not null && name is not null && key is not null && sig is not null)
                {
                    OnGroupKeyReceived?.Invoke(groupId, name, key, sig, from);
                    return true;
                }
            }

            if (ctrl.GetString() == "profile")
            {
                var accentColor = root.TryGetProperty("accentColor", out var ac) ? ac.GetString() : null;
                var avatarData = root.TryGetProperty("avatarData", out var ad) ? ad.GetString() : null;
                var avatarMimeType = root.TryGetProperty("avatarMimeType", out var am) ? am.GetString() : null;
                OnProfileReceived?.Invoke(from, accentColor, avatarData, avatarMimeType);
                return true;
            }

            if (ctrl.GetString() == "placekey")
            {
                var placeId = root.GetProperty("placeId").GetString();
                var metadataKey = root.GetProperty("metadataKey").GetString();
                var encryptedMetadata = root.GetProperty("metadata").GetString();
                if (placeId is not null && metadataKey is not null && encryptedMetadata is not null)
                {
                    OnPlaceKeyReceived?.Invoke(placeId, metadataKey, encryptedMetadata, from);
                    return true;
                }
            }

            if (ctrl.GetString() == "reaction")
            {
                var msgId = root.TryGetProperty("mid", out var mid) ? mid.GetString() : null;
                var emoji = root.TryGetProperty("emoji", out var em) ? em.GetString() : null;
                var action = root.TryGetProperty("action", out var act) ? act.GetString() : null;
                if (msgId is not null && emoji is not null)
                {
                    if (emoji.Length > 32) emoji = emoji[..32];
                    ApplyReaction(from, msgId, emoji, from, action == "add");
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>Strip ANSI escapes and control characters. Mirrors: escapeContent() in index.js</summary>
    internal static string EscapeContent(string text)
    {
        var s = text;
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\x1b\[[0-9;]*[A-Za-z]", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\x1b\][^\x07]*\x07", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\x1b\][^\x1b]*\x1b\\", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\x1b[^\[\]]", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]", "");
        return s;
    }
}
