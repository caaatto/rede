'use strict';

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const cryptoMod = require('./crypto');

const DATA_DIR = path.join(require('os').homedir(), '.rede');

function ensureDir() {
  if (!fs.existsSync(DATA_DIR)) fs.mkdirSync(DATA_DIR, { recursive: true, mode: 0o700 });
}

// Use opaque filename (full SHA-256 hash of userId) to avoid leaking user IDs
function profilePath(userId) {
  const hash = crypto.createHash('sha256').update(userId).digest('hex');
  return path.join(DATA_DIR, `${hash}.enc`);
}

// Legacy path for migration (16-char truncated hash)
function legacyProfilePath(userId) {
  const hash = crypto.createHash('sha256').update(userId).digest('hex').slice(0, 16);
  return path.join(DATA_DIR, `${hash}.enc`);
}

// Secure file overwrite: fill with random data before writing new content
function secureOverwrite(filePath) {
  try {
    if (fs.existsSync(filePath)) {
      const size = fs.statSync(filePath).size;
      if (size > 0) {
        fs.writeFileSync(filePath, crypto.randomBytes(size));
      }
    }
  } catch {}
}

function loadProfile(userId, passphrase) {
  ensureDir();
  let p = profilePath(userId);
  // Migration: check legacy path if new path doesn't exist
  if (!fs.existsSync(p)) {
    const legacy = legacyProfilePath(userId);
    if (fs.existsSync(legacy)) {
      // Migrate to new path
      fs.renameSync(legacy, p);
    } else {
      return null;
    }
  }
  const envelope = JSON.parse(fs.readFileSync(p, 'utf8'));
  const profile = cryptoMod.decryptProfile(envelope, passphrase);
  if (profile && migrateProfile(profile)) {
    saveProfile(profile, passphrase); // Re-encrypt with N=2^20 + v3 fields
  }
  return profile;
}

function saveProfile(profile, passphrase) {
  ensureDir();
  const p = profilePath(profile.userId);
  const lockFile = p + '.lock';

  // Atomic file-based lock using O_EXCL (prevents TOCTOU race)
  let lockFd = null;
  const maxRetries = 5;
  for (let attempt = 0; attempt < maxRetries; attempt++) {
    try {
      lockFd = fs.openSync(lockFile, fs.constants.O_WRONLY | fs.constants.O_CREAT | fs.constants.O_EXCL, 0o600);
      fs.writeSync(lockFd, String(process.pid));
      fs.closeSync(lockFd);
      break;
    } catch {
      // Lock exists — check if stale (older than 30s)
      try {
        const stat = fs.statSync(lockFile);
        if (Date.now() - stat.mtimeMs > 30000) {
          fs.unlinkSync(lockFile);
          continue; // Retry
        }
      } catch {
        continue; // Stat failed, retry
      }
      if (attempt === maxRetries - 1) {
        throw new Error('Profile is locked by another process');
      }
      // Brief wait before retry
      const waitMs = 50 + Math.random() * 100;
      const start = Date.now();
      while (Date.now() - start < waitMs) { /* spin */ }
    }
  }

  try {
    const envelope = cryptoMod.encryptProfile(profile, passphrase);
    // Atomic write: write to temp file then rename (prevents corruption)
    const tmpFile = p + '.tmp';
    fs.writeFileSync(tmpFile, JSON.stringify(envelope), { mode: 0o600 });
    // Overwrite old file with random data before replacing
    secureOverwrite(p);
    fs.renameSync(tmpFile, p);
  } finally {
    try { fs.unlinkSync(lockFile); } catch {}
  }
}

function createProfile(internalId, displayName, passphrase) {
  const kp = cryptoMod.generateKeyPair();
  const skp = cryptoMod.generateSigningKeyPair();
  const deviceId = crypto.randomBytes(4).toString('hex');
  const profile = {
    userId: internalId,       // internal ID (e.g. "kay#a3f2")
    displayName,              // display name (e.g. "kay")
    deviceId,                 // unique device identifier
    publicKey: kp.publicKey,
    secretKey: kp.secretKey,
    signingKey: skp.signingKey,
    signingSecretKey: skp.signingSecretKey,
    contacts: {},    // internalId -> { devices: { deviceId: { publicKey, signingKey } }, alias, displayName, addedAt }
    groups: {},      // groupId -> { name, key, members }
    chatHistory: {}, // internalId|groupId -> [{ from, text, ts, ttl }]
    // Signal Protocol state (v3)
    signedPreKey: null,         // { publicKey, secretKey }
    signedPreKeySig: null,      // base64
    oneTimePreKeys: [],         // [{ id, publicKey, secretKey }]
    nextPreKeyId: 0,
    ratchetStates: {},          // "contactId:deviceId" → serialized RatchetState
    senderKeys: {},             // groupId → { own: SenderKeyState, members: { userId: SenderKeyState } }
    serverSigningKey: null,     // base64 (TOFU pinned)
    protocolVersion: 3,
  };
  saveProfile(profile, passphrase);
  return profile;
}

