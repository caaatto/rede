'use strict';

require('dotenv').config({ path: require('path').join(__dirname, '..', '.env') });

const { MSG } = require('../shared/protocol');
const { RedeConnection } = require('./network');
const { RedeUI } = require('./ui');
const crypto = require('crypto');
const cryptoMod = require('./crypto');
const store = require('./store');
const { bootSequence } = require('./boot');

// --- Parse CLI args ---
const args = process.argv.slice(2);
const envTransport = (process.env.REDE_TRANSPORT || '').toLowerCase();
let serverUrl = process.env.REDE_SERVER || 'wss://localhost:9377';
let useTor = envTransport === 'tor';
let useI2P = envTransport === 'i2p';
let userId = null;
let inviteCode = null;
let linkCode = null;
let torProxy = process.env.REDE_TOR_PROXY || null;
let i2pProxy = process.env.REDE_I2P_PROXY || null;

for (let i = 0; i < args.length; i++) {
  switch (args[i]) {
    case '--server': case '-s': serverUrl = args[++i]; break;
    case '--tor': useTor = true; break;
    case '--i2p': useI2P = true; break;
    case '--tor-proxy': torProxy = args[++i]; break;
    case '--i2p-proxy': i2pProxy = args[++i]; break;
    case '--user': case '-u': userId = args[++i]; break;
    case '--invite': case '-i': inviteCode = args[++i]; break;
    case '--link': linkCode = args[++i]; break;
    case '--help': case '-h':
      console.log('Usage: node client/index.js [options]');
      console.log('  -s, --server <url>     Server URL (default: wss://localhost:9377)');
      console.log('  -u, --user <id>        User ID');
      console.log('  -i, --invite <code>    Invite code (for registration)');
      console.log('  --i2p                  Connect via I2P SOCKS proxy');
      console.log('  --tor                  Connect via Tor SOCKS5 proxy');
      console.log('  --i2p-proxy <url>      I2P proxy (default: socks5h://127.0.0.1:14447)');
      console.log('  --tor-proxy <url>      Tor proxy (default: socks5h://127.0.0.1:9050)');
      process.exit(0);
  }
}

if (!userId) {
  console.error('Usage: node client/index.js -u <id> [-i <inviteCode>] [-s <serverUrl>] [--i2p|--tor]');
  console.error('For login: -u your_id#xxxx');
  console.error('For register: -u display_name -i INVITE_CODE');
  process.exit(1);
}

// --- Passphrase prompt ---
function askPassphrase(prompt) {
  // Auto-fill from environment variable if available (for automation)
  if (process.env.REDE_AUTO_PASSPHRASE) {
    process.stderr.write(prompt + '***\n');
    return Promise.resolve(process.env.REDE_AUTO_PASSPHRASE);
  }

  return new Promise((resolve) => {
    process.stderr.write(prompt);
    if (process.stdin.isTTY) process.stdin.setRawMode(true);
    process.stdin.resume();
    let pass = '';
    function handler(ch) {
      const c = ch.toString();
      if (c === '\n' || c === '\r') {
        process.stdin.removeListener('data', handler);
        if (process.stdin.isTTY) process.stdin.setRawMode(false);
        process.stdin.pause();
        process.stderr.write('\n');
        resolve(pass);
      } else if (c === '\x7f' || c === '\b') {
        pass = pass.slice(0, -1);
      } else if (c === '\x03') {
        process.exit(0);
      } else {
        pass += c;
      }
    }
    process.stdin.on('data', handler);
  });
}

