'use strict';

require('dotenv').config({ path: require('path').join(__dirname, '..', '.env') });

const { MSG } = require('../shared/protocol');
const { RedeConnection } = require('./network');
const { cliBoot } = require('./boot');
const cryptoMod = require('./crypto');
const store = require('./store');

// --- Parse CLI args ---
const args = process.argv.slice(2);
const crypto = require('crypto');
const command = args[0];

const opts = {};
for (let i = 1; i < args.length; i++) {
  switch (args[i]) {
    case '--server': case '-s': opts.server = args[++i]; break;
    case '--i2p': opts.i2p = true; break;
    case '--tor': opts.tor = true; break;
    case '--user': case '-u': opts.user = args[++i]; break;
    case '--to': case '-t': opts.to = args[++i]; break;
    case '--group': case '-g': opts.group = args[++i]; break;
    case '--invite': case '-i': opts.invite = args[++i]; break;
    case '--link': opts.link = args[++i]; break;
    case '--ttl': opts.ttl = parseInt(args[++i], 10); break;
    case '--tor-proxy': opts.torProxy = args[++i]; break;
    case '--i2p-proxy': opts.i2pProxy = args[++i]; break;
    default:
      if (!args[i].startsWith('-') && !opts.message) {
        // Collect remaining args as message
        opts.message = args.slice(i).join(' ');
        i = args.length;
      }
  }
}

const HELP = `
rede - Secure Anonymous Messenger CLI

Usage: node client/cli.js <command> [options]

Commands:
  register   Register a new account
  link       Link a new device to existing account
  send       Send a message
  read       Read recent messages
  contacts   List contacts
  groups     List groups
  add        Add a contact
  group-new  Create a group
  ginvite    Invite user to group
  key        Show your public key & fingerprint
  listen     Listen for incoming messages (stays open)
  gen-link   Generate a device link code (from existing device)

Options:
  -u, --user <id>       Your user ID
  -s, --server <url>    Server URL
  -t, --to <userId>     Recipient (for send)
  -g, --group <id>      Group ID (for send, ginvite)
  -i, --invite <code>   Invite code (for register)
  --i2p                 Connect via I2P
  --tor                 Connect via Tor
  --ttl <seconds>       Self-destruct timer

Examples:
  node client/cli.js register -u alice -i CODE123 -s wss://server:9377
  node client/cli.js send -u alice -t bob "Hallo Bob!"
  node client/cli.js send -u alice -g GROUPID "Hallo Gruppe!"
  node client/cli.js read -u alice -t bob
  node client/cli.js read -u alice -g GROUPID
  node client/cli.js contacts -u alice
  node client/cli.js add -u alice -t bob
  node client/cli.js listen -u alice --i2p
`;

if (!command || command === 'help' || command === '--help' || command === '-h') {
  console.log(HELP);
  process.exit(0);
}

if (!opts.user) {
  console.error('Error: --user / -u is required');
  process.exit(1);
}

function getDefaultServer() {
  // Check .env / REDE_SERVER first
  if (process.env.REDE_SERVER) return process.env.REDE_SERVER;

  if (opts.i2p) {
    // Try to load I2P address from config file
    const fs = require('fs');
    const path = require('path');
    const addrPaths = [
      path.join(__dirname, '..', 'i2p', 'rede.i2p.addr'),
      path.join(process.cwd(), 'i2p', 'rede.i2p.addr'),
    ];
    for (const p of addrPaths) {
      try {
        const addr = fs.readFileSync(p, 'utf8').trim();
        if (addr) return `ws://${addr}`;
      } catch {}
    }
    console.error('Error: No I2P address found. Set --server, REDE_SERVER, or create i2p/rede.i2p.addr');
    process.exit(1);
  }
  return 'wss://localhost:9377';
}

// CLI flag --server > .env REDE_SERVER > getDefaultServer() fallback
const envTransport = (process.env.REDE_TRANSPORT || '').toLowerCase();
if (!opts.i2p && envTransport === 'i2p') opts.i2p = true;
if (!opts.tor && envTransport === 'tor') opts.tor = true;
if (!opts.torProxy && process.env.REDE_TOR_PROXY) opts.torProxy = process.env.REDE_TOR_PROXY;
if (!opts.i2pProxy && process.env.REDE_I2P_PROXY) opts.i2pProxy = process.env.REDE_I2P_PROXY;

const serverUrl = opts.server || getDefaultServer();

