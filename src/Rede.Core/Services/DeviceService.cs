using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Rede.Core.Crypto;
using Rede.Core.Networking;
using Rede.Core.Protocol;
using Rede.Core.Storage;

namespace Rede.Core.Services;

/// <summary>
/// Device management: link creation, device added notifications.
/// Mirrors: DEVICE_LINK_CREATE, DEVICE_ADDED handlers in index.js
/// </summary>
public class DeviceService
{
    private readonly RedeConnection _conn;
    private readonly ProfileStore _store;

    public Profile? Profile { get; set; }
    public string? Passphrase { get; set; }

    public event Action<string>? OnSystemMessage;
    public event Action<string, string>? OnDeviceLinkCode; // linkCode, userId

    public DeviceService(RedeConnection conn, ProfileStore store)
    {
        _conn = conn;
        _store = store;
        RegisterHandlers();
    }

    private void RegisterHandlers()
    {
        _conn.On(Msg.DeviceLinkCreateOk, HandleDeviceLinkCreateOk);
        _conn.On(Msg.DeviceAdded, HandleDeviceAdded);
    }

    /// <summary>Generate a device link code for adding a new device.</summary>
    public string CreateDeviceLink()
    {
        var linkCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var codeHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(linkCode))).ToLowerInvariant();

        _conn.Send(Msg.DeviceLinkCreate, ProtocolSerializer.Payload(
            ("codeHash", JsonValue.Create(codeHash))
        ));

        return linkCode;
    }

    /// <summary>Get own device info.</summary>
    public string? GetDeviceId() => Profile?.DeviceId;

    // --- Handlers ---

    private void HandleDeviceLinkCreateOk(JsonObject msg)
    {
        // Server confirmed link code registered — link code already shown to user
    }

    private async void HandleDeviceAdded(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var deviceId = ProtocolSerializer.GetString(msg, "deviceId");
        var publicKey = ProtocolSerializer.GetString(msg, "publicKey");
        var signingKey = ProtocolSerializer.GetString(msg, "signingKey");

        if (deviceId is null || publicKey is null || signingKey is null) return;

        // Validate key format (32-byte base64)
        try
        {
            var pk = Convert.FromBase64String(publicKey);
            var sk = Convert.FromBase64String(signingKey);
            if (pk.Length != 32 || sk.Length != 32)
            {
                OnSystemMessage?.Invoke("[SECURITY] Invalid device keys in DEVICE_ADDED — ignored.");
                return;
            }
        }
        catch
        {
            OnSystemMessage?.Invoke("[SECURITY] Invalid device keys in DEVICE_ADDED — ignored.");
            return;
        }

        Profile.OwnDevices ??= new Dictionary<string, DeviceKeys>();
        Profile.OwnDevices[deviceId] = new DeviceKeys { PublicKey = publicKey, SigningKey = signingKey };
        await _store.SaveProfileAsync(Profile, Passphrase);

        // H10: Show device fingerprint for out-of-band verification
        var fingerprintBytes = SHA256.HashData(Convert.FromBase64String(signingKey));
        var fingerprint = Convert.ToHexString(fingerprintBytes[..8]).ToLowerInvariant();
        var formatted = string.Join(":", Enumerable.Range(0, fingerprint.Length / 2)
            .Select(i => fingerprint.Substring(i * 2, 2)));
        OnSystemMessage?.Invoke($"New device linked: {deviceId}");
        OnSystemMessage?.Invoke($"[SECURITY] Device fingerprint: {formatted}");
        OnSystemMessage?.Invoke("[SECURITY] Verify this fingerprint matches the new device. Use /devices to review all linked devices.");
    }
}