async function main() {
  // Determine if this is a login (id#xxxx), registration (display name + invite), or device link
  const isRegistration = !!inviteCode;
  const isDeviceLink = !!linkCode;
  const isNewUser = isRegistration || isDeviceLink;
  let passphrase;

  if (isNewUser) {
    passphrase = await askPassphrase('Create passphrase (encrypts your keys): ');
    if (passphrase.length < 12) {
      console.error('Passphrase must be at least 12 characters.');
      process.exit(1);
    }
    const strength = cryptoMod.estimatePassphraseStrength(passphrase);
    if (strength < 40) {
      console.error(`Passphrase too weak (score: ${strength}/100). Use a mix of upper/lowercase, numbers, and symbols. Avoid common patterns.`);
      process.exit(1);
    }
    const confirm = await askPassphrase('Confirm passphrase: ');
    if (passphrase !== confirm) {
      console.error('Passphrases do not match.');
      process.exit(1);
    }
  } else {
    if (!store.profileExists(userId)) {
      console.error(`No profile found for "${userId}". Use -i <invite_code> to register.`);
      if (process.platform === 'win32') { console.error('\nPress any key to exit...'); try { require('fs').readSync(0, Buffer.alloc(1), 0, 1); } catch {} }
      process.exit(1);
    }
    passphrase = await askPassphrase('Passphrase: ');
  }

  let profile;
  if (isNewUser) {
    // Create temp profile, internalId will be assigned by server
    profile = store.createProfile('pending', userId, passphrase);
  } else {
    profile = store.loadProfile(userId, passphrase);
    if (!profile) {
      console.error('Wrong passphrase or corrupted profile.');
      console.error('If profile is corrupted, delete ~/.rede/ and re-register with a new invite code.');
      if (process.platform === 'win32') { console.error('\nPress any key to exit...'); try { require('fs').readSync(0, Buffer.alloc(1), 0, 1); } catch {} }
      process.exit(1);
    }
  }

  // --- Boot Sequence ---
  await bootSequence({
    userId,
    isNewUser,
    useI2P,
    useTor,
    serverUrl,
  });

  // --- State ---
  let currentTTL = 0;
  let currentChatTarget = null;
  let currentChatType = null;
  let pendingOutgoing = null; // { target, text, ttl } — queued while awaiting pre-key bundle

  const connOpts = { useTor, useI2P };
  if (torProxy) connOpts.torProxy = torProxy;
  if (i2pProxy) connOpts.i2pProxy = i2pProxy;
  const conn = new RedeConnection(serverUrl, connOpts);
  const ui = new RedeUI();

  ui.setStatus('Connecting...');

  try {
    await conn.connect();
  } catch (err) {
    ui.setStatus(`Connection failed: ${err.message}`);
    ui.showSystemMessage('Could not connect to server.');
    ui.render();
    return;
  }

  // Wait for queue admission if server is full
  conn.onQueuePosition = (pos, total) => {
    ui.setStatus(`Queue: ${pos}/${total} — waiting for slot...`);
    ui.render();
  };
  await new Promise(resolve => {
    // Give server a moment to send QUEUE_POSITION
    setTimeout(() => {
      if (!conn._isQueued) return resolve();
      conn.onQueueAdmit = () => {
        ui.setStatus('Admitted — authenticating...');
        ui.render();
        resolve();
      };
    }, 200);
  });

  // Re-authenticate on reconnect
  conn.onReconnect = () => {
    ui.setStatus('Reconnecting...');
    authenticate();
  };

  // --- Sealed Sender helper ---
  // Wraps a normal message payload in a sealed envelope.
  // Returns true if sealed send succeeded, false to fall back to normal.
  function trySealedSend(targetId, devId, innerPayload, ttl) {
    const contact = profile.contacts[targetId];
    if (!contact || !profile._deliveryToken) return false;
    // Get the recipient's identity public key for the specific device or primary
    let recipPubKey = null;
    if (devId && contact.devices && contact.devices[devId]) {
      recipPubKey = contact.devices[devId].publicKey;
    } else {
      recipPubKey = contact.publicKey;
    }
    if (!recipPubKey) return false;
    // Build inner payload with sender identity
    const inner = {
      from: profile.userId,
      fromDeviceId: profile.deviceId,
      ...innerPayload,
    };
    const sealed = cryptoMod.sealMessage(JSON.stringify(inner), recipPubKey);
    return conn.send(MSG.SEALED_MESSAGE, {
      to: targetId,
      toDeviceId: devId,
      sealedPayload: sealed,
      deliveryToken: profile._deliveryToken,
      ttl,
    });
  }

  // Strip ANSI escape sequences and control characters to prevent terminal injection
  function escapeContent(text) {
    return String(text)
      .replace(/\x1b\[[0-9;]*[A-Za-z]/g, '')   // CSI sequences
      .replace(/\x1b\][^\x07]*\x07/g, '')       // OSC sequences (BEL terminated)
      .replace(/\x1b\][^\x1b]*\x1b\\/g, '')     // OSC sequences (ST terminated)
      .replace(/\x1b[^[\]]/g, '')                // Other escape sequences
      .replace(/[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]/g, ''); // Control chars (keep \n \r \t)
  }

  // --- Pre-key upload helper ---
  function uploadPreKeysIfNeeded(prekeyCount) {
    const PREKEY_THRESHOLD = 5;
    if (prekeyCount !== undefined && prekeyCount <= PREKEY_THRESHOLD) {
      const bundle = store.generateAndStorePreKeys(profile, 20, passphrase);
      conn.send(MSG.UPLOAD_PREKEYS, {
        signedPreKey: bundle.signedPreKey,
        signedPreKeySig: bundle.signedPreKeySig,
        oneTimePreKeys: bundle.oneTimePreKeys,
      });
    }
  }

  // --- Server signature verification ---
  function verifyServerSig(msg) {
    if (!profile.serverSigningKey) return true; // Not yet pinned
    if (!msg.serverSig) return false;
    return cryptoMod.verifyServerSignature(msg, profile.serverSigningKey);
  }

  // --- Ratcheted send helper (multi-device) ---
  function sendRatcheted(targetId, text, ttl) {
    const contact = profile.contacts[targetId];
    if (!contact) { ui.showSystemMessage('Contact not found'); return; }

    const devices = contact.devices || {};
    const deviceIds = Object.keys(devices);

    // Check which devices need X3DH session establishment
    const needBundle = [];
    const haveSessions = [];
    for (const devId of deviceIds) {
      if (store.loadRatchetState(profile, targetId, devId)) {
        haveSessions.push(devId);
      } else {
        needBundle.push(devId);
      }
    }

    // Also check legacy (no device ID) ratchet state
    if (deviceIds.length === 0 && store.loadRatchetState(profile, targetId)) {
      haveSessions.push(null);
    }

    // Send to devices with existing sessions
    for (const devId of haveSessions) {
      const ratchetState = store.loadRatchetState(profile, targetId, devId);
      const stateBackup = JSON.parse(JSON.stringify(ratchetState));
      const result = cryptoMod.ratchetEncrypt(ratchetState, text);

      const msgPayload = {
        encrypted: result.ciphertext,
        nonce: result.nonce,
        header: result.header,
      };

      // Try sealed sender first (hides sender from server)
      let sent = trySealedSend(targetId, devId, msgPayload, ttl);
      if (!sent) {
        // Fallback to normal (e.g., no delivery token yet or no recipient key)
        sent = conn.send(MSG.MESSAGE, { to: targetId, toDeviceId: devId, ...msgPayload, ttl });
      }

      if (sent === false) {
        store.saveRatchetState(profile, targetId, stateBackup, passphrase, devId);
        ui.showSystemMessage('Message not sent — connection lost. Ratchet state preserved.');
        return;
      }
      store.saveRatchetState(profile, targetId, ratchetState, passphrase, devId);
    }

    // Fetch pre-key bundles for devices without sessions
    if (needBundle.length > 0) {
      pendingOutgoing = { target: targetId, text, ttl };
      conn.send(MSG.FETCH_PREKEY_BUNDLE, { targetUserId: targetId });
      if (haveSessions.length === 0) {
        ui.showSystemMessage('Establishing secure session...');
      }
    }
  }

  // --- Queued messages for device discovery ---
  const pendingDeviceDiscovery = new Map(); // userId -> [raw msg, ...]

  // --- Ratcheted receive helper (multi-device) ---
  function receiveRatcheted(msg) {
    const contact = profile.contacts[msg.from];
    if (!contact) return null;

    const fromDeviceId = msg.fromDeviceId || null;

    // Verify sender's identity key against known device keys
    function verifyIdentityKey(identityKey) {
      if (contact.devices) {
        for (const dev of Object.values(contact.devices)) {
          if (dev.publicKey === identityKey) return true;
        }
      }
      // Legacy fallback: check flat publicKey
      return contact.publicKey === identityKey;
    }

    // X3DH initial message — establish (or replace) receiver ratchet
    // Always process X3DH even if we have an existing state — the sender may have
    // reset their session (e.g. first message was never delivered/processed)
    if (msg.x3dh) {
      // Verify X3DH identity key matches known contact device key
      if (!verifyIdentityKey(msg.x3dh.identityKey)) {
        // Unknown device — queue message and refresh device list from server
        if (fromDeviceId) {
          if (!pendingDeviceDiscovery.has(msg.from)) {
            pendingDeviceDiscovery.set(msg.from, []);
            // Trigger device list refresh
            conn.send(MSG.USER_LOOKUP, { lookupId: msg.from });
          }
          pendingDeviceDiscovery.get(msg.from).push(msg);
          ui.showSystemMessage(`New device from ${escapeContent(msg.from)} — verifying...`);
          return null;
        }
        ui.showSystemMessage(`[SECURITY] X3DH identity key mismatch from ${escapeContent(msg.from)}! Message rejected.`);
        return null;
      }

      const otpkUsed = msg.x3dh.usedOTPKId !== null && msg.x3dh.usedOTPKId !== undefined;
      const otpkSecret = otpkUsed
        ? (() => {
            const key = store.consumeOneTimePreKey(profile, msg.x3dh.usedOTPKPub, passphrase);
            return key ? key.secretKey : null;
          })()
        : null;

      if (!profile.signedPreKey) {
        ui.showSystemMessage('Cannot establish session — no signed pre-key.');
        return null;
      }

      // Try current signed pre-key first, then archived ones
      const spkCandidates = [profile.signedPreKey];
      if (profile.previousSignedPreKeys) {
        for (const old of profile.previousSignedPreKeys) spkCandidates.push(old);
      }

      // OTP fallback: try with OTP first, then without (in case OTP was consumed/lost)
      const otpkAttempts = otpkUsed ? [otpkSecret, null] : [null];

      for (const otpk of otpkAttempts) {
        for (const spk of spkCandidates) {
          const x3dhResult = cryptoMod.x3dhRespond(
            profile.secretKey,
            spk.secretKey,
            otpk,
            msg.x3dh.identityKey,
            msg.x3dh.ephemeralKey
          );

          if (!x3dhResult) continue;

          const ratchetState = cryptoMod.ratchetInitReceiver(
            x3dhResult.sharedSecret,
            { publicKey: spk.publicKey, secretKey: spk.secretKey }
          );

          const plaintext = cryptoMod.ratchetDecrypt(ratchetState, msg.header, msg.encrypted, msg.nonce);
          if (plaintext !== null && plaintext !== undefined) {
            store.saveRatchetState(profile, msg.from, ratchetState, passphrase, fromDeviceId);
            return plaintext;
          }
        }
      }

      ui.showSystemMessage('X3DH key agreement failed (all signed pre-keys exhausted).');
      return null;
    }

    // Existing ratchet session
    if (msg.header) {
      const ratchetState = store.loadRatchetState(profile, msg.from, fromDeviceId);
      if (!ratchetState) {
        ui.showSystemMessage(`No ratchet session with ${escapeContent(msg.from)}. Message dropped.`);
        return null;
      }
      const stateBackup = JSON.parse(JSON.stringify(ratchetState));
      const plaintext = cryptoMod.ratchetDecrypt(ratchetState, msg.header, msg.encrypted, msg.nonce);
      if (plaintext) {
        store.saveRatchetState(profile, msg.from, ratchetState, passphrase, fromDeviceId);
      } else {
        store.saveRatchetState(profile, msg.from, stateBackup, passphrase, fromDeviceId);
      }
      return plaintext;
    }

    // Legacy fallback removed — Double Ratchet required for all 1:1 messages
    return null;
  }

  // --- Auth ---
  function authenticate() {
    if (isDeviceLink) {
      // Link device to existing account
      const codeHash = crypto.createHash('sha256').update(linkCode).digest('hex');
      // Proof-of-possession: sign codeHash+publicKey with our signing key
      const linkProof = cryptoMod.signString(
        'DEVICE_LINK:' + codeHash + ':' + profile.publicKey,
        profile.signingSecretKey
      );
      conn.send(MSG.DEVICE_LINK_USE, {
        userId,
        codeHash,
        publicKey: profile.publicKey,
        signingKey: profile.signingKey,
        deviceId: profile.deviceId,
        proof: linkProof,
      });
    } else if (isRegistration) {
      const displayName = userId; // userId arg is the display name for registration
      const proof = cryptoMod.signString(
        displayName + profile.publicKey,
        profile.signingSecretKey
      );
      conn.send(MSG.REGISTER, {
        inviteCode,
        publicKey: profile.publicKey,
        signingKey: profile.signingKey,
        displayName,
        deviceId: profile.deviceId,
        proof,
      });
    } else {
      conn.send(MSG.AUTH, { userId: profile.userId, deviceId: profile.deviceId });
    }
  }

  // --- Message handlers ---
  conn.on(MSG.REGISTER_OK, (msg) => {
    // Update profile with server-assigned ID
    profile.userId = msg.userId;
    profile.displayName = msg.displayName;
    if (msg.deviceId) profile.deviceId = msg.deviceId;

    // TOFU pin server signing key
    if (msg.serverSigningKey) {
      profile.serverSigningKey = msg.serverSigningKey;
      conn.serverSigningKey = msg.serverSigningKey;
    }

    store.saveProfile(profile, passphrase);

    ui.setStatus(`${msg.displayName} (${msg.userId}) | E2EE + PFS${useI2P ? ' | I2P' : useTor ? ' | Tor' : ''}`);
    ui.showSystemMessage('Registration successful!');
    ui.showSystemMessage(`Your ID: ${msg.userId}`);
    ui.showSystemMessage(`Remember this ID for login!`);
    ui.showSystemMessage(`Fingerprint: ${cryptoMod.fingerprint(profile.publicKey)}`);

    // Upload initial pre-key bundle
    uploadPreKeysIfNeeded(0);
  });

  conn.on(MSG.REGISTER_FAIL, (msg) => {
    ui.setStatus('Registration failed');
    ui.showSystemMessage(`Registration failed: ${msg.error}`);
  });

  conn.on(MSG.AUTH_CHALLENGE, (msg) => {
    // Sign the challenge and respond
    // Domain-separated: sign "AUTH_CHALLENGE:<base64>" to prevent cross-protocol signature reuse
    const signature = cryptoMod.signString('AUTH_CHALLENGE:' + msg.challenge, profile.signingSecretKey);
    conn.send(MSG.AUTH_RESPONSE, { signature });
  });

  conn.on(MSG.AUTH_OK, (msg) => {
    const name = profile.displayName || msg.userId;
    ui.setStatus(`${name} (${msg.userId}) | E2EE + PFS${useI2P ? ' | I2P' : useTor ? ' | Tor' : ''}`);
    ui.showSystemMessage('Authenticated.');

    // TOFU pin server signing key
    if (msg.serverSigningKey) {
      if (!profile.serverSigningKey) {
        profile.serverSigningKey = msg.serverSigningKey;
        conn.serverSigningKey = msg.serverSigningKey;
        store.saveProfile(profile, passphrase);
      } else if (profile.serverSigningKey !== msg.serverSigningKey) {
        ui.showSystemMessage('WARNING: Server signing key has CHANGED! Possible MITM attack.');
      } else {
        conn.serverSigningKey = msg.serverSigningKey;
      }
    }

    // Store delivery token for sealed sender
    if (msg.deliveryToken) {
      profile._deliveryToken = msg.deliveryToken;
    }

    // Upload pre-keys if running low
    uploadPreKeysIfNeeded(msg.prekeyCount);
  });

  conn.on(MSG.AUTH_FAIL, (msg) => {
    ui.setStatus('Auth failed');
    ui.showSystemMessage(`Authentication failed: ${msg.error}`);
  });

  // Incoming sealed message — unseal and process as a normal message
  conn.on(MSG.SEALED_MESSAGE, (msg) => {
    if (!msg.sealedPayload) return;
    const inner = cryptoMod.unsealMessage(msg.sealedPayload, profile.secretKey);
    if (!inner) {
      ui.showSystemMessage('Received sealed message but could not unseal.');
      return;
    }
    // Process the inner payload as if it were a normal MESSAGE
    const syntheticMsg = { ...inner, ts: msg.ts };
    const handler = conn.handlers.get(MSG.MESSAGE);
    if (handler) handler(syntheticMsg);
  });

  // Incoming 1:1 message
  conn.on(MSG.MESSAGE, (msg) => {
    // Client-side nonce deduplication (reject replays)
    if (msg.nonce && !cryptoMod.checkClientNonce(msg.nonce)) {
      ui.showSystemMessage(`Rejected duplicate nonce from ${escapeContent(msg.from || 'unknown')} (possible replay)`);
      return;
    }

    // Block messages from contacts with pending key change (possible MITM)
    if (profile._pendingKeyChange && profile._pendingKeyChange.userId === msg.from) {
      ui.showSystemMessage(`Blocked message from ${escapeContent(msg.from)} (key change pending). Use /confirm ${escapeContent(msg.from)} first.`);
      return;
    }

    const contact = profile.contacts[msg.from];
    if (!contact) {
      ui.showSystemMessage(`Message from unknown user ${escapeContent(msg.from)}. Use /add ${escapeContent(msg.from)}`);
      return;
    }

    const plaintext = receiveRatcheted(msg);

    if (plaintext === null || plaintext === undefined) {
      ui.showSystemMessage(`Could not decrypt message from ${escapeContent(msg.from)}.`);
      return;
    }

    // Intercept control messages (JSON-encoded with __rede_ctrl field)
    let ctrl = null;
    try { ctrl = JSON.parse(plaintext); } catch {}
    if (ctrl && ctrl.__rede_ctrl) {
      if (ctrl.__rede_ctrl === 'senderkey' && ctrl.groupId && ctrl.chainKey) {
        // Verify sender is a known member of the target group
        const group = profile.groups[ctrl.groupId];
        if (!group) {
          ui.showSystemMessage(`REJECTED sender key from ${escapeContent(msg.from)} — unknown group ${escapeContent(ctrl.groupId)}`);
          return;
        }
        if (!group.members || !group.members.includes(msg.from)) {
          ui.showSystemMessage(`REJECTED sender key from ${escapeContent(msg.from)} — not a group member`);
          return;
        }
        // Verify sender key is signed by the sender's signing key
        if (!ctrl.sig || !contact.signingKey) {
          ui.showSystemMessage(`REJECTED sender key from ${escapeContent(msg.from)} — missing signature`);
          return;
        }
        const skPayload = `SENDERKEY:${ctrl.groupId}:${ctrl.chainKey}:${ctrl.messageNumber || 0}`;
        const skPayloadBytes = require('tweetnacl-util').decodeUTF8(skPayload);
        if (!cryptoMod.verifyBytes(skPayloadBytes, ctrl.sig, contact.signingKey)) {
          ui.showSystemMessage(`REJECTED sender key from ${escapeContent(msg.from)} — invalid signature! Possible attack.`);
          return;
        }
        // Validate chainKey format (must be 32-byte base64)
        try {
          const ckBytes = cryptoMod.decodeBase64(ctrl.chainKey);
          if (ckBytes.length !== 32) throw new Error('Bad length');
        } catch {
          ui.showSystemMessage(`REJECTED sender key from ${escapeContent(msg.from)} — invalid chainKey format`);
          return;
        }
        let skState = store.loadSenderKeyState(profile, ctrl.groupId) || { own: null, members: {} };
        skState.members[msg.from] = { chainKey: ctrl.chainKey, messageNumber: ctrl.messageNumber || 0 };
        store.saveSenderKeyState(profile, ctrl.groupId, skState, passphrase);
        ui.showSystemMessage(`Received sender key for group from ${escapeContent(msg.from)}`);
      } else if (ctrl.__rede_ctrl === 'groupkey' && ctrl.groupId && ctrl.key) {
        if (!ctrl.sig || !contact.signingKey || !cryptoMod.verifyGroupKey(ctrl.groupId, ctrl.name, ctrl.key, ctrl.sig, contact.signingKey)) {
          ui.showSystemMessage(`REJECTED group key from ${escapeContent(msg.from)} — invalid signature! Possible attack.`);
          return;
        }
        store.addGroup(profile, ctrl.groupId, ctrl.name, ctrl.key, [], passphrase);
        refreshContactList();
        ui.showSystemMessage(`Joined group "${escapeContent(ctrl.name)}" (verified key from ${escapeContent(msg.from)})`);
      }
      return;
    }
    // Reject legacy format for security
    if (plaintext.startsWith('__SENDERKEY__:') || plaintext.startsWith('__GROUPKEY__:')) {
      return;
    }

    const chatMsg = { from: msg.from, text: plaintext, ts: Date.now(), ttl: msg.ttl || 0 };
    store.addChatMessage(profile, msg.from, chatMsg, passphrase);

    if (currentChatTarget === msg.from) {
      const time = new Date(chatMsg.ts).toLocaleTimeString();
      const ttlInfo = chatMsg.ttl > 0 ? ` [${chatMsg.ttl}s]` : '';
      ui.addChatLine(`${time} ${escapeContent(contact.alias || msg.from)}: ${escapeContent(plaintext)}${ttlInfo}`);
    } else {
      ui.showSystemMessage(`New message from ${escapeContent(contact.alias || msg.from)}`);
    }
  });

  // Incoming group message
  conn.on(MSG.GROUP_MESSAGE, (msg) => {
    // Client-side nonce deduplication
    if (msg.nonce && !cryptoMod.checkClientNonce(`g:${msg.nonce}`)) return;

    const group = profile.groups[msg.groupId];
    if (!group) return;

    let plaintext = null;

    // Try Sender Keys first (v3)
    if (msg.senderKeyHeader) {
      const skState = store.loadSenderKeyState(profile, msg.groupId);
      const senderState = skState?.members?.[msg.from];
      const contact = profile.contacts[msg.from];
      if (senderState && contact?.signingKey) {
        plaintext = cryptoMod.senderKeyDecrypt(
          senderState, msg.encrypted, msg.nonce,
          msg.senderKeyHeader.messageNumber, msg.senderKeyHeader.signature,
          contact.signingKey, msg.groupId
        );
        if (plaintext) {
          store.saveSenderKeyState(profile, msg.groupId, skState, passphrase);
        }
      }
    }

    // Legacy fallback removed — Sender Keys required for group messages
    // (group.key only used for key distribution, not message encryption)

    if (!plaintext) {
      ui.showSystemMessage(`Could not decrypt group message in ${escapeContent(group.name)}.`);
      return;
    }

    const senderName = profile.contacts[msg.from]?.alias || msg.from;
    const chatMsg = { from: msg.from, text: plaintext, ts: Date.now(), ttl: msg.ttl || 0 };
    store.addChatMessage(profile, msg.groupId, chatMsg, passphrase);

    if (currentChatTarget === msg.groupId) {
      const time = new Date(chatMsg.ts).toLocaleTimeString();
      const ttlInfo = chatMsg.ttl > 0 ? ` [${chatMsg.ttl}s]` : '';
      ui.addChatLine(`${time} ${escapeContent(senderName)}: ${escapeContent(plaintext)}${ttlInfo}`);
    } else {
      ui.showSystemMessage(`New group message in ${escapeContent(group.name)}`);
    }
  });

  conn.on(MSG.MESSAGE_ACK, () => {});

  conn.on(MSG.GROUP_CREATE_OK, (msg) => {
    const groupKey = cryptoMod.generateGroupKey();
    store.addGroup(profile, msg.groupId, msg.name, groupKey, [profile.userId], passphrase);
    refreshContactList();
    ui.showSystemMessage(`Group "${escapeContent(msg.name)}" created. ID: ${msg.groupId}`);
  });

  conn.on(MSG.GROUP_INVITE, (msg) => {
    ui.showSystemMessage(`Invited to group "${escapeContent(msg.name)}" by ${escapeContent(msg.from)}`);
  });

  conn.on(MSG.USER_LOOKUP_OK, (msg) => {
    const displayName = msg.displayName || msg.userId;
    const devices = msg.devices || null;

    // Check if this is a device discovery refresh (not a user-initiated /add)
    const queued = pendingDeviceDiscovery.get(msg.userId);
    if (queued) {
      pendingDeviceDiscovery.delete(msg.userId);
      // Update contact's device list (verify keys before accepting)
      const contact = profile.contacts[msg.userId];
      if (contact && devices) {
        const oldDevices = contact.devices || {};
        let changedDevices = [];
        for (const [dId, dInfo] of Object.entries(devices)) {
          if (typeof dInfo !== 'object' || !dInfo.publicKey || !dInfo.signingKey) continue;
          // Validate key format (32-byte base64)
          try {
            const pk = cryptoMod.decodeBase64(dInfo.publicKey);
            const sk = cryptoMod.decodeBase64(dInfo.signingKey);
            if (pk.length !== 32 || sk.length !== 32) continue;
          } catch { continue; }
          const old = oldDevices[dId];
          if (old && (old.publicKey !== dInfo.publicKey || old.signingKey !== dInfo.signingKey)) {
            changedDevices.push(dId);
          }
        }
        if (changedDevices.length > 0) {
          ui.showSystemMessage(`[SECURITY] Device key change detected for ${escapeContent(displayName)}: ${changedDevices.join(', ')}. Verify out-of-band!`);
        }
        contact.devices = devices;
        store.saveProfile(profile, passphrase);
        ui.showSystemMessage(`Device list updated for ${escapeContent(displayName)} (${Object.keys(devices).length} device(s))`);
      }
      // Retry queued messages
      for (const qMsg of queued) {
        const handler = conn.handlers.get(MSG.MESSAGE);
        if (handler) handler(qMsg);
      }
      return;
    }

    const result = store.addContact(profile, msg.userId, msg.publicKey, msg.signingKey, displayName, passphrase, devices);
    if (result.warning) {
      ui.showSystemMessage(`WARNING: Key changed for ${escapeContent(displayName)} (${escapeContent(msg.userId)})!`);
      ui.showSystemMessage(`  Old: ${result.oldFingerprint}`);
      ui.showSystemMessage(`  New: ${result.newFingerprint}`);
      ui.showSystemMessage(`  Use /confirm ${escapeContent(msg.userId)} to accept the new key.`);
      profile._pendingKeyChange = { userId: msg.userId, publicKey: msg.publicKey, signingKey: msg.signingKey, devices };
    } else {
      refreshContactList();
      const fp = cryptoMod.fingerprint(msg.publicKey);
      const devCount = devices ? Object.keys(devices).length : 1;
      ui.showSystemMessage(`Contact added: ${escapeContent(displayName)} (${escapeContent(msg.userId)}) [${devCount} device(s)]`);
      ui.showSystemMessage(`  Fingerprint: ${fp}`);
      ui.showSystemMessage(`  Verify this fingerprint out-of-band!`);
    }
  });

  conn.on(MSG.USER_LOOKUP_FAIL, (msg) => {
    ui.showSystemMessage(`User not found: ${escapeContent(msg.lookupId)}`);
  });

  conn.on(MSG.PENDING_MESSAGES, (msg) => {
    if (msg.messages && msg.messages.length > 0) {
      ui.showSystemMessage(`${msg.messages.length} pending message(s) received.`);
      for (const pm of msg.messages) {
        let handlerType;
        if (pm.type === MSG.SEALED_MESSAGE || pm.sealed) {
          handlerType = MSG.SEALED_MESSAGE;
        } else if (pm.type === MSG.GROUP_MESSAGE) {
          handlerType = MSG.GROUP_MESSAGE;
        } else {
          handlerType = MSG.MESSAGE;
        }
        const handler = conn.handlers.get(handlerType);
        if (handler) handler(pm);
      }
    }
  });

  // Pre-key bundle received — complete X3DH and send pending message (multi-device)
  conn.on(MSG.PREKEY_BUNDLE, (msg) => {
    if (!pendingOutgoing || pendingOutgoing.target !== msg.targetUserId) return;

    const contact = profile.contacts[msg.targetUserId];
    if (!contact) {
      ui.showSystemMessage(`Cannot establish session — ${escapeContent(msg.targetUserId)} is not a contact.`);
      pendingOutgoing = null;
      return;
    }

    // Process per-device bundles (new format) or single bundle (legacy)
    const deviceBundles = msg.devices || [{
      deviceId: null,
      identityKey: msg.identityKey,
      signingKey: msg.signingKey,
      signedPreKey: msg.signedPreKey,
      signedPreKeySig: msg.signedPreKeySig,
      oneTimePreKey: msg.oneTimePreKey,
    }];

    let successCount = 0;
    for (const bundle of deviceBundles) {
      const devId = bundle.deviceId;

      // Skip devices we already have sessions with
      if (store.loadRatchetState(profile, msg.targetUserId, devId)) {
        successCount++;
        continue;
      }

      // Verify identity key matches known contact device key
      let keyValid = false;
      if (contact.devices) {
        const dev = contact.devices[devId] || contact.devices.primary;
        if (dev && dev.publicKey === bundle.identityKey) keyValid = true;
      }
      if (!keyValid && contact.publicKey === bundle.identityKey) keyValid = true;

      if (!keyValid) {
        // New device — verify signed pre-key signature before accepting
        if (devId && bundle.identityKey && bundle.signingKey && bundle.signedPreKey && bundle.signedPreKeySig) {
          try {
            const sigBytes = cryptoMod.decodeBase64(bundle.signedPreKeySig);
            const preKeyBytes = cryptoMod.decodeBase64(bundle.signedPreKey);
            const sigKeyBytes = cryptoMod.decodeBase64(bundle.signingKey);
            if (sigBytes.length === 64 && preKeyBytes.length === 32 && sigKeyBytes.length === 32) {
              const nacl = require('tweetnacl');
              if (nacl.sign.detached.verify(preKeyBytes, sigBytes, sigKeyBytes)) {
                if (!contact.devices) contact.devices = {};
                contact.devices[devId] = { publicKey: bundle.identityKey, signingKey: bundle.signingKey };
                store.saveProfile(profile, passphrase);
                keyValid = true;
                ui.showSystemMessage(`[SECURITY] New device ${devId} for ${escapeContent(msg.targetUserId)} accepted (pre-key signature verified). Verify fingerprint out-of-band!`);
              }
            }
          } catch {
            // Signature verification failed — reject
          }
        }
      }

      if (!keyValid) {
        ui.showSystemMessage(`[SECURITY] Pre-key bundle identity key mismatch for ${escapeContent(msg.targetUserId)} device ${devId || 'primary'}! Skipped.`);
        continue;
      }

      const x3dhResult = cryptoMod.x3dhInitiate(profile.secretKey, {
        identityKey: bundle.identityKey,
        signingKey: bundle.signingKey,
        signedPreKey: bundle.signedPreKey,
        signedPreKeySig: bundle.signedPreKeySig,
        oneTimePreKey: bundle.oneTimePreKey ? { id: 0, key: bundle.oneTimePreKey } : null,
      });

      if (!x3dhResult) {
        ui.showSystemMessage(`X3DH failed for device ${devId || 'primary'} — invalid pre-key signature.`);
        continue;
      }

      const ratchetState = cryptoMod.ratchetInitSender(x3dhResult.sharedSecret, bundle.signedPreKey);
      const result = cryptoMod.ratchetEncrypt(ratchetState, pendingOutgoing.text);
      store.saveRatchetState(profile, msg.targetUserId, ratchetState, passphrase, devId);

      conn.send(MSG.MESSAGE, {
        to: msg.targetUserId,
        toDeviceId: devId,
        encrypted: result.ciphertext,
        nonce: result.nonce,
        header: result.header,
        x3dh: {
          identityKey: profile.publicKey,
          ephemeralKey: x3dhResult.ephemeralPublic,
          usedOTPKPub: bundle.oneTimePreKey || null,
          usedOTPKId: x3dhResult.usedOTPKId,
        },
        ttl: pendingOutgoing.ttl,
      });
      successCount++;
    }

    if (successCount > 0) {
      const time = new Date().toLocaleTimeString();
      const ttlInfo = pendingOutgoing.ttl > 0 ? ` [${pendingOutgoing.ttl}s]` : '';
      store.addChatMessage(profile, msg.targetUserId, {
        from: profile.userId, text: pendingOutgoing.text, ts: Date.now(), ttl: pendingOutgoing.ttl,
      }, passphrase);
      if (currentChatTarget === msg.targetUserId) {
        ui.addChatLine(`${time} You: ${escapeContent(pendingOutgoing.text)}${ttlInfo}`);
      }
      ui.showSystemMessage(`Secure session established (${successCount} device(s)).`);
    } else {
      ui.showSystemMessage('Failed to establish session with any device.');
    }

    pendingOutgoing = null;
  });

  conn.on(MSG.PREKEY_BUNDLE_FAIL, (msg) => {
    ui.showSystemMessage(`Could not fetch pre-key bundle: ${escapeContent(msg.error)}`);
    pendingOutgoing = null;
  });

  conn.on(MSG.UPLOAD_PREKEYS_OK, (msg) => {
    ui.showSystemMessage(`Pre-keys uploaded (${msg.count} OTPs).`);
  });

  conn.on(MSG.DEVICE_ADDED, (msg) => {
    ui.showSystemMessage(`New device linked: ${msg.deviceId}`);
    // Store new device keys for own user (so other devices know about us)
    if (msg.deviceId && msg.publicKey && msg.signingKey) {
      // Validate key format
      try {
        const pk = cryptoMod.decodeBase64(msg.publicKey);
        const sk = cryptoMod.decodeBase64(msg.signingKey);
        if (pk.length === 32 && sk.length === 32) {
          if (!profile.ownDevices) profile.ownDevices = {};
          profile.ownDevices[msg.deviceId] = { publicKey: msg.publicKey, signingKey: msg.signingKey };
          store.saveProfile(profile, passphrase);
          ui.showSystemMessage(`Device ${msg.deviceId} keys stored.`);
        }
      } catch {
        ui.showSystemMessage(`[SECURITY] Invalid device keys in DEVICE_ADDED — ignored.`);
      }
    }
  });

  conn.on(MSG.DEVICE_LINK_CREATE_OK, () => {});

  conn.on(MSG.DEVICE_LINK_OK, (msg) => {
    // This device was just linked
    profile.userId = msg.userId;
    profile.displayName = msg.displayName;
    if (msg.deviceId) profile.deviceId = msg.deviceId;
    if (msg.serverSigningKey) {
      profile.serverSigningKey = msg.serverSigningKey;
      conn.serverSigningKey = msg.serverSigningKey;
    }
    store.saveProfile(profile, passphrase);
    ui.setStatus(`${msg.displayName} (${msg.userId}) [${msg.deviceId}] | E2EE + PFS`);
    ui.showSystemMessage(`Device linked successfully! Device ID: ${msg.deviceId}`);
    uploadPreKeysIfNeeded(0);
  });

  conn.on(MSG.DEVICE_LINK_FAIL, (msg) => {
    ui.showSystemMessage(`Device link failed: ${escapeContent(msg.error)}`);
  });

  conn.on(MSG.GROUP_KICK_OK, (msg) => {
    ui.showSystemMessage(`Removed ${escapeContent(msg.targetUserId)} from group ${escapeContent(msg.groupId)}. Use /rekey to rotate group key.`);
  });

  conn.on(MSG.ERROR, (msg) => {
    ui.showSystemMessage(`Error: ${escapeContent(msg.error)}`);
  });

  conn.on(MSG.INVITE_CREATE_OK, (msg) => {
    ui.showSystemMessage(`New invite code: ${msg.code}`);
  });

  // --- Command handler ---
  ui.onCommand = (cmd, ...cmdArgs) => {
    switch (cmd) {
      case 'add': {
        const targetId = cmdArgs[0];
        if (!targetId) { ui.showSystemMessage('Usage: /add <userId>'); return; }
        conn.send(MSG.USER_LOOKUP, { lookupId: targetId });
        break;
      }
      case 'confirm': {
        const pending = profile._pendingKeyChange;
        if (!pending || pending.userId !== cmdArgs[0]) {
          ui.showSystemMessage('No pending key change for this user.');
          return;
        }
        store.confirmContactKeyChange(profile, pending.userId, pending.publicKey, pending.signingKey, pending.userId, passphrase, pending.devices);
        delete profile._pendingKeyChange;
        refreshContactList();
        ui.showSystemMessage(`Key updated for ${escapeContent(pending.userId)}.`);
        break;
      }
      case 'reset_session': case 'reset': {
        const targetId = cmdArgs[0];
        if (!targetId) {
          ui.showSystemMessage('Usage: /reset_session <userId>');
          return;
        }
        if (!profile.contacts[targetId]) {
          ui.showSystemMessage(`Unknown contact: ${escapeContent(targetId)}`);
          return;
        }
        const deleted = store.deleteRatchetState(profile, targetId, passphrase);
        if (deleted) {
          ui.showSystemMessage(`Ratchet session reset for ${escapeContent(targetId)}. A new session will be established on next message.`);
        } else {
          ui.showSystemMessage(`No active session found for ${escapeContent(targetId)}.`);
        }
        break;
      }
      case 'select': {
        const name = cmdArgs[0];
        // Match by displayName or internalId
        for (const [id, c] of Object.entries(profile.contacts)) {
          if ((c.displayName || c.alias) === name || id === name) {
            currentChatTarget = id;
            currentChatType = 'user';
            ui.setCurrentChat(escapeContent(c.displayName || c.alias || id));
            ui.clearChat();
            loadChatHistory(id);
            return;
          }
        }
        for (const [gid, g] of Object.entries(profile.groups)) {
          if (g.name === name || gid === name) {
            currentChatTarget = gid;
            currentChatType = 'group';
            ui.setCurrentChat(`# ${escapeContent(g.name)}`);
            ui.clearChat();
            loadChatHistory(gid);
            return;
          }
        }
        break;
      }
      case 'group': {
        const name = cmdArgs.join(' ') || 'Unnamed';
        conn.send(MSG.GROUP_CREATE, { name });
        break;
      }
      case 'ginvite': {
        const [groupName, inviteeId] = cmdArgs;
        if (!groupName || !inviteeId) {
          ui.showSystemMessage('Usage: /ginvite <groupId> <userId>');
          return;
        }
        let groupId = null;
        for (const [gid, g] of Object.entries(profile.groups)) {
          if (g.name === groupName || gid === groupName) { groupId = gid; break; }
        }
        if (!groupId) { ui.showSystemMessage('Group not found'); return; }

        conn.send(MSG.GROUP_INVITE, { groupId, inviteeId });

        const contact = profile.contacts[inviteeId];
        if (contact) {
          const group = profile.groups[groupId];
          // Send group key via ratcheted DM (legacy format for backward compat)
          const sig = cryptoMod.signGroupKey(groupId, group.name, group.key, profile.signingSecretKey);
          const keyMsg = JSON.stringify({ __rede_ctrl: 'groupkey', groupId, name: group.name, key: group.key, sig });
          sendRatcheted(inviteeId, keyMsg, 60);

          // Also distribute our sender key if we have one
          const skState = store.loadSenderKeyState(profile, groupId);
          if (skState && skState.own) {
            const skSigPayload = `SENDERKEY:${groupId}:${skState.own.chainKey}:${skState.own.messageNumber}`;
            const skSig = cryptoMod.signString(skSigPayload, profile.signingSecretKey);
            const skMsg = JSON.stringify({ __rede_ctrl: 'senderkey', groupId, chainKey: skState.own.chainKey, messageNumber: skState.own.messageNumber, sig: skSig });
            sendRatcheted(inviteeId, skMsg, 120);
          }

          ui.showSystemMessage(`Group key sent to ${escapeContent(inviteeId)} (ratcheted, auto-expires in 60s)`);
        } else {
          ui.showSystemMessage(`Add ${escapeContent(inviteeId)} as contact first.`);
        }
        break;
      }
      case 'ttl': {
        const seconds = parseInt(cmdArgs[0], 10);
        if (isNaN(seconds) || seconds < 0) {
          ui.showSystemMessage('Usage: /ttl <seconds> (0 = permanent)');
          return;
        }
        currentTTL = Math.min(seconds, 86400);
        ui.showSystemMessage(seconds > 0 ? `TTL set to ${seconds}s` : 'TTL disabled');
        break;
      }
      case 'contacts': {
        ui.showSystemMessage('Contacts:');
        for (const [id, c] of Object.entries(profile.contacts)) {
          const fp = cryptoMod.fingerprint(c.publicKey);
          ui.addChatLine(`  ${escapeContent(c.alias || id)} - ${fp}`);
        }
        if (Object.keys(profile.contacts).length === 0) ui.addChatLine('  (none)');
        break;
      }
      case 'groups': {
        ui.showSystemMessage('Groups:');
        for (const [id, g] of Object.entries(profile.groups)) {
          ui.addChatLine(`  # ${escapeContent(g.name)} (${id})`);
        }
        if (Object.keys(profile.groups).length === 0) ui.addChatLine('  (none)');
        break;
      }
      case 'fingerprint': case 'fp': {
        const target = cmdArgs[0];
        if (target && profile.contacts[target]) {
          ui.showSystemMessage(`${escapeContent(target)}: ${cryptoMod.fingerprint(profile.contacts[target].publicKey)}`);
        } else {
          ui.showSystemMessage(`Your fingerprint: ${cryptoMod.fingerprint(profile.publicKey)}`);
        }
        break;
      }
      case 'key': {
        ui.showSystemMessage(`Public key: ${profile.publicKey}`);
        ui.showSystemMessage(`Fingerprint: ${cryptoMod.fingerprint(profile.publicKey)}`);
        break;
      }
      case 'rekey': {
        const groupName = cmdArgs[0];
        if (!groupName) { ui.showSystemMessage('Usage: /rekey <groupName>'); return; }
        let groupId = null;
        for (const [gid, g] of Object.entries(profile.groups)) {
          if (g.name === groupName || gid === groupName) { groupId = gid; break; }
        }
        if (!groupId) { ui.showSystemMessage('Group not found'); return; }
        const group = profile.groups[groupId];
        // Generate new group key
        const newKey = cryptoMod.generateGroupKey();
        group.key = newKey;
        store.saveProfile(profile, passphrase);
        // Distribute new key to all known contacts in the group via ratcheted DM
        let sent = 0;
        for (const memberId of (group.members || [])) {
          if (memberId === profile.userId) continue;
          const contact = profile.contacts[memberId];
          if (!contact) continue;
          const sig = cryptoMod.signGroupKey(groupId, group.name, newKey, profile.signingSecretKey);
          const keyMsg = JSON.stringify({ __rede_ctrl: 'groupkey', groupId, name: group.name, key: newKey, sig });
          sendRatcheted(memberId, keyMsg, 120);
          sent++;
        }
        ui.showSystemMessage(`Group key rotated for "${escapeContent(group.name)}". New key sent to ${sent} member(s).`);
        break;
      }
      case 'kick': {
        const [groupName, targetId] = cmdArgs;
        if (!groupName || !targetId) {
          ui.showSystemMessage('Usage: /kick <groupId> <userId>');
          return;
        }
        let groupId = null;
        for (const [gid, g] of Object.entries(profile.groups)) {
          if (g.name === groupName || gid === groupName) { groupId = gid; break; }
        }
        if (!groupId) { ui.showSystemMessage('Group not found'); return; }
        conn.send(MSG.GROUP_KICK, { groupId, targetUserId: targetId });
        break;
      }
      case 'link': {
        // Generate device link code for adding a new device
        const linkCode = crypto.randomBytes(16).toString('hex');
        const codeHash = crypto.createHash('sha256').update(linkCode).digest('hex');
        conn.send(MSG.DEVICE_LINK_CREATE, { codeHash });
        ui.showSystemMessage(`Device link code (expires in 5 min):`);
        ui.showSystemMessage(`  ${linkCode}`);
        ui.showSystemMessage(`On the new device, run:`);
        ui.showSystemMessage(`  node client/index.js -u ${profile.userId} --link ${linkCode}`);
        break;
      }
      case 'devices': {
        ui.showSystemMessage(`Your device: ${profile.deviceId}`);
        break;
      }
      case 'help': {
        ui.showHelp();
        break;
      }
      case 'quit': {
        conn.disconnect();
        process.exit(0);
        break;
      }
      default:
        ui.showSystemMessage(`Unknown command: /${escapeContent(cmd)}`);
    }
  };

  // --- Outgoing messages ---
  ui.onMessage = (text) => {
    if (!currentChatTarget) {
      ui.showSystemMessage('Select a contact or group first (Tab + Enter)');
      return;
    }

    const time = new Date().toLocaleTimeString();
    const ttlInfo = currentTTL > 0 ? ` [${currentTTL}s]` : '';

    if (currentChatType === 'user') {
      const contact = profile.contacts[currentChatTarget];
      if (!contact) { ui.showSystemMessage('Contact not found'); return; }

      // Ratcheted send (X3DH + Double Ratchet)
      sendRatcheted(currentChatTarget, text, currentTTL);

      // Store chat history locally (sendRatcheted handles server send)
      // Check if we have sessions with any device of the contact
      const contact2 = profile.contacts[currentChatTarget];
      const devIds = contact2 && contact2.devices ? Object.keys(contact2.devices) : [];
      const hasSession = devIds.some(d => store.loadRatchetState(profile, currentChatTarget, d))
        || store.loadRatchetState(profile, currentChatTarget);
      if (hasSession && !pendingOutgoing) {
        // Message was sent immediately (had existing session)
        store.addChatMessage(profile, currentChatTarget, {
          from: profile.userId, text, ts: Date.now(), ttl: currentTTL,
        }, passphrase);
        ui.addChatLine(`${time} You: ${escapeContent(text)}${ttlInfo}`);
      }
      // If no ratchet state, message is pending — will be added when PREKEY_BUNDLE arrives

    } else if (currentChatType === 'group') {
      const group = profile.groups[currentChatTarget];
      if (!group) { ui.showSystemMessage('Group not found'); return; }

      // Try Sender Keys (v3) first
      let skState = store.loadSenderKeyState(profile, currentChatTarget);
      if (skState && skState.own) {
        const result = cryptoMod.senderKeyEncrypt(skState.own, text, profile.signingSecretKey, currentChatTarget);
        store.saveSenderKeyState(profile, currentChatTarget, skState, passphrase);

        conn.send(MSG.GROUP_MESSAGE, {
          groupId: currentChatTarget,
          encrypted: result.ciphertext,
          nonce: result.nonce,
          senderKeyHeader: { messageNumber: result.messageNumber, signature: result.signature },
          ttl: currentTTL,
        });
      } else {
        // Generate sender key and distribute via ratcheted DMs
        const newKey = cryptoMod.generateSenderKey();
        if (!skState) skState = { own: null, members: {} };
        skState.own = newKey;
        store.saveSenderKeyState(profile, currentChatTarget, skState, passphrase);

        // Distribute to group members via ratcheted 1:1
        for (const memberId of (group.members || [])) {
          if (memberId === profile.userId) continue;
          if (!profile.contacts[memberId]) continue;
          const skSigPayload = `SENDERKEY:${currentChatTarget}:${newKey.chainKey}:${newKey.messageNumber}`;
          const skSig = cryptoMod.signString(skSigPayload, profile.signingSecretKey);
          const keyMsg = JSON.stringify({ __rede_ctrl: 'senderkey', groupId: currentChatTarget, chainKey: newKey.chainKey, messageNumber: newKey.messageNumber, sig: skSig });
          sendRatcheted(memberId, keyMsg, 120);
        }

        // Now encrypt with the sender key
        const result = cryptoMod.senderKeyEncrypt(skState.own, text, profile.signingSecretKey, currentChatTarget);
        store.saveSenderKeyState(profile, currentChatTarget, skState, passphrase);

        conn.send(MSG.GROUP_MESSAGE, {
          groupId: currentChatTarget,
          encrypted: result.ciphertext,
          nonce: result.nonce,
          senderKeyHeader: { messageNumber: result.messageNumber, signature: result.signature },
          ttl: currentTTL,
        });
      }

      store.addChatMessage(profile, currentChatTarget, {
        from: profile.userId, text, ts: Date.now(), ttl: currentTTL,
      }, passphrase);
      ui.addChatLine(`${time} You: ${escapeContent(text)}${ttlInfo}`);
    }
  };

  function loadChatHistory(chatId) {
    store.cleanupExpiredMessages(profile, passphrase);
    const history = profile.chatHistory[chatId] || [];
    for (const msg of history.slice(-50)) {
      const time = new Date(msg.ts).toLocaleTimeString();
      const sender = msg.from === profile.userId ? 'You' : escapeContent(profile.contacts[msg.from]?.alias || msg.from);
      const ttlInfo = msg.ttl > 0 ? ` [${msg.ttl}s]` : '';
      ui.addChatLine(`${time} ${sender}: ${escapeContent(msg.text)}${ttlInfo}`);
    }
  }

  function refreshContactList() {
    const items = [];
    for (const [id, c] of Object.entries(profile.contacts)) {
      items.push(c.displayName || c.alias || id);
    }
    for (const [, g] of Object.entries(profile.groups)) {
      items.push(`# ${g.name}`);
    }
    ui.updateContacts(items);
  }

  // --- Periodic TTL cleanup ---
  setInterval(() => store.cleanupExpiredMessages(profile, passphrase), 30000);

  // --- Graceful shutdown: zero sensitive data in memory ---
  function cleanupOnExit() {
    if (profile.secretKey) profile.secretKey = '';
    if (profile.signingSecretKey) profile.signingSecretKey = '';
    conn.disconnect();
  }
  process.on('SIGTERM', () => { cleanupOnExit(); process.exit(0); });

  // Set conn.serverSigningKey from profile on startup (for server sig verification)
  if (profile.serverSigningKey) {
    conn.serverSigningKey = profile.serverSigningKey;
  }

  // --- Start ---
  authenticate();
  refreshContactList();
  ui.showHelp();
  ui.render();
}

main().catch((err) => {
  process.stdout.write('\x1b[?25h'); // restore cursor
  console.error('\nFatal error:', err.message);
  if (err.stack) console.error(err.stack);
  // On Windows, keep window open so user can read the error
  if (process.platform === 'win32') {
    console.error('\nPress any key to exit...');
    try {
      require('fs').readSync(0, Buffer.alloc(1), 0, 1);
    } catch {}
  }
  process.exit(1);
});
