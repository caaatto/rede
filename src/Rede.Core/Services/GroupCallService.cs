using System.Text;
using System.Text.Json.Nodes;
using Rede.Core.Crypto;
using Rede.Core.Networking;
using Rede.Core.Protocol;
using Rede.Core.Storage;

namespace Rede.Core.Services;

/// <summary>
/// Group call scope — identifies which Place channel or Group a call belongs to.
/// The server derives a deterministic LiveKit room name from this.
/// </summary>
public sealed class GCallScope
{
    public required string Kind { get; init; } // "place" | "group"
    public required string Id { get; init; }
    public string? ChannelId { get; init; }

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["kind"] = Kind,
            ["id"] = Id,
        };
        if (ChannelId is not null) o["channelId"] = ChannelId;
        return o;
    }

    public static GCallScope? FromJson(JsonObject? o)
    {
        if (o is null) return null;
        var kind = o["kind"]?.GetValue<string>();
        var id = o["id"]?.GetValue<string>();
        if (kind is null || id is null) return null;
        return new GCallScope
        {
            Kind = kind,
            Id = id,
            ChannelId = o["channelId"]?.GetValue<string>(),
        };
    }

    public string Key => ChannelId is null ? $"{Kind}:{Id}" : $"{Kind}:{Id}:{ChannelId}";
}

/// <summary>
/// Token + LiveKit room coordinates returned by the server.
/// Never persisted — short-lived (6h TTL).
/// </summary>
public sealed class GCallTokenInfo
{
    public required GCallScope Scope { get; init; }
    public required string Url { get; init; }      // LiveKit WebSocket URL
    public required string Token { get; init; }    // JWT
    public required string Room { get; init; }     // Opaque room name (HMAC of scope)
    public long ExpiresAt { get; init; }           // Unix seconds
}

public enum GCallState
{
    Idle,
    RequestingToken,
    Connecting,
    InCall,
}

/// <summary>
/// Group call coordination. Requests a LiveKit JWT from the Rede server,
/// then hands it to the WebView which runs LiveKit JS for media.
///
/// This service does NOT touch media itself — it only orchestrates:
///   1. Token request/response
///   2. Announce/End broadcasts to other members
///   3. SFrame key material (derived from Sender Keys, never sent to server)
///
/// The WebView is responsible for actual WebRTC + SFrame encryption.
/// </summary>
public class GroupCallService
{
    private readonly RedeConnection _connection;

    private GCallState _state = GCallState.Idle;
    private GCallScope? _activeScope;
    private GCallTokenInfo? _activeToken;

    public GCallState State => _state;
    public GCallScope? ActiveScope => _activeScope;
    public GCallTokenInfo? ActiveToken => _activeToken;

    /// <summary>Raised when the server returns a valid token — UI should open the WebView now.</summary>
    public event Action<GCallTokenInfo>? OnTokenReceived;

    /// <summary>Raised when the server rejects a token request.</summary>
    public event Action<GCallScope, string>? OnTokenFailed;

    /// <summary>Raised when another member starts a call in one of our scopes.</summary>
    public event Action<GCallScope, string, long>? OnIncomingAnnounce;

    /// <summary>Raised when a call ends.</summary>
    public event Action<GCallScope, string>? OnCallEnded;

    public GroupCallService(RedeConnection connection)
    {
        _connection = connection;
        _connection.On(Msg.GCallToken, OnServerToken);
        _connection.On(Msg.GCallTokenFail, OnServerTokenFail);
        _connection.On(Msg.GCallAnnounce, OnServerAnnounce);
        _connection.On(Msg.GCallEnd, OnServerEnd);
    }