// --- Passphrase ---
function askPassphrase(prompt, keepEnv = false) {
  return new Promise((resolve) => {
    if (process.env.REDE_PASS) {
      process.stderr.write('[WARNING] Using REDE_PASS env var — visible via /proc and ps. Use interactive prompt for security.\n');
      const pass = process.env.REDE_PASS;
      if (!keepEnv) delete process.env.REDE_PASS;
      return resolve(pass);
    }
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

// --- Connect and authenticate ---
async function connectAndAuth(profile, passphrase) {
  const spinner = await cliBoot({
    userId: opts.user,
    useI2P: opts.i2p,
    useTor: opts.tor,
    command: command,
  });

  const connOpts = { useTor: opts.tor, useI2P: opts.i2p };
  if (opts.torProxy) connOpts.torProxy = opts.torProxy;
  if (opts.i2pProxy) connOpts.i2pProxy = opts.i2pProxy;
  const conn = new RedeConnection(serverUrl, connOpts);

  // Set server signing key from profile on startup (for server sig verification)
  if (profile.serverSigningKey) {
    conn.serverSigningKey = profile.serverSigningKey;
  }

  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      spinner.fail('TIMEOUT');
      conn.disconnect();
      reject(new Error('Connection timeout'));
    }, opts.i2p ? 120000 : 30000);

    conn.on(MSG.AUTH_CHALLENGE, (msg) => {
      // Domain-separated: sign "AUTH_CHALLENGE:<base64>" to prevent cross-protocol signature reuse
      const signature = cryptoMod.signString('AUTH_CHALLENGE:' + msg.challenge, profile.signingSecretKey);
      conn.send(MSG.AUTH_RESPONSE, { signature, deviceId: profile.deviceId });
    });

    // Buffer pending messages that arrive with AUTH_OK
    conn._pendingBuffer = [];
    conn.on(MSG.PENDING_MESSAGES, (msg) => {
      if (msg.messages) conn._pendingBuffer.push(...msg.messages);
    });

    conn.on(MSG.AUTH_OK, (msg) => {
      clearTimeout(timeout);
      // TOFU pin server signing key (with format validation)
      if (msg.serverSigningKey) {
        let validKey = false;
        try {
          const skBytes = cryptoMod.decodeBase64(msg.serverSigningKey);
          if (skBytes.length === 32) validKey = true;
        } catch {}
        if (validKey) {
          if (!profile.serverSigningKey) {
            profile.serverSigningKey = msg.serverSigningKey;
            store.saveProfile(profile, passphrase);
          }
          conn.serverSigningKey = profile.serverSigningKey;
        } else {
          console.error('[Security] Invalid serverSigningKey format — ignoring');
        }
      }
      // Update deviceId if server assigned one
      if (msg.deviceId && !profile.deviceId) {
        profile.deviceId = msg.deviceId;
        store.saveProfile(profile, passphrase);
      }
      // Upload pre-keys if running low
      if (msg.prekeyCount !== undefined && msg.prekeyCount <= 5) {
        const bundle = store.generateAndStorePreKeys(profile, 20, passphrase);
        conn.send(MSG.UPLOAD_PREKEYS, {
          signedPreKey: bundle.signedPreKey,
          signedPreKeySig: bundle.signedPreKeySig,
          oneTimePreKeys: bundle.oneTimePreKeys,
        });
      }
      // Store delivery token for sealed sender
      if (msg.deliveryToken) {
        profile._deliveryToken = msg.deliveryToken;
      }
      spinner.stop('CONNECTED');
      resolve(conn);
    });

    conn.on(MSG.AUTH_FAIL, (msg) => {
      clearTimeout(timeout);
      spinner.fail(msg.error);
      conn.disconnect();
      reject(new Error(`Auth failed: ${msg.error}`));
    });

    conn.on(MSG.ERROR, (msg) => {
      console.error(`Server error: ${msg.error}`);
    });

    conn.connect()
      .then(() => {
        conn.send(MSG.AUTH, { userId: profile.userId, deviceId: profile.deviceId });
      })
      .catch((err) => {
        clearTimeout(timeout);
        spinner.fail(err.message);
        reject(err);
      });
  });
}

// --- Commands ---
async function cmdRegister() {
  if (!opts.invite) {
    console.error('Error: --invite / -i is required for registration');
    process.exit(1);
  }

  let passphrase = await askPassphrase('Create passphrase (min 12 chars): ', true);
  if (passphrase.length < 12) {
    console.error('Passphrase must be at least 12 characters.');
    process.exit(1);
  }
  const strength = cryptoMod.estimatePassphraseStrength(passphrase);
  if (strength < 40) {
    console.error(`Passphrase too weak (score: ${strength}/100). Use a mix of upper/lowercase, numbers, and symbols.`);
    process.exit(1);
  }
  const confirm = await askPassphrase('Confirm passphrase: ');
  delete process.env.REDE_PASS; // Clear from env after all passphrase prompts
  if (passphrase !== confirm) {
    console.error('Passphrases do not match.');
    process.exit(1);
  }

  // Create temp profile with displayName — internalId comes from server
  const displayName = opts.user;
  const tempProfile = store.createProfile('pending', displayName, passphrase);

  const regConnOpts = { useTor: opts.tor, useI2P: opts.i2p };
  if (opts.torProxy) regConnOpts.torProxy = opts.torProxy;
  if (opts.i2pProxy) regConnOpts.i2pProxy = opts.i2pProxy;
  const conn = new RedeConnection(serverUrl, regConnOpts);
  conn.shouldReconnect = false; // No auto-reconnect during registration

  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => { conn.disconnect(); reject(new Error('Timeout')); }, opts.i2p ? 120000 : 30000);

    conn.on(MSG.REGISTER_OK, (msg) => {
      clearTimeout(timeout);
      // Delete temp "pending" profile, save under real ID
      const fs = require('fs');
      const pendingHash = crypto.createHash('sha256').update('pending').digest('hex');
      const redeDir = require('path').join(require('os').homedir(), '.rede');
      try { fs.unlinkSync(require('path').join(redeDir, pendingHash + '.enc')); } catch {}
      try { fs.unlinkSync(require('path').join(redeDir, pendingHash.slice(0, 16) + '.enc')); } catch {}

      tempProfile.userId = msg.userId;
      tempProfile.displayName = msg.displayName;
      if (msg.deviceId) tempProfile.deviceId = msg.deviceId;
      if (msg.serverSigningKey) {
        try {
          const skBytes = cryptoMod.decodeBase64(msg.serverSigningKey);
          if (skBytes.length !== 32) throw new Error('Bad length');
          tempProfile.serverSigningKey = msg.serverSigningKey;
          conn.serverSigningKey = msg.serverSigningKey;
        } catch {
          console.error('[Security] Invalid serverSigningKey format in REGISTER_OK — ignoring');
        }
      }
      store.saveProfile(tempProfile, passphrase);

      // Upload initial pre-key bundle
      const bundle = store.generateAndStorePreKeys(tempProfile, 20, passphrase);
      conn.send(MSG.UPLOAD_PREKEYS, {
        signedPreKey: bundle.signedPreKey,
        signedPreKeySig: bundle.signedPreKeySig,
        oneTimePreKeys: bundle.oneTimePreKeys,
      });

      console.log(`Registered as: ${msg.displayName}`);
      console.log(`Your ID: ${msg.userId}`);
      console.log(`Fingerprint: ${cryptoMod.fingerprint(tempProfile.publicKey)}`);
      console.log(`Remember your ID for login: ${msg.userId}`);
      // Small delay for pre-key upload to complete
      setTimeout(() => { conn.disconnect(); resolve(); }, 500);
    });

    conn.on(MSG.REGISTER_FAIL, (msg) => {
      clearTimeout(timeout);
      console.error(`Registration failed: ${msg.error}`);
      conn.disconnect();
      reject(new Error(msg.error));
    });

    conn.connect()
      .then(() => {
        const proof = cryptoMod.signString(
          displayName + tempProfile.publicKey,
          tempProfile.signingSecretKey
        );
        conn.send(MSG.REGISTER, {
          inviteCode: opts.invite,
          publicKey: tempProfile.publicKey,
          signingKey: tempProfile.signingKey,
          displayName,
          deviceId: tempProfile.deviceId,
          proof,
        });
      })
      .catch(reject);
  });
}