// --- Profile migration ---
function migrateProfile(profile) {
  let changed = false;
  if (profile.protocolVersion < 3) {
    if (!profile.ratchetStates) profile.ratchetStates = {};
    if (!profile.senderKeys) profile.senderKeys = {};
    if (!profile.oneTimePreKeys) profile.oneTimePreKeys = [];
    if (profile.nextPreKeyId === undefined) profile.nextPreKeyId = 0;
    if (profile.signedPreKey === undefined) profile.signedPreKey = null;
    if (profile.signedPreKeySig === undefined) profile.signedPreKeySig = null;
    if (profile.serverSigningKey === undefined) profile.serverSigningKey = null;
    if (!profile.previousSignedPreKeys) profile.previousSignedPreKeys = [];
    profile.protocolVersion = 3;
    changed = true;
  }
  // Multi-device migration: add deviceId if missing
  if (!profile.deviceId) {
    profile.deviceId = crypto.randomBytes(4).toString('hex');
    changed = true;
  }
  // Migrate contacts: add devices map if contacts have flat publicKey/signingKey
  for (const [id, contact] of Object.entries(profile.contacts || {})) {
    if (!contact.devices && contact.publicKey) {
      contact.devices = {
        primary: { publicKey: contact.publicKey, signingKey: contact.signingKey },
      };
      changed = true;
    }
  }
  return changed;
}

// Validate a server-provided devices map: keep only entries whose
// publicKey and signingKey are base64 decoding to 32 bytes
function validateDeviceMap(devices) {
  if (!devices || typeof devices !== 'object') return null;
  const validated = {};
  for (const [dId, dInfo] of Object.entries(devices)) {
    if (typeof dInfo !== 'object' || !dInfo || !dInfo.publicKey || !dInfo.signingKey) continue;
    try {
      const pk = cryptoMod.decodeBase64(dInfo.publicKey);
      const sk = cryptoMod.decodeBase64(dInfo.signingKey);
      if (pk.length !== 32 || sk.length !== 32) continue;
    } catch { continue; }
    validated[dId] = dInfo;
  }
  return validated;
}

function addContact(profile, internalId, publicKey, signingKey, displayName, passphrase, devices) {
  const existing = profile.contacts[internalId];
  // Build devices map from server response (drop entries with invalid keys)
  const deviceMap = validateDeviceMap(devices) || { primary: { publicKey, signingKey: signingKey || null } };

  if (existing && existing.publicKey !== publicKey) {
    return {
      warning: true,
      oldFingerprint: cryptoMod.fingerprint(existing.publicKey),
      newFingerprint: cryptoMod.fingerprint(publicKey),
    };
  }
  profile.contacts[internalId] = {
    publicKey,
    signingKey: signingKey || null,
    devices: deviceMap,
    alias: displayName || internalId,
    displayName: displayName || internalId,
    addedAt: Date.now(),
  };
  saveProfile(profile, passphrase);
  return { warning: false };
}

function confirmContactKeyChange(profile, internalId, publicKey, signingKey, displayName, passphrase, devices) {
  const deviceMap = devices || { primary: { publicKey, signingKey: signingKey || null } };
  profile.contacts[internalId] = {
    publicKey,
    signingKey: signingKey || null,
    devices: deviceMap,
    alias: displayName || internalId,
    displayName: displayName || internalId,
    addedAt: Date.now(),
  };
  saveProfile(profile, passphrase);
}

function updateContactDevices(profile, internalId, devices, passphrase) {
  const contact = profile.contacts[internalId];
  if (!contact) return;
  contact.devices = devices;
  saveProfile(profile, passphrase);
}

function addGroup(profile, groupId, name, groupKey, members, passphrase) {
  profile.groups[groupId] = { name, key: groupKey, members: members || [] };
  saveProfile(profile, passphrase);
}

function addChatMessage(profile, chatId, message, passphrase) {
  if (!profile.chatHistory[chatId]) profile.chatHistory[chatId] = [];
  profile.chatHistory[chatId].push(message);

  if (profile.chatHistory[chatId].length > 1000) {
    profile.chatHistory[chatId] = profile.chatHistory[chatId].slice(-1000);
  }
  saveProfile(profile, passphrase);
}

function cleanupExpiredMessages(profile, passphrase) {
  const now = Date.now();
  let changed = false;
  for (const chatId of Object.keys(profile.chatHistory)) {
    const before = profile.chatHistory[chatId].length;
    profile.chatHistory[chatId] = profile.chatHistory[chatId].filter((m) => {
      if (!m.ttl || m.ttl === 0) return true;
      return now - m.ts < m.ttl * 1000;
    });
    if (profile.chatHistory[chatId].length !== before) changed = true;
  }
  if (changed) saveProfile(profile, passphrase);
}

function profileExists(userId) {
  ensureDir();
  return fs.existsSync(profilePath(userId)) || fs.existsSync(legacyProfilePath(userId));
}

// --- Ratchet State Persistence (per device) ---
// Key format: "contactId:deviceId" for multi-device, "contactId" for legacy
function _ratchetKey(contactId, deviceId) {
  return deviceId ? `${contactId}:${deviceId}` : contactId;
}