    /// <summary>
    /// Derive the SFrame E2EE key for a group call scope.
    ///
    /// For Places, we use the Place's metadataKey — a 32-byte symmetric key
    /// shared among all members via the existing E2EE Place key distribution.
    /// For Groups, we use the shared group key.
    ///
    /// Different channels in the same Place get different SFrame keys by
    /// mixing the channel id into the HKDF info. When the Place rekeys
    /// (e.g. after /prekey), the SFrame key rotates too.
    ///
    /// The Rede server and LiveKit SFU never see this key — it's derived
    /// client-side from secrets the server doesn't have.
    /// </summary>
    public static byte[]? DeriveSFrameKey(Profile profile, GCallScope scope)
    {
        string? sharedKeyB64 = null;
        if (scope.Kind == "place")
        {
            if (profile.Places.TryGetValue(scope.Id, out var place) && !string.IsNullOrEmpty(place.MetadataKey))
                sharedKeyB64 = place.MetadataKey;
        }
        else if (scope.Kind == "group")
        {
            if (profile.Groups.TryGetValue(scope.Id, out var group) && !string.IsNullOrEmpty(group.Key))
                sharedKeyB64 = group.Key;
        }
        if (string.IsNullOrEmpty(sharedKeyB64)) return null;

        byte[] ikm;
        try { ikm = Convert.FromBase64String(sharedKeyB64); }
        catch { return null; }
        if (ikm.Length < 32) return null;

        // Domain-separated info so SFrame keys can never collide with any
        // other key derived from the same metadataKey (e.g. metadata encryption).
        var info = Encoding.UTF8.GetBytes("REDE_GCALL_SFRAME_V1:" + scope.Key);
        return Hkdf.DeriveKey(ikm, Array.Empty<byte>(), info, 32);
    }

    /// <summary>
    /// Request a LiveKit token for the given scope. Server verifies membership,
    /// mints a JWT (HS256), and replies with GCALL_TOKEN.
    /// </summary>
    public bool RequestToken(GCallScope scope)
    {
        if (_state != GCallState.Idle) return false;
        _state = GCallState.RequestingToken;
        _activeScope = scope;
        return _connection.Send(Msg.GCallRequestToken, new JsonObject
        {
            ["scope"] = scope.ToJson(),
        });
    }

    /// <summary>
    /// Announce to other members of the scope that a call has started.
    /// Called after the WebView successfully joins the LiveKit room.
    /// </summary>
    public void Announce(GCallScope scope)
    {
        _connection.Send(Msg.GCallAnnounce, new JsonObject
        {
            ["scope"] = scope.ToJson(),
        });
    }

    /// <summary>
    /// Signal that the local user left the call. Server removes them from
    /// the participant set and broadcasts GCALL_END when the last one leaves.
    /// </summary>
    public void EndCall()
    {
        if (_activeScope is null) return;
        _connection.Send(Msg.GCallEnd, new JsonObject
        {
            ["scope"] = _activeScope.ToJson(),
        });
        _state = GCallState.Idle;
        _activeScope = null;
        _activeToken = null;
    }

    /// <summary>
    /// Called by the UI/WebView once the LiveKit connection is live.
    /// </summary>
    public void MarkInCall()
    {
        _state = GCallState.InCall;
    }

    // --- Server message handlers ---

    private void OnServerToken(JsonObject msg)
    {
        var scope = GCallScope.FromJson(msg["scope"] as JsonObject);
        var url = msg["url"]?.GetValue<string>();
        var token = msg["token"]?.GetValue<string>();
        var room = msg["room"]?.GetValue<string>();
        var expiresAt = msg["expiresAt"]?.GetValue<long>() ?? 0;

        if (scope is null || url is null || token is null || room is null)
        {
            _state = GCallState.Idle;
            return;
        }

        var info = new GCallTokenInfo
        {
            Scope = scope,
            Url = url,
            Token = token,
            Room = room,
            ExpiresAt = expiresAt,
        };
        _activeToken = info;
        _state = GCallState.Connecting;
        OnTokenReceived?.Invoke(info);
    }

    private void OnServerTokenFail(JsonObject msg)
    {
        var scope = GCallScope.FromJson(msg["scope"] as JsonObject) ?? _activeScope;
        var reason = msg["reason"]?.GetValue<string>() ?? "unknown";
        _state = GCallState.Idle;
        _activeScope = null;
        _activeToken = null;
        if (scope is not null) OnTokenFailed?.Invoke(scope, reason);
    }

    private void OnServerAnnounce(JsonObject msg)
    {
        var scope = GCallScope.FromJson(msg["scope"] as JsonObject);
        var startedBy = msg["startedBy"]?.GetValue<string>();
        var startedAt = msg["startedAt"]?.GetValue<long>() ?? 0;
        if (scope is null || startedBy is null) return;
        OnIncomingAnnounce?.Invoke(scope, startedBy, startedAt);
    }

    private void OnServerEnd(JsonObject msg)
    {
        var scope = GCallScope.FromJson(msg["scope"] as JsonObject);
        var endedBy = msg["endedBy"]?.GetValue<string>() ?? "";
        if (scope is null) return;
        OnCallEnded?.Invoke(scope, endedBy);
    }
}