async function cmdSend() {
  if (!opts.to && !opts.group) {
    console.error('Error: --to or --group is required');
    process.exit(1);
  }
  if (!opts.message) {
    console.error('Error: message text is required');
    process.exit(1);
  }

  const passphrase = await askPassphrase('Passphrase: ');
  const profile = store.loadProfile(opts.user, passphrase);
  if (!profile) { console.error('Wrong passphrase or no profile.'); process.exit(1); }

  const conn = await connectAndAuth(profile, passphrase);

  // Sealed sender helper
  function trySealedSend(targetId, devId, innerPayload, ttl) {
    const contact = profile.contacts[targetId];
    if (!contact || !profile._deliveryToken) return false;
    let recipPubKey = null;
    if (devId && contact.devices && contact.devices[devId]) {
      recipPubKey = contact.devices[devId].publicKey;
    } else {
      recipPubKey = contact.publicKey;
    }
    if (!recipPubKey) return false;
    const inner = { from: profile.userId, fromDeviceId: profile.deviceId, ...innerPayload };
    const sealed = cryptoMod.sealMessage(JSON.stringify(inner), recipPubKey);
    return conn.send(MSG.SEALED_MESSAGE, { to: targetId, toDeviceId: devId, sealedPayload: sealed, deliveryToken: profile._deliveryToken, ttl });
  }

  if (opts.to) {
    // 1:1 message (ratcheted, multi-device)
    const contact = profile.contacts[opts.to];
    if (!contact) {
      console.error(`Contact "${opts.to}" not found. Use: add -u ${opts.user} -t ${opts.to}`);
      conn.disconnect();
      process.exit(1);
    }

    let ackCount = 0;
    let expectedAcks = 0;
    conn.on(MSG.MESSAGE_ACK, (msg) => {
      ackCount++;
      if (ackCount >= expectedAcks) {
        const status = msg.queued ? '(queued - recipient offline)' : '(delivered)';
        console.log(`Sent to ${opts.to} ${status}`);
        store.addChatMessage(profile, opts.to, {
          from: profile.userId, text: opts.message, ts: Date.now(), ttl: opts.ttl || 0,
        }, passphrase);
        conn.disconnect();
      }
    });

    const devices = contact.devices || {};
    const deviceIds = Object.keys(devices);
    const needBundle = [];
    let sentImmediate = false;

    // Send to devices with existing sessions
    for (const devId of deviceIds) {
      const ratchetState = store.loadRatchetState(profile, opts.to, devId);
      if (ratchetState) {
        const result = cryptoMod.ratchetEncrypt(ratchetState, opts.message);
        store.saveRatchetState(profile, opts.to, ratchetState, passphrase, devId);
        const msgPayload = { encrypted: result.ciphertext, nonce: result.nonce, header: result.header };
        if (!trySealedSend(opts.to, devId, msgPayload, opts.ttl || 0)) {
          conn.send(MSG.MESSAGE, { to: opts.to, toDeviceId: devId, ...msgPayload, ttl: opts.ttl || 0 });
        }
        expectedAcks++;
        sentImmediate = true;
      } else {
        needBundle.push(devId);
      }
    }

    // Legacy: check for session without device ID
    if (deviceIds.length === 0) {
      const ratchetState = store.loadRatchetState(profile, opts.to);
      if (ratchetState) {
        const result = cryptoMod.ratchetEncrypt(ratchetState, opts.message);
        store.saveRatchetState(profile, opts.to, ratchetState, passphrase);
        const msgPayload = { encrypted: result.ciphertext, nonce: result.nonce, header: result.header };
        if (!trySealedSend(opts.to, null, msgPayload, opts.ttl || 0)) {
          conn.send(MSG.MESSAGE, { to: opts.to, ...msgPayload, ttl: opts.ttl || 0 });
        }
        expectedAcks++;
        sentImmediate = true;
      } else {
        needBundle.push(null);
      }
    }

    // Fetch pre-key bundles for devices without sessions
    if (needBundle.length > 0) {
      conn.on(MSG.PREKEY_BUNDLE, (msg) => {
        const deviceBundles = msg.devices || [{
          deviceId: null,
          identityKey: msg.identityKey,
          signingKey: msg.signingKey,
          signedPreKey: msg.signedPreKey,
          signedPreKeySig: msg.signedPreKeySig,
          oneTimePreKey: msg.oneTimePreKey,
        }];

        for (const bundle of deviceBundles) {
          const devId = bundle.deviceId;
          if (store.loadRatchetState(profile, opts.to, devId)) continue;

          // Verify identity key
          let keyValid = false;
          if (contact.devices) {
            const dev = contact.devices[devId] || contact.devices.primary;
            if (dev && dev.publicKey === bundle.identityKey) keyValid = true;
          }
          if (!keyValid && contact.publicKey === bundle.identityKey) keyValid = true;
          if (!keyValid && devId && bundle.identityKey && bundle.signingKey && bundle.signedPreKey && bundle.signedPreKeySig) {
            // Verify signed pre-key signature before accepting new device
            try {
              const naclUtil = require('tweetnacl-util');
              const nacl = require('tweetnacl');
              const sigBytes = naclUtil.decodeBase64(bundle.signedPreKeySig);
              const preKeyBytes = naclUtil.decodeBase64(bundle.signedPreKey);
              const sigKeyBytes = naclUtil.decodeBase64(bundle.signingKey);
              if (sigBytes.length === 64 && preKeyBytes.length === 32 && sigKeyBytes.length === 32 &&
                  nacl.sign.detached.verify(preKeyBytes, sigBytes, sigKeyBytes)) {
                if (!contact.devices) contact.devices = {};
                contact.devices[devId] = { publicKey: bundle.identityKey, signingKey: bundle.signingKey };
                store.saveProfile(profile, passphrase);
                keyValid = true;
                console.log(`[SECURITY] New device ${devId} for ${opts.to} accepted (pre-key sig verified). Verify fingerprint out-of-band!`);
              }
            } catch {
              // Signature verification failed
            }
          }

          if (!keyValid) {
            console.error(`[SECURITY] Identity key mismatch for ${opts.to} device ${devId || 'primary'}! Skipped.`);
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
            console.error(`X3DH failed for device ${devId || 'primary'}.`);
            continue;
          }
          const newState = cryptoMod.ratchetInitSender(x3dhResult.sharedSecret, bundle.signedPreKey);
          const result = cryptoMod.ratchetEncrypt(newState, opts.message);
          store.saveRatchetState(profile, opts.to, newState, passphrase, devId);
          conn.send(MSG.MESSAGE, {
            to: opts.to,
            toDeviceId: devId,
            encrypted: result.ciphertext,
            nonce: result.nonce,
            header: result.header,
            x3dh: {
              identityKey: profile.publicKey,
              ephemeralKey: x3dhResult.ephemeralPublic,
              usedOTPKPub: bundle.oneTimePreKey || null,
            },
            ttl: opts.ttl || 0,
          });
          expectedAcks++;
        }
        if (expectedAcks === 0) {
          console.error('Failed to establish session with any device.');
          conn.disconnect();
          process.exit(1);
        }
      });
      conn.on(MSG.PREKEY_BUNDLE_FAIL, (msg) => {
        console.error(`Could not fetch pre-key bundle: ${msg.error}`);
        conn.disconnect();
        process.exit(1);
      });
      conn.send(MSG.FETCH_PREKEY_BUNDLE, { targetUserId: opts.to });
    }

  } else if (opts.group) {
    // Group message (Sender Keys)
    const group = profile.groups[opts.group];
    if (!group) {
      console.error(`Group "${opts.group}" not found.`);
      conn.disconnect();
      process.exit(1);
    }

    let skState = store.loadSenderKeyState(profile, opts.group);
    if (!skState || !skState.own) {
      // Generate new sender key
      const newKey = cryptoMod.generateSenderKey();
      if (!skState) skState = { own: null, members: {} };
      skState.own = newKey;
      store.saveSenderKeyState(profile, opts.group, skState, passphrase);
      // Note: sender key distribution via CLI would need ratcheted DMs to each member
      // For now, distribute to known contacts
      for (const memberId of (group.members || [])) {
        if (memberId === profile.userId) continue;
        if (!profile.contacts[memberId]) continue;
        // Best-effort distribution (will use ratchet if available, legacy otherwise)
        const skSigPayload = `SENDERKEY:${opts.group}:${newKey.chainKey}:${newKey.messageNumber}`;
        const skSig = cryptoMod.signString(skSigPayload, profile.signingSecretKey);
        const keyMsg = JSON.stringify({ __rede_ctrl: 'senderkey', groupId: opts.group, chainKey: newKey.chainKey, messageNumber: newKey.messageNumber, sig: skSig });
        const rState = store.loadRatchetState(profile, memberId);
        if (rState) {
          const enc = cryptoMod.ratchetEncrypt(rState, keyMsg);
          store.saveRatchetState(profile, memberId, rState, passphrase);
          conn.send(MSG.MESSAGE, { to: memberId, encrypted: enc.ciphertext, nonce: enc.nonce, header: enc.header, ttl: 120 });
        }
      }
    }

    const result = cryptoMod.senderKeyEncrypt(skState.own, opts.message, profile.signingSecretKey);
    store.saveSenderKeyState(profile, opts.group, skState, passphrase);

    conn.send(MSG.GROUP_MESSAGE, {
      groupId: opts.group,
      encrypted: result.ciphertext,
      nonce: result.nonce,
      senderKeyHeader: { messageNumber: result.messageNumber, signature: result.signature },
      ttl: opts.ttl || 0,
    });

    store.addChatMessage(profile, opts.group, {
      from: profile.userId, text: opts.message, ts: Date.now(), ttl: opts.ttl || 0,
    }, passphrase);

    // Small delay to ensure delivery
    setTimeout(() => {
      console.log(`Sent to group "${group.name}"`);
      conn.disconnect();
    }, 500);
  }
}

