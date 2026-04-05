using System.Text.Json.Nodes;
using Rede.Core.Crypto;
using Rede.Core.Networking;
using Rede.Core.Protocol;
using Rede.Core.Storage;

namespace Rede.Core.Services;

/// <summary>
/// Contact management: add, lookup, confirm key change, fingerprint.
/// Mirrors: USER_LOOKUP handler, addContact, confirmContactKeyChange in index.js
/// </summary>
public class ContactService : IDisposable
{
    private readonly RedeConnection _conn;
    private readonly ProfileStore _store;

    public void Dispose() { GC.SuppressFinalize(this); }

    public Profile? Profile { get; set; }
    public string? Passphrase { get; set; }

    public event Action<string>? OnSystemMessage;
    public event Action<string, string, string>? OnContactAdded; // userId, displayName, fingerprint
    public event Action<string, string, string>? OnKeyChangeWarning; // userId, oldFP, newFP
    public event Action? OnContactsChanged;

    public ContactService(RedeConnection conn, ProfileStore store)
    {
        _conn = conn;
        _store = store;
        RegisterHandlers();
    }

    private void RegisterHandlers()
    {
        _conn.On(Msg.UserLookupOk, HandleUserLookupOk);
        _conn.On(Msg.UserLookupFail, HandleUserLookupFail);
    }

    /// <summary>Add a contact by looking up their ID on the server.</summary>
    public void AddContact(string lookupId)
    {
        _conn.Send(Msg.UserLookup, ProtocolSerializer.Payload(
            ("lookupId", JsonValue.Create(lookupId))
        ));
    }

    // H3: Store pending key changes so ConfirmKeyChange can apply them
    private readonly Dictionary<string, (byte[] PublicKey, byte[]? SigningKey, string? DisplayName, Dictionary<string, DeviceKeys>? Devices)> _pendingKeyChanges = new();

    /// <summary>Confirm key change for a contact (after security warning).</summary>
    public async Task ConfirmKeyChange(string userId)
    {
        if (Profile is null || Passphrase is null) return;

        if (_pendingKeyChanges.TryGetValue(userId, out var pending))
        {
            _pendingKeyChanges.Remove(userId);
            await _store.ConfirmContactKeyChangeAsync(
                Profile, userId, pending.PublicKey, pending.SigningKey,
                pending.DisplayName, Passphrase, pending.Devices);
            OnSystemMessage?.Invoke($"Key change accepted for {userId}.");
            OnContactsChanged?.Invoke();
        }
        else if (Profile.Contacts.ContainsKey(userId))
        {
            OnSystemMessage?.Invoke($"No pending key change for {userId}.");
        }
        else
        {
            OnSystemMessage?.Invoke($"Contact {userId} not found.");
        }
    }

    /// <summary>Get fingerprint for a user.</summary>
    public string? GetFingerprint(string? userId = null)
    {
        if (Profile is null) return null;

        if (userId is null)
            return CryptoService.Fingerprint(Profile.PublicKey);

        if (Profile.Contacts.TryGetValue(userId, out var contact))
            return CryptoService.Fingerprint(contact.PublicKey);

        return null;
    }

    /// <summary>Get all contacts.</summary>
    public IReadOnlyDictionary<string, Contact>? GetContacts() => Profile?.Contacts;

    // --- Handlers ---

    private async void HandleUserLookupOk(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var userId = ProtocolSerializer.GetString(msg, "userId");
        var publicKey = ProtocolSerializer.GetString(msg, "publicKey");
        var signingKey = ProtocolSerializer.GetString(msg, "signingKey");
        var displayName = ProtocolSerializer.GetString(msg, "displayName");

        if (userId is null || publicKey is null) return;

        // Validate key format (32-byte base64)
        try
        {
            var keyBytes = Convert.FromBase64String(publicKey);
            if (keyBytes.Length != 32)
            {
                OnSystemMessage?.Invoke($"Invalid key format from server for {userId}.");
                return;
            }
        }
        catch
        {
            OnSystemMessage?.Invoke($"Invalid key encoding from server for {userId}.");
            return;
        }

        // Decode keys at the wire boundary
        byte[] publicKeyBytes;
        byte[]? signingKeyBytes = null;
        try
        {
            publicKeyBytes = Convert.FromBase64String(publicKey);
            if (signingKey is not null) signingKeyBytes = Convert.FromBase64String(signingKey);
        }
        catch
        {
            OnSystemMessage?.Invoke($"Invalid key encoding from server for {userId}.");
            return;
        }

        // Parse devices if present — M1: validate each device key
        Dictionary<string, DeviceKeys>? devices = null;
        var devicesNode = msg["devices"];
        if (devicesNode is JsonObject devObj)
        {
            devices = new Dictionary<string, DeviceKeys>();
            foreach (var (devId, devData) in devObj)
            {
                if (devData is JsonObject dd)
                {
                    var devPk = dd["publicKey"]?.GetValue<string>() ?? "";
                    var devSk = dd["signingKey"]?.GetValue<string>();
                    byte[] devPkBytes;
                    byte[]? devSkBytes = null;
                    try
                    {
                        devPkBytes = Convert.FromBase64String(devPk);
                        if (devPkBytes.Length != 32) continue;
                        if (devSk is not null)
                        {
                            devSkBytes = Convert.FromBase64String(devSk);
                            if (devSkBytes.Length != 32) continue;
                        }
                    }
                    catch { continue; }
                    devices[devId] = new DeviceKeys { PublicKey = devPkBytes, SigningKey = devSkBytes };
                }
            }
        }

        var result = await _store.AddContactAsync(Profile, userId, publicKeyBytes, signingKeyBytes, displayName, Passphrase, devices);

        if (result.Warning)
        {
            // H3: Store pending key change for later confirmation
            _pendingKeyChanges[userId] = (publicKeyBytes, signingKeyBytes, displayName, devices);
            OnKeyChangeWarning?.Invoke(userId, result.OldFingerprint!, result.NewFingerprint!);
        }
        else
        {
            var fp = CryptoService.Fingerprint(publicKeyBytes);
            OnContactAdded?.Invoke(userId, displayName ?? userId, fp);
            OnContactsChanged?.Invoke();
        }
    }

    private void HandleUserLookupFail(JsonObject msg)
    {
        var raw = ProtocolSerializer.GetString(msg, "error") ?? "User not found";
        // H3: Sanitize server error
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
}
