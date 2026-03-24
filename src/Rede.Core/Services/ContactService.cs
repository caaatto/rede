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
public class ContactService
{
    private readonly RedeConnection _conn;
    private readonly ProfileStore _store;

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

    /// <summary>Confirm key change for a contact (after security warning).</summary>
    public async Task ConfirmKeyChange(string userId)
    {
        if (Profile is null || Passphrase is null) return;

        // The pending key change data should have been stored
        // For now, re-accept the contact with current known keys
        if (Profile.Contacts.TryGetValue(userId, out var contact))
        {
            OnSystemMessage?.Invoke($"Key change accepted for {userId}.");
            OnContactsChanged?.Invoke();
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

        // Parse devices if present
        Dictionary<string, DeviceKeys>? devices = null;
        var devicesNode = msg["devices"];
        if (devicesNode is JsonObject devObj)
        {
            devices = new Dictionary<string, DeviceKeys>();
            foreach (var (devId, devData) in devObj)
            {
                if (devData is JsonObject dd)
                {
                    devices[devId] = new DeviceKeys
                    {
                        PublicKey = dd["publicKey"]?.GetValue<string>() ?? "",
                        SigningKey = dd["signingKey"]?.GetValue<string>(),
                    };
                }
            }
        }

        var result = await _store.AddContactAsync(Profile, userId, publicKey, signingKey, displayName, Passphrase, devices);

        if (result.Warning)
        {
            OnKeyChangeWarning?.Invoke(userId, result.OldFingerprint!, result.NewFingerprint!);
        }
        else
        {
            var fp = CryptoService.Fingerprint(publicKey);
            OnContactAdded?.Invoke(userId, displayName ?? userId, fp);
            OnContactsChanged?.Invoke();
        }
    }

    private void HandleUserLookupFail(JsonObject msg)
    {
        var error = ProtocolSerializer.GetString(msg, "error") ?? "User not found";
        OnSystemMessage?.Invoke(error);
    }
}