async function cmdRead() {
  if (!opts.to && !opts.group) {
    console.error('Error: --to or --group is required');
    process.exit(1);
  }

  const passphrase = await askPassphrase('Passphrase: ');
  const profile = store.loadProfile(opts.user, passphrase);
  if (!profile) { console.error('Wrong passphrase or no profile.'); process.exit(1); }

  store.cleanupExpiredMessages(profile, passphrase);

  const chatId = opts.to || opts.group;
  const history = profile.chatHistory[chatId] || [];

  if (history.length === 0) {
    console.log('No messages.');
    process.exit(0);
  }

  const label = opts.group ? `# ${profile.groups[opts.group]?.name || opts.group}` : opts.to;
  console.log(`--- ${label} ---`);

  for (const msg of history.slice(-50)) {
    const time = new Date(msg.ts).toLocaleTimeString();
    const date = new Date(msg.ts).toLocaleDateString();
    const sender = msg.from === profile.userId ? 'You' : (profile.contacts[msg.from]?.alias || msg.from);
    const ttl = msg.ttl > 0 ? ` [${msg.ttl}s]` : '';
    console.log(`[${date} ${time}] ${sender}: ${msg.text}${ttl}`);
  }
}

async function cmdContacts() {
  const passphrase = await askPassphrase('Passphrase: ');
  const profile = store.loadProfile(opts.user, passphrase);
  if (!profile) { console.error('Wrong passphrase or no profile.'); process.exit(1); }

  const contacts = Object.entries(profile.contacts);
  if (contacts.length === 0) {
    console.log('No contacts.');
    process.exit(0);
  }

  console.log('Contacts:');
  for (const [id, c] of contacts) {
    const fp = cryptoMod.fingerprint(c.publicKey);
    console.log(`  ${c.alias || id}  ${fp}`);
  }
}