function saveRatchetState(profile, contactId, state, passphrase, deviceId) {
  const key = _ratchetKey(contactId, deviceId);
  profile.ratchetStates[key] = state;
  saveProfile(profile, passphrase);
}

function loadRatchetState(profile, contactId, deviceId) {
  const key = _ratchetKey(contactId, deviceId);
  // Try device-specific key first, then legacy key
  return profile.ratchetStates[key] || (deviceId ? profile.ratchetStates[contactId] : null) || null;
}

function deleteRatchetState(profile, contactId, passphrase, deviceId) {
  if (deviceId) {
    const key = _ratchetKey(contactId, deviceId);
    if (profile.ratchetStates[key]) {
      delete profile.ratchetStates[key];
      saveProfile(profile, passphrase);
      return true;
    }
    return false;
  }
  // Delete all ratchet states for this contact (all devices)
  let found = false;
  for (const k of Object.keys(profile.ratchetStates)) {
    if (k === contactId || k.startsWith(contactId + ':')) {
      delete profile.ratchetStates[k];
      found = true;
    }
  }
  if (found) saveProfile(profile, passphrase);
  return found;
}

// Get all device IDs for which we have ratchet sessions with a contact
function getRatchetDeviceIds(profile, contactId) {
  const ids = [];
  for (const k of Object.keys(profile.ratchetStates)) {
    if (k === contactId) {
      ids.push(null); // legacy (no deviceId)
    } else if (k.startsWith(contactId + ':')) {
      ids.push(k.slice(contactId.length + 1));
    }
  }
  return ids;
}

// --- Sender Key State Persistence ---
function saveSenderKeyState(profile, groupId, data, passphrase) {
  profile.senderKeys[groupId] = data;
  saveProfile(profile, passphrase);
}

function loadSenderKeyState(profile, groupId) {
  return profile.senderKeys[groupId] || null;
}

// --- Pre-Key Management ---
function generateAndStorePreKeys(profile, count, passphrase) {
  const bundle = cryptoMod.generatePreKeyBundle(profile.signingSecretKey);
  // Archive previous signed pre-key so in-flight X3DH messages can still be decrypted
  if (profile.signedPreKey) {
    if (!profile.previousSignedPreKeys) profile.previousSignedPreKeys = [];
    profile.previousSignedPreKeys.push({
      ...profile.signedPreKey,
      archivedAt: Date.now(),
    });
    // Keep old signed pre-keys for 7 days max, limit to 5
    const maxAge = 7 * 24 * 3600 * 1000;
    const now = Date.now();
    profile.previousSignedPreKeys = profile.previousSignedPreKeys
      .filter(k => now - k.archivedAt < maxAge)
      .slice(-5);
  }
  // Store new private keys in profile
  profile.signedPreKey = bundle.privateKeys.signedPreKey;
  profile.signedPreKeySig = bundle.publicBundle.signedPreKeySig;
  // Append one-time pre-keys with global IDs
  const startId = profile.nextPreKeyId;
  for (let i = 0; i < bundle.privateKeys.oneTimePreKeys.length; i++) {
    const otpk = bundle.privateKeys.oneTimePreKeys[i];
    otpk.id = startId + i;
    profile.oneTimePreKeys.push(otpk);
  }
  profile.nextPreKeyId = startId + bundle.privateKeys.oneTimePreKeys.length;
  saveProfile(profile, passphrase);

  // Return public bundle for server upload (with updated IDs)
  const publicOTPKs = bundle.privateKeys.oneTimePreKeys.map(k => k.publicKey);
  return {
    signedPreKey: bundle.publicBundle.signedPreKey,
    signedPreKeySig: bundle.publicBundle.signedPreKeySig,
    oneTimePreKeys: publicOTPKs,
  };
}

// Look up a one-time pre-key WITHOUT removing it — callers must call
// consumeOneTimePreKey only after the key was successfully used to decrypt,
// otherwise undecryptable messages can drain the pre-key pool
function peekOneTimePreKey(profile, preKeyPub) {
  return profile.oneTimePreKeys.find(k => k.publicKey === preKeyPub) || null;
}

function consumeOneTimePreKey(profile, preKeyPub, passphrase) {
  const idx = profile.oneTimePreKeys.findIndex(k => k.publicKey === preKeyPub);
  if (idx === -1) return null;
  const key = profile.oneTimePreKeys.splice(idx, 1)[0];
  saveProfile(profile, passphrase);
  return key;
}

module.exports = {
  loadProfile,
  saveProfile,
  createProfile,
  migrateProfile,
  addContact,
  confirmContactKeyChange,
  updateContactDevices,
  addGroup,
  addChatMessage,
  cleanupExpiredMessages,
  profileExists,
  saveRatchetState,
  loadRatchetState,
  deleteRatchetState,
  getRatchetDeviceIds,
  saveSenderKeyState,
  loadSenderKeyState,
  generateAndStorePreKeys,
  peekOneTimePreKey,
  consumeOneTimePreKey,
};
