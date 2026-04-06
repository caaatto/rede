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

    // Places (Discord-like servers with channels)
    public const string PlaceCreate = "place_create";
    public const string PlaceCreateOk = "place_create_ok";
    public const string PlaceInvite = "place_invite";
    public const string PlaceKick = "place_kick";
    public const string PlaceKickOk = "place_kick_ok";
    public const string PlaceLeave = "place_leave";
    public const string PlaceLeaveOk = "place_leave_ok";
    public const string PlaceChannelAdd = "place_channel_add";
    public const string PlaceChannelAddOk = "place_channel_add_ok";
    public const string PlaceChannelRemove = "place_channel_remove";
    public const string PlaceChannelRemoveOk = "place_channel_remove_ok";
    public const string PlaceMessage = "place_message";
    public const string PlaceRoleSet = "place_role_set";
    public const string PlaceRoleSetOk = "place_role_set_ok";
    public const string PlaceBan = "place_ban";
    public const string PlaceBanOk = "place_ban_ok";
    public const string PlaceUnban = "place_unban";
    public const string PlaceUnbanOk = "place_unban_ok";
    public const string PlaceMembers = "place_members";
    public const string PlaceMembersOk = "place_members_ok";

    // Voice Calls
    public const string CallOffer = "call_offer";
    public const string CallAnswer = "call_answer";
    public const string CallIce = "call_ice";
    public const string CallHangup = "call_hangup";
    public const string CallReject = "call_reject";
    public const string CallBusy = "call_busy";
    public const string CallRinging = "call_ringing";
    // SFU Control
    public const string CallJoin = "call_join";
    public const string CallLeave = "call_leave";
    public const string CallMute = "call_mute";
    public const string CallParticipants = "call_participants";

    // Group Calls (LiveKit SFU, E2EE via SFrame)
    public const string GCallRequestToken = "gcall_request_token";
    public const string GCallToken = "gcall_token";
    public const string GCallTokenFail = "gcall_token_fail";
    public const string GCallAnnounce = "gcall_announce";
    public const string GCallEnd = "gcall_end";
    public const string GCallActive = "gcall_active";

    // Blob (Attachments)
    public const string BlobUpload = "blob_upload";
    public const string BlobUploadOk = "blob_upload_ok";
    public const string BlobUploadFail = "blob_upload_fail";
    public const string BlobFetch = "blob_fetch";
    public const string BlobData = "blob_data";
    public const string BlobDataFail = "blob_data_fail";

    // Status / Presence
    public const string StatusUpdate = "status_update";
    public const string StatusSubscribe = "status_subscribe";
    public const string StatusChange = "status_change";

    // Queue
    public const string QueuePosition = "queue_position";
    public const string QueueAdmit = "queue_admit";

    // System
    public const string Error = "error";
    public const string PendingMessages = "pending_messages";
    public const string SessionEnd = "session_end";
}