async function cmdGroups() {
  const passphrase = await askPassphrase('Passphrase: ');
  const profile = store.loadProfile(opts.user, passphrase);
  if (!profile) { console.error('Wrong passphrase or no profile.'); process.exit(1); }

  const groups = Object.entries(profile.groups);
  if (groups.length === 0) {
    console.log('No groups.');
    process.exit(0);
  }

  console.log('Groups:');
  for (const [id, g] of groups) {
    console.log(`  # ${g.name}  (${id})  ${g.members?.length || '?'} members`);
  }
}

async function cmdAdd() {
  if (!opts.to) {
    console.error('Error: --to is required');
    process.exit(1);
  }

  const passphrase = await askPassphrase('Passphrase: ');
  const profile = store.loadProfile(opts.user, passphrase);
  if (!profile) { console.error('Wrong passphrase or no profile.'); process.exit(1); }

  const conn = await connectAndAuth(profile, passphrase);

  conn.on(MSG.USER_LOOKUP_OK, (msg) => {
    const devices = msg.devices || null;
    const result = store.addContact(profile, msg.userId, msg.publicKey, msg.signingKey, msg.displayName || msg.userId, passphrase, devices);
    if (result.warning) {
      console.log(`WARNING: Key changed for ${msg.userId}!`);
      console.log(`  Old: ${result.oldFingerprint}`);
      console.log(`  New: ${result.newFingerprint}`);
    } else {
      const devCount = devices ? Object.keys(devices).length : 1;
      console.log(`Contact added: ${msg.displayName || msg.userId} (${msg.userId}) [${devCount} device(s)]`);
      console.log(`Fingerprint: ${cryptoMod.fingerprint(msg.publicKey)}`);
      console.log('Verify this fingerprint out-of-band!');
    }
    conn.disconnect();
  });

  conn.on(MSG.USER_LOOKUP_FAIL, (msg) => {
    console.error(`User not found: ${msg.lookupId}`);
    conn.disconnect();
    process.exit(1);
  });

  conn.send(MSG.USER_LOOKUP, { lookupId: opts.to });
}

async function cmdGroupNew() {
  const name = opts.message || opts.group || 'Unnamed';

  const passphrase = await askPassphrase('Passphrase: ');
  const profile = store.loadProfile(opts.user, passphrase);
  if (!profile) { console.error('Wrong passphrase or no profile.'); process.exit(1); }

  const conn = await connectAndAuth(profile, passphrase);

  conn.on(MSG.GROUP_CREATE_OK, (msg) => {
    const groupKey = cryptoMod.generateGroupKey();
    store.addGroup(profile, msg.groupId, msg.name, groupKey, [profile.userId], passphrase);
    console.log(`Group created: "${msg.name}"`);
    console.log(`Group ID: ${msg.groupId}`);
    conn.disconnect();
  });

  conn.send(MSG.GROUP_CREATE, { name });
}

async function cmdGinvite() {
  if (!opts.group || !opts.to) {
    console.error('Error: --group and --to are required');
    process.exit(1);
  }

  const passphrase = await askPassphrase('Passphrase: ');
  const profile = store.loadProfile(opts.user, passphrase);
  if (!profile) { console.error('Wrong passphrase or no profile.'); process.exit(1); }

  const group = profile.groups[opts.group];
  if (!group) { console.error('Group not found.'); process.exit(1); }

  const contact = profile.contacts[opts.to];
  if (!contact) { console.error('Add contact first.'); process.exit(1); }

  const conn = await connectAndAuth(profile, passphrase);

  conn.send(MSG.GROUP_INVITE, { groupId: opts.group, inviteeId: opts.to });

  // Send group key via ratcheted DM (signed)
  const sig = cryptoMod.signGroupKey(opts.group, group.name, group.key, profile.signingSecretKey);
  const keyMsg = JSON.stringify({ __rede_ctrl: 'groupkey', groupId: opts.group, name: group.name, key: group.key, sig });

  // Send to all devices with existing sessions
  const devices = contact.devices || {};
  const deviceIds = Object.keys(devices);
  let sentAny = false;
  const needBundle = [];

  for (const devId of deviceIds) {
    const ratchetState = store.loadRatchetState(profile, opts.to, devId);
    if (ratchetState) {
      const enc = cryptoMod.ratchetEncrypt(ratchetState, keyMsg);
      store.saveRatchetState(profile, opts.to, ratchetState, passphrase, devId);
      conn.send(MSG.MESSAGE, { to: opts.to, toDeviceId: devId, encrypted: enc.ciphertext, nonce: enc.nonce, header: enc.header, ttl: 60 });
      sentAny = true;
    } else {
      needBundle.push(devId);
    }
  }

  // Legacy check
  if (deviceIds.length === 0) {
    const ratchetState = store.loadRatchetState(profile, opts.to);
    if (ratchetState) {
      const enc = cryptoMod.ratchetEncrypt(ratchetState, keyMsg);
      store.saveRatchetState(profile, opts.to, ratchetState, passphrase);
      conn.send(MSG.MESSAGE, { to: opts.to, encrypted: enc.ciphertext, nonce: enc.nonce, header: enc.header, ttl: 60 });
      sentAny = true;
    } else {
      needBundle.push(null);
    }
  }

  if (needBundle.length > 0) {
    conn.on(MSG.PREKEY_BUNDLE, (msg) => {
      const deviceBundles = msg.devices || [{
        deviceId: null, identityKey: msg.identityKey, signingKey: msg.signingKey,
        signedPreKey: msg.signedPreKey, signedPreKeySig: msg.signedPreKeySig, oneTimePreKey: msg.oneTimePreKey,
      }];
      for (const bundle of deviceBundles) {
        const devId = bundle.deviceId;
        if (store.loadRatchetState(profile, opts.to, devId)) continue;
        let keyValid = false;
        if (contact.devices) {
          const dev = contact.devices[devId] || contact.devices.primary;
          if (dev && dev.publicKey === bundle.identityKey) keyValid = true;
        }
        if (!keyValid && contact.publicKey === bundle.identityKey) keyValid = true;
        if (!keyValid) continue;
        const x3dhResult = cryptoMod.x3dhInitiate(profile.secretKey, {
          identityKey: bundle.identityKey, signingKey: bundle.signingKey,
          signedPreKey: bundle.signedPreKey, signedPreKeySig: bundle.signedPreKeySig,
          oneTimePreKey: bundle.oneTimePreKey ? { id: 0, key: bundle.oneTimePreKey } : null,
        });
        if (x3dhResult) {
          const newState = cryptoMod.ratchetInitSender(x3dhResult.sharedSecret, bundle.signedPreKey);
          const enc = cryptoMod.ratchetEncrypt(newState, keyMsg);
          store.saveRatchetState(profile, opts.to, newState, passphrase, devId);
          conn.send(MSG.MESSAGE, {
            to: opts.to, toDeviceId: devId, encrypted: enc.ciphertext, nonce: enc.nonce, header: enc.header,
            x3dh: { identityKey: profile.publicKey, ephemeralKey: x3dhResult.ephemeralPublic, usedOTPKPub: bundle.oneTimePreKey || null },
            ttl: 60,
          });
        }
      }
      console.log(`Invited ${opts.to} to "${group.name}" (key sent to devices)`);
      setTimeout(() => conn.disconnect(), 500);
    });
    conn.on(MSG.PREKEY_BUNDLE_FAIL, () => {
      console.error('Could not fetch pre-key bundle for invitee.');
      conn.disconnect();
    });
    conn.send(MSG.FETCH_PREKEY_BUNDLE, { targetUserId: opts.to });
    return; // Don't disconnect yet
  }

  console.log(`Invited ${opts.to} to "${group.name}" (ratcheted key sent, expires 60s)`);
  setTimeout(() => conn.disconnect(), 1000);
}

