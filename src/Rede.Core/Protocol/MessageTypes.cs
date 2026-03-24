namespace Rede.Core.Protocol;

public static class Msg
{
    public const int ProtocolVersion = 3;

    // Auth & Registration
    public const string Register = "register";
    public const string RegisterOk = "register_ok";
    public const string RegisterFail = "register_fail";
    public const string Auth = "auth";
    public const string AuthChallenge = "auth_challenge";
    public const string AuthResponse = "auth_response";
    public const string AuthOk = "auth_ok";
    public const string AuthFail = "auth_fail";

    // Pre-Key Management (X3DH)
    public const string UploadPrekeys = "upload_prekeys";
    public const string UploadPrekeysOk = "upload_prekeys_ok";
    public const string FetchPrekeyBundle = "fetch_prekey_bundle";
    public const string PrekeyBundle = "prekey_bundle";
    public const string PrekeyBundleFail = "prekey_bundle_fail";

    // Key Exchange (legacy, kept for compat)
    public const string KeyExchange = "key_exchange";
    public const string KeyExchangeOk = "key_exchange_ok";

    // 1:1 Messages (Double Ratchet headers)
    public const string Message = "message";
    public const string MessageAck = "message_ack";

    // Groups (Sender Keys)
    public const string GroupCreate = "group_create";
    public const string GroupCreateOk = "group_create_ok";
    public const string GroupInvite = "group_invite";
    public const string GroupKick = "group_kick";
    public const string GroupKickOk = "group_kick_ok";
    public const string GroupMessage = "group_message";
    public const string GroupMembers = "group_members";

    // User Discovery
    public const string UserLookup = "user_lookup";
    public const string UserLookupOk = "user_lookup_ok";
    public const string UserLookupFail = "user_lookup_fail";

    // Invite Codes
    public const string InviteCreate = "invite_create";
    public const string InviteCreateOk = "invite_create_ok";

    // Multi-Device
    public const string DeviceLinkCreate = "device_link_create";
    public const string DeviceLinkCreateOk = "device_link_create_ok";
    public const string DeviceLinkUse = "device_link_use";
    public const string DeviceLinkOk = "device_link_ok";
    public const string DeviceLinkFail = "device_link_fail";
    public const string DeviceAdded = "device_added";

    // Sealed Sender
    public const string SealedMessage = "sealed_message";
    public const string SealedMessageAck = "sealed_message_ack";

    // System
    public const string Error = "error";
    public const string PendingMessages = "pending_messages";
    public const string SessionEnd = "session_end";
}
