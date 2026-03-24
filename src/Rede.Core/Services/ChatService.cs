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
public class ChatService
{
    private readonly RedeConnection _conn;
    private readonly ProfileStore _store;
    private readonly NonceTracker _nonceTracker = new();

    public Profile? Profile { get; set; }
    public string? Passphrase { get; set; }

    // Queued while awaiting pre-key bundle
    private (string Target, string Text, int Ttl)? _pendingOutgoing;

    public event Action<string, string, string, DateTime, bool>? OnMessageReceived; // from, text, chatId, timestamp, isSealed
    public event Action<string>? OnSystemMessage;
    public event Action<string, string>? OnMessageSent; // chatId, text

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

        // Send to devices with existing sessions
        foreach (var devId in haveSessions)
        {
            SendToDevice(targetId, devId, text, ttl, contact);
        }

        // Fetch pre-key bundles for devices without sessions
        if (needBundle.Count > 0)
        {
            _pendingOutgoing = (targetId, text, ttl);
            _conn.Send(Msg.FetchPrekeyBundle, ProtocolSerializer.Payload(
                ("targetUserId", JsonValue.Create(targetId))
            ));
            if (haveSessions.Count == 0)
                OnSystemMessage?.Invoke("Establishing secure session...");
        }
    }

    private void SendToDevice(string targetId, string? devId, string text, int ttl, Contact contact)
    {
        if (Profile is null || Passphrase is null) return;

        var stateJson = _store.LoadRatchetState(Profile, targetId, devId);
        if (stateJson is null) return;

        var state = JsonSerializer.Deserialize<DoubleRatchet.RatchetState>(stateJson.Value);
        if (state is null) return;

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
            Task.Run(async () => await _store.SaveRatchetStateAsync(Profile, targetId, backupJson, Passphrase, devId));
            OnSystemMessage?.Invoke("Message not sent — connection lost. Ratchet state preserved.");
            return;
        }

        var stateElement = JsonSerializer.SerializeToElement(state);
        Task.Run(async () => await _store.SaveRatchetStateAsync(Profile, targetId, stateElement, Passphrase, devId));
    }

    private bool TrySealedSend(string targetId, string? devId, JsonObject innerPayload, int ttl, Contact contact)
    {
        if (Profile?.DeliveryToken is null) return false;

        string? recipPubKey = null;
        if (devId is not null && contact.Devices.TryGetValue(devId, out var devKeys))
            recipPubKey = devKeys.PublicKey;
        else
            recipPubKey = contact.PublicKey;

        if (recipPubKey is null) return false;

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

        var nonce = ProtocolSerializer.GetString(msg, "nonce");
        if (nonce is not null && !_nonceTracker.Check(nonce)) return; // Replay

        var plaintext = ReceiveRatcheted(msg, from);
        if (plaintext is null) return;

        var sanitized = EscapeContent(plaintext);
        var ts = DateTimeOffset.FromUnixTimeMilliseconds(ProtocolSerializer.GetLong(msg, "ts")).LocalDateTime;
        var ttl = ProtocolSerializer.GetInt(msg, "ttl");

        // Store chat history
        Task.Run(async () => await _store.AddChatMessageAsync(Profile, from, new ChatMessage
        {
            From = from, Text = sanitized, Ts = ProtocolSerializer.GetLong(msg, "ts"), Ttl = ttl,
        }, Passphrase));

        OnMessageReceived?.Invoke(from, sanitized, from, ts, false);
    }

    private void HandleSealedMessage(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var sealedNode = msg["sealedPayload"];
        if (sealedNode is null) return;

        var envelope = new SealedSender.SealedEnvelope(
            sealedNode["ephemeralKey"]?.GetValue<string>() ?? "",
            sealedNode["nonce"]?.GetValue<string>() ?? "",
            sealedNode["ciphertext"]?.GetValue<string>() ?? ""
        );

        var inner = SealedSender.Unseal(envelope, Profile.SecretKey);
        if (inner is null) return;

        var innerObj = JsonObject.Create(inner.Value);
        if (innerObj is null) return;

        // Process as normal message
        var from = ProtocolSerializer.GetString(innerObj, "from");
        if (from is null) return;

        var plaintext = ReceiveRatcheted(innerObj, from);
        if (plaintext is null) return;

        var sanitized = EscapeContent(plaintext);
        var ts = DateTime.Now;

        Task.Run(async () => await _store.AddChatMessageAsync(Profile, from, new ChatMessage
        {
            From = from, Text = sanitized, Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Ttl = 0,
        }, Passphrase));

        OnMessageReceived?.Invoke(from, sanitized, from, ts, true);
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
        {
            return HandleX3dhMessage(msg, from, fromDeviceId, contact);
        }

        // Existing ratchet session
        if (headerNode is not null)
        {
            return HandleRatchetMessage(msg, from, fromDeviceId);
        }

        return null;
    }

    private string? HandleX3dhMessage(JsonObject msg, string from, string? fromDeviceId, Contact contact)
    {
        if (Profile is null || Passphrase is null) return null;

        var x3dh = msg["x3dh"]!;
        var identityKey = x3dh["identityKey"]?.GetValue<string>();
        var ephemeralKey = x3dh["ephemeralKey"]?.GetValue<string>();
        if (identityKey is null || ephemeralKey is null) return null;

        // Verify identity key
        bool verified = contact.Devices.Values.Any(d => d.PublicKey == identityKey)
                        || contact.PublicKey == identityKey;
        if (!verified)
        {
            OnSystemMessage?.Invoke($"[SECURITY] X3DH identity key mismatch from {from}! Message rejected.");
            return null;
        }

        var usedOtpkPub = x3dh["usedOTPKPub"]?.GetValue<string>();
        string? otpkSecret = null;
        if (usedOtpkPub is not null)
        {
            var idx = Profile.OneTimePreKeys.FindIndex(k => k.PublicKey == usedOtpkPub);
            if (idx >= 0)
            {
                otpkSecret = Profile.OneTimePreKeys[idx].SecretKey;
                Profile.OneTimePreKeys.RemoveAt(idx);
                Task.Run(async () => await _store.SaveProfileAsync(Profile, Passphrase));
            }
        }

        if (Profile.SignedPreKey is null)
        {
            OnSystemMessage?.Invoke("Cannot establish session — no signed pre-key.");
            return null;
        }

        // Try current signed pre-key, then archived
        var spkCandidates = new List<KeyPairData> { Profile.SignedPreKey };
        if (Profile.PreviousSignedPreKeys is not null)
        {
            foreach (var old in Profile.PreviousSignedPreKeys)
                spkCandidates.Add(new KeyPairData { PublicKey = old.PublicKey, SecretKey = old.SecretKey });
        }

        var otpkAttempts = usedOtpkPub is not null ? new[] { otpkSecret, null } : new string?[] { null };

        foreach (var otpk in otpkAttempts)
        {
            foreach (var spk in spkCandidates)
            {
                var x3dhResult = X3dh.Respond(
                    Profile.SecretKey, spk.SecretKey, otpk, identityKey, ephemeralKey);

                var ratchetState = DoubleRatchet.InitReceiver(
                    x3dhResult.SharedSecret,
                    new DoubleRatchet.KeyPairB64(spk.PublicKey, spk.SecretKey));

                var headerNode = msg["header"];
                if (headerNode is null) continue;

                var header = new DoubleRatchet.RatchetHeader(
                    headerNode["dh"]?.GetValue<string>() ?? "",
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
                    Task.Run(async () => await _store.SaveRatchetStateAsync(Profile, from, stateJson, Passphrase, fromDeviceId));
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

        var headerNode = msg["header"]!;
        var header = new DoubleRatchet.RatchetHeader(
            headerNode["dh"]?.GetValue<string>() ?? "",
            headerNode["pn"]?.GetValue<int>() ?? 0,
            headerNode["n"]?.GetValue<int>() ?? 0
        );

        var encrypted = ProtocolSerializer.GetString(msg, "encrypted");
        var nonce = ProtocolSerializer.GetString(msg, "nonce");
        if (encrypted is null || nonce is null) return null;

        var plaintext = DoubleRatchet.Decrypt(state, header, encrypted, nonce);

        // K2 fix: rollback on failed decrypt
        var toSave = plaintext is not null ? state : backup;
        var saveJson = JsonSerializer.SerializeToElement(toSave);
        Task.Run(async () => await _store.SaveRatchetStateAsync(Profile, from, saveJson, Passphrase, fromDeviceId));

        return plaintext;
    }

    private void HandleMessageAck(JsonObject msg) { /* delivery confirmation */ }
    private void HandleSealedMessageAck(JsonObject msg) { /* sealed delivery confirmation */ }

    private void HandlePrekeyBundle(JsonObject msg)
    {
        if (Profile is null || Passphrase is null || _pendingOutgoing is null) return;

        var pending = _pendingOutgoing.Value;
        _pendingOutgoing = null;

        var targetUserId = ProtocolSerializer.GetString(msg, "targetUserId");
        if (targetUserId is null || targetUserId != pending.Target) return;

        if (!Profile.Contacts.TryGetValue(targetUserId, out var contact))
        {
            OnSystemMessage?.Invoke($"Cannot establish session — {targetUserId} is not a contact.");
            return;
        }

        // Parse per-device bundles or single legacy bundle
        var deviceBundles = new List<(string? DevId, string IdentityKey, string? SigningKey, string SignedPreKey, string SignedPreKeySig, string? OneTimePreKey)>();
        var devicesNode = msg["devices"];
        if (devicesNode is JsonArray devArr)
        {
            foreach (var d in devArr)
            {
                if (d is not JsonObject dObj) continue;
                deviceBundles.Add((
                    dObj["deviceId"]?.GetValue<string>(),
                    dObj["identityKey"]?.GetValue<string>() ?? "",
                    dObj["signingKey"]?.GetValue<string>(),
                    dObj["signedPreKey"]?.GetValue<string>() ?? "",
                    dObj["signedPreKeySig"]?.GetValue<string>() ?? "",
                    dObj["oneTimePreKey"]?.GetValue<string>()
                ));
            }
        }
        else
        {
            deviceBundles.Add((
                null,
                ProtocolSerializer.GetString(msg, "identityKey") ?? "",
                ProtocolSerializer.GetString(msg, "signingKey"),
                ProtocolSerializer.GetString(msg, "signedPreKey") ?? "",
                ProtocolSerializer.GetString(msg, "signedPreKeySig") ?? "",
                ProtocolSerializer.GetString(msg, "oneTimePreKey")
            ));
        }

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

            // Verify identity key matches known contact
            bool keyValid = false;
            if (contact.Devices.TryGetValue(devId ?? "primary", out var dev) && dev.PublicKey == bundle.IdentityKey)
                keyValid = true;
            if (!keyValid && contact.PublicKey == bundle.IdentityKey)
                keyValid = true;

            // New device — verify signed pre-key signature
            if (!keyValid && devId is not null && bundle.SigningKey is not null)
            {
                if (CryptoService.Verify(bundle.SignedPreKey, bundle.SignedPreKeySig, bundle.SigningKey))
                {
                    contact.Devices[devId] = new DeviceKeys { PublicKey = bundle.IdentityKey, SigningKey = bundle.SigningKey };
                    Task.Run(async () => await _store.SaveProfileAsync(Profile, Passphrase));
                    keyValid = true;
                    OnSystemMessage?.Invoke($"[SECURITY] New device {devId} for {targetUserId} accepted (pre-key verified). Verify fingerprint out-of-band!");
                }
            }

            if (!keyValid)
            {
                OnSystemMessage?.Invoke($"[SECURITY] Pre-key bundle identity key mismatch for {targetUserId} device {devId ?? "primary"}! Skipped.");
                continue;
            }

            var otpk = bundle.OneTimePreKey is not null ? new X3dh.OneTimePreKeyPublic(0, bundle.OneTimePreKey) : null;
            var x3dhResult = X3dh.Initiate(Profile.SecretKey, new X3dh.RecipientBundle(
                bundle.IdentityKey, bundle.SignedPreKey, bundle.SignedPreKeySig, bundle.SigningKey ?? "", otpk));

            if (x3dhResult is null)
            {
                OnSystemMessage?.Invoke($"X3DH failed for device {devId ?? "primary"} — invalid pre-key signature.");
                continue;
            }

            var ratchetState = DoubleRatchet.InitSender(x3dhResult.SharedSecret, bundle.SignedPreKey);
            var result = DoubleRatchet.Encrypt(ratchetState, pending.Text);

            var stateJson = JsonSerializer.SerializeToElement(ratchetState);
            Task.Run(async () => await _store.SaveRatchetStateAsync(Profile, targetUserId, stateJson, Passphrase, devId));

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
                    ["identityKey"] = Profile.PublicKey,
                    ["ephemeralKey"] = x3dhResult.EphemeralPublic,
                    ["usedOTPKPub"] = bundle.OneTimePreKey,
                })
            );
            if (pending.Ttl > 0) payload["ttl"] = pending.Ttl;

            _conn.Send(Msg.Message, payload);
            successCount++;
        }

        if (successCount > 0)
        {
            Task.Run(async () => await _store.AddChatMessageAsync(Profile, targetUserId, new ChatMessage
            {
                From = Profile.UserId, Text = pending.Text, Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Ttl = pending.Ttl,
            }, Passphrase));
            OnSystemMessage?.Invoke($"Secure session established ({successCount} device(s)).");
            OnMessageSent?.Invoke(targetUserId, pending.Text);
        }
        else
        {
            OnSystemMessage?.Invoke("Failed to establish session with any device.");
        }
    }

    private void HandlePrekeyBundleFail(JsonObject msg)
    {
        _pendingOutgoing = null;
        var error = ProtocolSerializer.GetString(msg, "error") ?? "Failed to fetch pre-key bundle";
        OnSystemMessage?.Invoke(error);
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

    /// <summary>Strip ANSI escapes and control characters. Mirrors: escapeContent() in index.js</summary>
    private static string EscapeContent(string text)
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