async function cmdKey() {
  const passphrase = await askPassphrase('Passphrase: ');
  const profile = store.loadProfile(opts.user, passphrase);
  if (!profile) { console.error('Wrong passphrase or no profile.'); process.exit(1); }

  console.log(`User: ${profile.userId}`);
  console.log(`Device: ${profile.deviceId || 'primary'}`);
  console.log(`Public key: ${profile.publicKey}`);
  console.log(`Signing key: ${profile.signingKey}`);
  console.log(`Fingerprint: ${cryptoMod.fingerprint(profile.publicKey)}`);
}

async function cmdListen() {
  const passphrase = await askPassphrase('Passphrase: ');
  const profile = store.loadProfile(opts.user, passphrase);
  if (!profile) { console.error('Wrong passphrase or no profile.'); process.exit(1); }

  // Buffer to collect messages that arrive during auth
  const pendingBuffer = [];
  let ready = false;

  function handleDM(msg) {
    // Client-side nonce deduplication
    if (msg.nonce && !cryptoMod.checkClientNonce(msg.nonce)) return;

    const contact = profile.contacts[msg.from];
    if (!contact) {
      console.log(`[${new Date().toLocaleTimeString()}] <unknown:${msg.from}> (use: add -t ${msg.from})`);
      return;
    }

    const fromDeviceId = msg.fromDeviceId || null;
    let plaintext = null;

    // Verify identity key against known device keys
    function verifyIdentityKey(identityKey) {
      if (contact.devices) {
        for (const dev of Object.values(contact.devices)) {
          if (dev.publicKey === identityKey) return true;
        }
      }
      return contact.publicKey === identityKey;
    }

    // Try ratcheted decrypt (X3DH + Double Ratchet)
    // Always process X3DH even if we have an existing state — the sender may have
    // reset their session (e.g. first message was never delivered/processed)
    if (msg.x3dh) {
      // Verify X3DH identity key matches known contact device key
      if (!verifyIdentityKey(msg.x3dh.identityKey)) {
        if (fromDeviceId) {
          // Unknown device — refresh device list and retry
          console.log(`[System] New device from ${msg.from} — refreshing device list...`);
          const prevHandler = conn.handlers.get(MSG.USER_LOOKUP_OK);
          conn.on(MSG.USER_LOOKUP_OK, (lookupMsg) => {
            // Restore previous handler
            if (prevHandler) conn.on(MSG.USER_LOOKUP_OK, prevHandler);
            else conn.handlers.delete(MSG.USER_LOOKUP_OK);
            if (lookupMsg.userId === msg.from && lookupMsg.devices) {
              const c = profile.contacts[msg.from];
              if (c) {
                // Validate device keys before accepting
                const validated = {};
                for (const [dId, dInfo] of Object.entries(lookupMsg.devices)) {
                  if (typeof dInfo !== 'object' || !dInfo.publicKey || !dInfo.signingKey) continue;
                  try {
                    const naclUtil = require('tweetnacl-util');
                    const pk = naclUtil.decodeBase64(dInfo.publicKey);
                    const sk = naclUtil.decodeBase64(dInfo.signingKey);
                    if (pk.length !== 32 || sk.length !== 32) continue;
                    validated[dId] = dInfo;
                  } catch { continue; }
                  const old = c.devices?.[dId];
                  if (old && (old.publicKey !== dInfo.publicKey || old.signingKey !== dInfo.signingKey)) {
                    console.log(`[SECURITY] Device key change for ${msg.from} device ${dId}. Verify out-of-band!`);
                  }
                }
                c.devices = validated;
                store.saveProfile(profile, passphrase);
                handleDM(msg);
              }
            }
          });
          conn.send(MSG.USER_LOOKUP, { lookupId: msg.from });
          return;
        }
        console.log(`[SECURITY] X3DH identity key mismatch from ${msg.from}! Message rejected.`);
        return;
      }
      // X3DH initial message
      const otpkUsed = !!msg.x3dh.usedOTPKPub;
      const otpkSecret = otpkUsed
        ? (() => { const k = store.consumeOneTimePreKey(profile, msg.x3dh.usedOTPKPub, passphrase); return k ? k.secretKey : null; })()
        : null;
      if (profile.signedPreKey) {
        const spkCandidates = [profile.signedPreKey];
        if (profile.previousSignedPreKeys) {
          for (const old of profile.previousSignedPreKeys) spkCandidates.push(old);
        }
        // OTP fallback: try with OTP first, then without (in case OTP was consumed/lost)
        const otpkAttempts = otpkUsed ? [otpkSecret, null] : [null];
        let x3dhDone = false;
        for (const otpk of otpkAttempts) {
          if (x3dhDone) break;
          for (const spk of spkCandidates) {
            const x3dhResult = cryptoMod.x3dhRespond(
              profile.secretKey, spk.secretKey, otpk,
              msg.x3dh.identityKey, msg.x3dh.ephemeralKey
            );
            if (!x3dhResult) continue;
            const ratchetState = cryptoMod.ratchetInitReceiver(x3dhResult.sharedSecret, {
              publicKey: spk.publicKey, secretKey: spk.secretKey,
            });
            plaintext = cryptoMod.ratchetDecrypt(ratchetState, msg.header, msg.encrypted, msg.nonce);
            if (plaintext !== null && plaintext !== undefined) {
              store.saveRatchetState(profile, msg.from, ratchetState, passphrase, fromDeviceId);
              x3dhDone = true;
              break;
            }
          }
        }
      }
    } else if (msg.header) {
      const ratchetState = store.loadRatchetState(profile, msg.from, fromDeviceId);
      if (ratchetState) {
        const stateBackup = JSON.parse(JSON.stringify(ratchetState));
        plaintext = cryptoMod.ratchetDecrypt(ratchetState, msg.header, msg.encrypted, msg.nonce);
        store.saveRatchetState(profile, msg.from, plaintext ? ratchetState : stateBackup, passphrase, fromDeviceId);
      }
    }

    // Legacy fallback removed — Double Ratchet required for all 1:1 messages
    if (!plaintext && !msg.header) {
      plaintext = null;
    }

    if (plaintext === null || plaintext === undefined) {
      console.log(`[${new Date().toLocaleTimeString()}] <${msg.from}> (decrypt failed)`);
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
          console.log(`[Security] REJECTED sender key from ${msg.from} — unknown group`);
          return;
        }
        if (!group.members || !group.members.includes(msg.from)) {
          console.log(`[Security] REJECTED sender key from ${msg.from} — not a group member`);
          return;
        }
        // Verify sender key signature
        if (!ctrl.sig || !contact.signingKey) {
          console.log(`[Security] REJECTED sender key from ${msg.from} — missing signature`);
          return;
        }
        const skPayload = `SENDERKEY:${ctrl.groupId}:${ctrl.chainKey}:${ctrl.messageNumber || 0}`;
        const skPayloadBytes = require('tweetnacl-util').decodeUTF8(skPayload);
        if (!cryptoMod.verifyBytes(skPayloadBytes, ctrl.sig, contact.signingKey)) {
          console.log(`[Security] REJECTED sender key from ${msg.from} — invalid signature!`);
          return;
        }
        // Validate chainKey format (must be 32-byte base64)
        try {
          const ckBytes = cryptoMod.decodeBase64(ctrl.chainKey);
          if (ckBytes.length !== 32) throw new Error('Bad length');
        } catch {
          console.log(`[Security] REJECTED sender key from ${msg.from} — invalid chainKey format`);
          return;
        }
        let skState = store.loadSenderKeyState(profile, ctrl.groupId) || { own: null, members: {} };
        skState.members[msg.from] = { chainKey: ctrl.chainKey, messageNumber: ctrl.messageNumber || 0 };
        store.saveSenderKeyState(profile, ctrl.groupId, skState, passphrase);
        console.log(`[System] Received sender key for group from ${msg.from}`);
      } else if (ctrl.__rede_ctrl === 'groupkey' && ctrl.groupId && ctrl.key) {
        if (!ctrl.sig || !contact.signingKey || !cryptoMod.verifyGroupKey(ctrl.groupId, ctrl.name, ctrl.key, ctrl.sig, contact.signingKey)) {
          console.log(`[Security] REJECTED group key from ${msg.from} — invalid signature!`);
          return;
        }
        store.addGroup(profile, ctrl.groupId, ctrl.name, ctrl.key, [], passphrase);
        console.log(`[System] Joined group "${ctrl.name}" (verified key)`);
      }
      return;
    }
    // Legacy compat: old-format control messages
    if (plaintext.startsWith('__SENDERKEY__:') || plaintext.startsWith('__GROUPKEY__:')) {
      return; // Reject legacy format for security
    }
    const ttl = msg.ttl > 0 ? ` [${msg.ttl}s]` : '';
    console.log(`[${new Date().toLocaleTimeString()}] ${contact.alias || msg.from}: ${plaintext}${ttl}`);
    store.addChatMessage(profile, msg.from, {
      from: msg.from, text: plaintext, ts: Date.now(), ttl: msg.ttl || 0,
    }, passphrase);
  }

  function handleGM(msg) {
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
          contact.signingKey
        );
        if (plaintext) {
          store.saveSenderKeyState(profile, msg.groupId, skState, passphrase);
        }
      }
    }

    // Legacy fallback removed — Sender Keys required for group messages

    if (!plaintext) return;
    const sender = profile.contacts[msg.from]?.alias || msg.from;
    const ttl = msg.ttl > 0 ? ` [${msg.ttl}s]` : '';
    console.log(`[${new Date().toLocaleTimeString()}] #${group.name} ${sender}: ${plaintext}${ttl}`);
    store.addChatMessage(profile, msg.groupId, {
      from: msg.from, text: plaintext, ts: Date.now(), ttl: msg.ttl || 0,
    }, passphrase);
  }

  const conn = await connectAndAuth(profile, passphrase);

  // Unseal sealed messages and forward to handleDM
  function handleSealed(msg) {
    if (!msg.sealedPayload) return;
    const inner = cryptoMod.unsealMessage(msg.sealedPayload, profile.secretKey);
    if (!inner) {
      console.log('[Security] Received sealed message but could not unseal.');
      return;
    }
    handleDM({ ...inner, ts: msg.ts });
  }

  // Register handlers
  conn.on(MSG.MESSAGE, handleDM);
  conn.on(MSG.SEALED_MESSAGE, handleSealed);
  conn.on(MSG.GROUP_MESSAGE, handleGM);
  conn.on(MSG.PENDING_MESSAGES, (msg) => {
    if (msg.messages) {
      for (const pm of msg.messages) {
        if (pm.type === MSG.SEALED_MESSAGE || pm.sealed) handleSealed(pm);
        else if (pm.type === MSG.GROUP_MESSAGE) handleGM(pm);
        else handleDM(pm);
      }
    }
  });

  // Drain buffered pending messages from auth phase
  if (conn._pendingBuffer && conn._pendingBuffer.length > 0) {
    console.log(`[${conn._pendingBuffer.length} pending message(s)]`);
    for (const pm of conn._pendingBuffer) {
      if (pm.type === MSG.GROUP_MESSAGE) handleGM(pm);
      else handleDM(pm);
    }
    conn._pendingBuffer = [];
  }

  console.log('Listening for messages... (Ctrl+C to quit)');
  process.on('SIGINT', () => { conn.disconnect(); process.exit(0); });
}

