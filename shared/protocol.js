'use strict';

const PROTOCOL_VERSION = 3;

const MSG = {
  // Auth & Registration
  REGISTER: 'register',
  REGISTER_OK: 'register_ok',
  REGISTER_FAIL: 'register_fail',
  AUTH: 'auth',
  AUTH_CHALLENGE: 'auth_challenge',
  AUTH_RESPONSE: 'auth_response',
  AUTH_OK: 'auth_ok',
  AUTH_FAIL: 'auth_fail',

  // Pre-Key Management (X3DH)
  UPLOAD_PREKEYS: 'upload_prekeys',
  UPLOAD_PREKEYS_OK: 'upload_prekeys_ok',
  FETCH_PREKEY_BUNDLE: 'fetch_prekey_bundle',
  PREKEY_BUNDLE: 'prekey_bundle',
  PREKEY_BUNDLE_FAIL: 'prekey_bundle_fail',

  // Key Exchange (legacy, kept for compat)
  KEY_EXCHANGE: 'key_exchange',
  KEY_EXCHANGE_OK: 'key_exchange_ok',

  // 1:1 Messages (now with Double Ratchet headers)
  MESSAGE: 'message',
  MESSAGE_ACK: 'message_ack',

  // Group (now with Sender Keys)
  GROUP_CREATE: 'group_create',
  GROUP_CREATE_OK: 'group_create_ok',
  GROUP_INVITE: 'group_invite',
  GROUP_KICK: 'group_kick',
  GROUP_KICK_OK: 'group_kick_ok',
  GROUP_MESSAGE: 'group_message',
  GROUP_MEMBERS: 'group_members',

  // User Discovery
  USER_LOOKUP: 'user_lookup',
  USER_LOOKUP_OK: 'user_lookup_ok',
  USER_LOOKUP_FAIL: 'user_lookup_fail',

  // Invite Codes
  INVITE_CREATE: 'invite_create',
  INVITE_CREATE_OK: 'invite_create_ok',

  // Multi-Device
  DEVICE_LINK_CREATE: 'device_link_create',
  DEVICE_LINK_CREATE_OK: 'device_link_create_ok',
  DEVICE_LINK_USE: 'device_link_use',
  DEVICE_LINK_OK: 'device_link_ok',
  DEVICE_LINK_FAIL: 'device_link_fail',
  DEVICE_ADDED: 'device_added',

  // Sealed Sender
  SEALED_MESSAGE: 'sealed_message',
  SEALED_MESSAGE_ACK: 'sealed_message_ack',

  // Places (Discord-like servers with channels)
  PLACE_CREATE: 'place_create',
  PLACE_CREATE_OK: 'place_create_ok',
  PLACE_INVITE: 'place_invite',
  PLACE_KICK: 'place_kick',
  PLACE_KICK_OK: 'place_kick_ok',
  PLACE_LEAVE: 'place_leave',
  PLACE_LEAVE_OK: 'place_leave_ok',
  PLACE_CHANNEL_ADD: 'place_channel_add',
  PLACE_CHANNEL_ADD_OK: 'place_channel_add_ok',
  PLACE_CHANNEL_REMOVE: 'place_channel_remove',
  PLACE_CHANNEL_REMOVE_OK: 'place_channel_remove_ok',
  PLACE_MESSAGE: 'place_message',

  // System
  ERROR: 'error',
  PENDING_MESSAGES: 'pending_messages',
  SESSION_END: 'session_end',
};

// Server-side: creates signed messages (ts set by server)
function createMessage(type, payload = {}) {
  return JSON.stringify({ ...payload, v: PROTOCOL_VERSION, type, ts: Date.now() });
}

// Client version (no ts — server sets it)
function createClientMessage(type, payload = {}) {
  return JSON.stringify({ ...payload, v: PROTOCOL_VERSION, type });
}

function parseMessage(raw) {
  try {
    return JSON.parse(raw);
  } catch {
    return null;
  }
}

module.exports = { MSG, PROTOCOL_VERSION, createMessage, createClientMessage, parseMessage };