async function cmdLink() {
  if (!opts.link) {
    console.error('Error: --link <code> is required');
    process.exit(1);
  }

  let passphrase = await askPassphrase('Create passphrase for this device (min 12 chars): ', true);
  if (passphrase.length < 12) {
    console.error('Passphrase must be at least 12 characters.');
    process.exit(1);
  }
  const confirm = await askPassphrase('Confirm passphrase: ');
  delete process.env.REDE_PASS;
  if (passphrase !== confirm) {
    console.error('Passphrases do not match.');
    process.exit(1);
  }

  // Create new profile with fresh keys for this device
  const tempProfile = store.createProfile('pending', opts.user, passphrase);

  const connOpts = { useTor: opts.tor, useI2P: opts.i2p };
  if (opts.torProxy) connOpts.torProxy = opts.torProxy;
  if (opts.i2pProxy) connOpts.i2pProxy = opts.i2pProxy;
  const conn = new RedeConnection(serverUrl, connOpts);
  conn.shouldReconnect = false;

  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => { conn.disconnect(); reject(new Error('Timeout')); }, opts.i2p ? 120000 : 30000);

    conn.on(MSG.DEVICE_LINK_OK, (msg) => {
      clearTimeout(timeout);
      // Clean up temp profile
      const fs = require('fs');
      const pendingHash = crypto.createHash('sha256').update('pending').digest('hex');
      const redeDir = require('path').join(require('os').homedir(), '.rede');
      try { fs.unlinkSync(require('path').join(redeDir, pendingHash + '.enc')); } catch {}
      try { fs.unlinkSync(require('path').join(redeDir, pendingHash.slice(0, 16) + '.enc')); } catch {}

      tempProfile.userId = msg.userId;
      tempProfile.displayName = msg.displayName;
      if (msg.deviceId) tempProfile.deviceId = msg.deviceId;
      if (msg.serverSigningKey) {
        try {
          const skBytes = cryptoMod.decodeBase64(msg.serverSigningKey);
          if (skBytes.length !== 32) throw new Error('Bad length');
          tempProfile.serverSigningKey = msg.serverSigningKey;
          conn.serverSigningKey = msg.serverSigningKey;
        } catch {
          console.error('[Security] Invalid serverSigningKey format in DEVICE_LINK_OK — ignoring');
        }
      }
      store.saveProfile(tempProfile, passphrase);

      // Upload pre-keys for this device
      const bundle = store.generateAndStorePreKeys(tempProfile, 20, passphrase);
      conn.send(MSG.UPLOAD_PREKEYS, {
        signedPreKey: bundle.signedPreKey,
        signedPreKeySig: bundle.signedPreKeySig,
        oneTimePreKeys: bundle.oneTimePreKeys,
      });

      console.log(`Device linked to: ${msg.displayName} (${msg.userId})`);
      console.log(`Device ID: ${msg.deviceId}`);
      console.log(`Login with: node client/cli.js <command> -u ${msg.userId}`);
      setTimeout(() => { conn.disconnect(); resolve(); }, 500);
    });

    conn.on(MSG.DEVICE_LINK_FAIL, (msg) => {
      clearTimeout(timeout);
      console.error(`Device link failed: ${msg.error}`);
      conn.disconnect();
      reject(new Error(msg.error));
    });

    conn.connect()
      .then(() => {
        const codeHash = crypto.createHash('sha256').update(opts.link).digest('hex');
        // Proof-of-possession: sign codeHash+publicKey with our signing key
        const linkProof = cryptoMod.signString(
          'DEVICE_LINK:' + codeHash + ':' + tempProfile.publicKey,
          tempProfile.signingSecretKey
        );
        conn.send(MSG.DEVICE_LINK_USE, {
          userId: opts.user,
          codeHash,
          publicKey: tempProfile.publicKey,
          signingKey: tempProfile.signingKey,
          deviceId: tempProfile.deviceId,
          proof: linkProof,
        });
      })
      .catch(reject);
  });
}

async function cmdGenLink() {
  const passphrase = await askPassphrase('Passphrase: ');
  const profile = store.loadProfile(opts.user, passphrase);
  if (!profile) { console.error('Wrong passphrase or no profile.'); process.exit(1); }

  const conn = await connectAndAuth(profile, passphrase);

  const linkCode = crypto.randomBytes(16).toString('hex');
  const codeHash = crypto.createHash('sha256').update(linkCode).digest('hex');

  conn.on(MSG.DEVICE_LINK_CREATE_OK, () => {
    console.log(`Device link code (expires in 5 min): ${linkCode}`);
    console.log(`On the new device, run:`);
    console.log(`  node client/cli.js link -u ${profile.userId} --link ${linkCode}`);
    conn.disconnect();
  });

  conn.send(MSG.DEVICE_LINK_CREATE, { codeHash });
}

// --- Main ---
const commands = {
  register: cmdRegister,
  link: cmdLink,
  'gen-link': cmdGenLink,
  send: cmdSend,
  read: cmdRead,
  contacts: cmdContacts,
  groups: cmdGroups,
  add: cmdAdd,
  'group-new': cmdGroupNew,
  ginvite: cmdGinvite,
  key: cmdKey,
  listen: cmdListen,
};

const fn = commands[command];
if (!fn) {
  console.error(`Unknown command: ${command}`);
  console.log(HELP);
  process.exit(1);
}

fn().catch((err) => {
  console.error(`Error: ${err.message}`);
  process.exit(1);
});
