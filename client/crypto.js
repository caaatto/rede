'use strict';

const nacl = require('tweetnacl');
const naclUtil = require('tweetnacl-util');
const crypto = require('crypto');

// ============================================================================
// MEMORY SAFETY
// ============================================================================

function zeroOut(arr) {
  if (arr instanceof Uint8Array || Buffer.isBuffer(arr)) {
    arr.fill(0);
  }
}

// ============================================================================
// HKDF-SHA256 (RFC 5869)
// Used by X3DH and Double Ratchet for key derivation
// ============================================================================

function hkdfExtract(salt, ikm) {
  return crypto.createHmac('sha256', salt).update(ikm).digest();
}

function hkdfExpand(prk, info, length) {
  let okm = Buffer.alloc(0);
  let t = Buffer.alloc(0);
  for (let i = 1; okm.length < length; i++) {
    const hmac = crypto.createHmac('sha256', prk);
    hmac.update(t);
    hmac.update(info);
    hmac.update(Buffer.from([i]));
    t = hmac.digest();
    okm = Buffer.concat([okm, t]);
  }
  return new Uint8Array(okm.slice(0, length));
}

function hkdf(ikm, salt, info, length) {
  const prk = hkdfExtract(salt, ikm);
  return hkdfExpand(prk, typeof info === 'string' ? Buffer.from(info) : info, length);
}

// Build identity-bound HKDF salt for X3DH (sorted for deterministic ordering)
function x3dhIdentitySalt(pubA, pubB) {
  // Sort public keys lexicographically so both sides produce the same salt
  const a = Buffer.from(pubA);
  const b = Buffer.from(pubB);
  const sorted = Buffer.compare(a, b) <= 0 ? Buffer.concat([a, b]) : Buffer.concat([b, a]);
  return crypto.createHash('sha256').update(Buffer.concat([Buffer.from('RedeX3DHSalt'), sorted])).digest();
}

// ============================================================================
// MESSAGE PADDING — Fixed-size buckets to prevent traffic analysis
// ============================================================================

const PAD_BUCKETS = [256, 1024, 4096, 16384];

function padMessage(plaintext) {
  const msgBytes = typeof plaintext === 'string' ? Buffer.from(plaintext, 'utf8') : plaintext;
  const needed = 2 + msgBytes.length; // 2-byte length prefix + content
  let bucket = PAD_BUCKETS[PAD_BUCKETS.length - 1];
  for (const b of PAD_BUCKETS) {
    if (needed <= b) { bucket = b; break; }
  }
  const padded = Buffer.alloc(bucket);
  padded.writeUInt16BE(msgBytes.length, 0);
  msgBytes.copy(padded, 2);
  // Fill remainder with random bytes
  if (bucket - needed > 0) {
    crypto.randomBytes(bucket - needed).copy(padded, needed);
  }
  return new Uint8Array(padded);
}

function unpadMessage(paddedBytes) {
  if (paddedBytes.length < 2) return null;
  const buf = Buffer.from(paddedBytes);
  const len = buf.readUInt16BE(0);
  if (len > buf.length - 2) return null;
  return buf.slice(2, 2 + len).toString('utf8');
}

// ============================================================================
// KEY GENERATION (unchanged)
// ============================================================================

function generateKeyPair() {
  const kp = nacl.box.keyPair();
  const result = {
    publicKey: naclUtil.encodeBase64(kp.publicKey),
    secretKey: naclUtil.encodeBase64(kp.secretKey),
  };
  zeroOut(kp.secretKey);
  return result;
}

function generateSigningKeyPair() {
  const kp = nacl.sign.keyPair();
  const result = {
    signingKey: naclUtil.encodeBase64(kp.publicKey),
    signingSecretKey: naclUtil.encodeBase64(kp.secretKey),
  };
  zeroOut(kp.secretKey);
  return result;
}

// ============================================================================
// SIGNATURES (Ed25519)
// ============================================================================

function sign(dataB64, signingSecretKeyB64) {
  const data = naclUtil.decodeBase64(dataB64);
  const sk = naclUtil.decodeBase64(signingSecretKeyB64);
  const sig = nacl.sign.detached(data, sk);
  zeroOut(sk);
  return naclUtil.encodeBase64(sig);
}

function signString(text, signingSecretKeyB64) {
  const data = naclUtil.decodeUTF8(text);
  const sk = naclUtil.decodeBase64(signingSecretKeyB64);
  const sig = nacl.sign.detached(data, sk);
  zeroOut(sk);
  return naclUtil.encodeBase64(sig);
}

function signBytes(data, signingSecretKeyB64) {
  const sk = naclUtil.decodeBase64(signingSecretKeyB64);
  const sig = nacl.sign.detached(data, sk);
  zeroOut(sk);
  return naclUtil.encodeBase64(sig);
}

function verify(dataB64, signatureB64, signingKeyB64) {
  try {
    const data = naclUtil.decodeBase64(dataB64);
    const sig = naclUtil.decodeBase64(signatureB64);
    const pk = naclUtil.decodeBase64(signingKeyB64);
    return nacl.sign.detached.verify(data, sig, pk);
  } catch {
    return false;
  }
}

function verifyBytes(data, signatureB64, signingKeyB64) {
  try {
    const sig = naclUtil.decodeBase64(signatureB64);
    const pk = naclUtil.decodeBase64(signingKeyB64);
    return nacl.sign.detached.verify(data, sig, pk);
  } catch {
    return false;
  }
}

function fingerprint(publicKeyB64) {
  const hash = crypto.createHash('sha256').update(publicKeyB64).digest('hex');
  return hash.match(/.{4}/g).slice(0, 8).join(' ');
}

// ============================================================================
// LEGACY ENCRYPTION (kept for backward compatibility / migration)
// ============================================================================

function encryptFor(plaintext, recipientPubKeyB64, senderSecretKeyB64) {
  const nonce = nacl.randomBytes(nacl.box.nonceLength);
  const messageBytes = naclUtil.decodeUTF8(plaintext);
  const recipientPub = naclUtil.decodeBase64(recipientPubKeyB64);
  const senderSecret = naclUtil.decodeBase64(senderSecretKeyB64);
  const encrypted = nacl.box(messageBytes, nonce, recipientPub, senderSecret);
  zeroOut(senderSecret);
  if (!encrypted) return null;
  return {
    encrypted: naclUtil.encodeBase64(encrypted),
    nonce: naclUtil.encodeBase64(nonce),
  };
}

function decryptFrom(encryptedB64, nonceB64, senderPubKeyB64, recipientSecretKeyB64) {
  try {
    const encrypted = naclUtil.decodeBase64(encryptedB64);
    const nonce = naclUtil.decodeBase64(nonceB64);
    const senderPub = naclUtil.decodeBase64(senderPubKeyB64);
    const recipientSecret = naclUtil.decodeBase64(recipientSecretKeyB64);
    const decrypted = nacl.box.open(encrypted, nonce, senderPub, recipientSecret);
    zeroOut(recipientSecret);
    if (!decrypted) return null;
    const result = naclUtil.encodeUTF8(decrypted);
    zeroOut(decrypted);
    return result;
  } catch {
    return null;
  }
}

function generateGroupKey() {
  const key = nacl.randomBytes(nacl.secretbox.keyLength);
  const result = naclUtil.encodeBase64(key);
  zeroOut(key);
  return result;
}

function encryptGroup(plaintext, groupKeyB64) {
  const nonce = nacl.randomBytes(nacl.secretbox.nonceLength);
  const messageBytes = naclUtil.decodeUTF8(plaintext);
  const key = naclUtil.decodeBase64(groupKeyB64);
  const encrypted = nacl.secretbox(messageBytes, nonce, key);
  zeroOut(key);
  if (!encrypted) return null;
  return {
    encrypted: naclUtil.encodeBase64(encrypted),
    nonce: naclUtil.encodeBase64(nonce),
  };
}

function decryptGroup(encryptedB64, nonceB64, groupKeyB64) {
  try {
    const encrypted = naclUtil.decodeBase64(encryptedB64);
    const nonce = naclUtil.decodeBase64(nonceB64);
    const key = naclUtil.decodeBase64(groupKeyB64);
    const decrypted = nacl.secretbox.open(encrypted, nonce, key);
    zeroOut(key);
    if (!decrypted) return null;
    const result = naclUtil.encodeUTF8(decrypted);
    zeroOut(decrypted);
    return result;
  } catch {
    return null;
  }
}

// ============================================================================
// X3DH — Extended Triple Diffie-Hellman Key Agreement
// ============================================================================

// Raw X25519 DH using nacl.scalarMult
function dh(secretKey, publicKey) {
  return nacl.scalarMult(secretKey, publicKey);
}

// Generate pre-key bundle for upload to server
function generatePreKeyBundle(signingSecretKeyB64) {
  // Signed pre-key (ephemeral X25519, signed by identity Ed25519)
  const spk = nacl.box.keyPair();
  const spkPub = naclUtil.encodeBase64(spk.publicKey);
  const spkSec = naclUtil.encodeBase64(spk.secretKey);
  const spkSig = signBytes(spk.publicKey, signingSecretKeyB64);
  zeroOut(spk.secretKey);

  // One-time pre-keys (20 X25519 keypairs)
  const otpks = [];
  const otpksPrivate = [];
  for (let i = 0; i < 20; i++) {
    const kp = nacl.box.keyPair();
    otpks.push({ id: i, key: naclUtil.encodeBase64(kp.publicKey) });
    otpksPrivate.push({ id: i, publicKey: naclUtil.encodeBase64(kp.publicKey), secretKey: naclUtil.encodeBase64(kp.secretKey) });
    zeroOut(kp.secretKey);
  }

  return {
    // Public parts (for server upload)
    publicBundle: {
      signedPreKey: spkPub,
      signedPreKeySig: spkSig,
      oneTimePreKeys: otpks,
    },
    // Private parts (for local storage in profile)
    privateKeys: {
      signedPreKey: { publicKey: spkPub, secretKey: spkSec },
      oneTimePreKeys: otpksPrivate,
    },
  };
}

// Initiator side of X3DH (Alice sending first message to Bob)
function x3dhInitiate(senderIdentitySecretB64, recipientBundle) {
  // recipientBundle: { identityKey, signedPreKey, signedPreKeySig, signingKey, oneTimePreKey? }

  // Verify signed pre-key signature
  const spkBytes = naclUtil.decodeBase64(recipientBundle.signedPreKey);
  if (!verifyBytes(spkBytes, recipientBundle.signedPreKeySig, recipientBundle.signingKey)) {
    return null; // Invalid signature — abort
  }

  const ikA = naclUtil.decodeBase64(senderIdentitySecretB64);
  const ikB = naclUtil.decodeBase64(recipientBundle.identityKey);
  const spkB = naclUtil.decodeBase64(recipientBundle.signedPreKey);

  // Derive sender's public identity key for salt binding
  const ikAPub = nacl.box.keyPair.fromSecretKey(ikA).publicKey;

  // Generate ephemeral keypair
  const ek = nacl.box.keyPair();

  // 4-way (or 3-way) DH
  const dh1 = dh(ikA, spkB);           // DH(IK_A, SPK_B)
  const dh2 = dh(ek.secretKey, ikB);   // DH(EK_A, IK_B)
  const dh3 = dh(ek.secretKey, spkB);  // DH(EK_A, SPK_B)

  let dhConcat;
  let usedOTPKId = null;

  if (recipientBundle.oneTimePreKey) {
    const opkB = naclUtil.decodeBase64(recipientBundle.oneTimePreKey.key);
    const dh4 = dh(ek.secretKey, opkB); // DH(EK_A, OPK_B)
    dhConcat = new Uint8Array(128);
    dhConcat.set(dh1, 0);
    dhConcat.set(dh2, 32);
    dhConcat.set(dh3, 64);
    dhConcat.set(dh4, 96);
    zeroOut(dh4);
    usedOTPKId = recipientBundle.oneTimePreKey.id;
  } else {
    dhConcat = new Uint8Array(96);
    dhConcat.set(dh1, 0);
    dhConcat.set(dh2, 32);
    dhConcat.set(dh3, 64);
  }

  // Bind HKDF salt to both participant identities (sorted for determinism)
  const x3dhSalt = x3dhIdentitySalt(ikAPub, ikB);
  const sharedSecret = hkdf(dhConcat, x3dhSalt, 'RedeX3DH', 32);

  zeroOut(dh1);
  zeroOut(dh2);
  zeroOut(dh3);
  zeroOut(dhConcat);
  zeroOut(ikA);

  const ephemeralPublic = naclUtil.encodeBase64(ek.publicKey);
  zeroOut(ek.secretKey);

  return { sharedSecret, ephemeralPublic, usedOTPKId };
}

// Responder side of X3DH (Bob receiving first message from Alice)
function x3dhRespond(recipientIdentitySecretB64, signedPreKeySecretB64, oneTimePreKeySecretB64, senderIdentityKeyB64, senderEphemeralKeyB64) {
  const ikB = naclUtil.decodeBase64(recipientIdentitySecretB64);
  const spkB = naclUtil.decodeBase64(signedPreKeySecretB64);
  const ikA = naclUtil.decodeBase64(senderIdentityKeyB64);
  const ekA = naclUtil.decodeBase64(senderEphemeralKeyB64);

  // Derive recipient's public identity key for salt binding
  const ikBPub = nacl.box.keyPair.fromSecretKey(ikB).publicKey;

  const dh1 = dh(spkB, ikA);   // DH(SPK_B, IK_A)
  const dh2 = dh(ikB, ekA);    // DH(IK_B, EK_A)
  const dh3 = dh(spkB, ekA);   // DH(SPK_B, EK_A)

  let dhConcat;

  if (oneTimePreKeySecretB64) {
    const opkB = naclUtil.decodeBase64(oneTimePreKeySecretB64);
    const dh4 = dh(opkB, ekA); // DH(OPK_B, EK_A)
    dhConcat = new Uint8Array(128);
    dhConcat.set(dh1, 0);
    dhConcat.set(dh2, 32);
    dhConcat.set(dh3, 64);
    dhConcat.set(dh4, 96);
    zeroOut(dh4);
    zeroOut(opkB);
  } else {
    dhConcat = new Uint8Array(96);
    dhConcat.set(dh1, 0);
    dhConcat.set(dh2, 32);
    dhConcat.set(dh3, 64);
  }

  // Bind HKDF salt to both participant identities (sorted for determinism)
  const x3dhSalt = x3dhIdentitySalt(ikA, ikBPub);
  const sharedSecret = hkdf(dhConcat, x3dhSalt, 'RedeX3DH', 32);

  zeroOut(dh1);
  zeroOut(dh2);
  zeroOut(dh3);
  zeroOut(dhConcat);
  zeroOut(ikB);
  zeroOut(spkB);

  return { sharedSecret };
}

// ============================================================================
// DOUBLE RATCHET — Per-message PFS for 1:1 conversations
// ============================================================================

const MAX_SKIP = 256;
const MAX_MKSKIPPED = 1000; // Max stored skipped message keys

// KDF for root chain: produces new root key + chain key
function kdfRK(rk, dhOut) {
  const derived = hkdf(dhOut, rk, 'RedeRatchet', 64);
  const newRK = derived.slice(0, 32);
  const chainKey = derived.slice(32, 64);
  return [newRK, chainKey];
}

// KDF for chain key: produces new chain key + message key
function kdfCK(ck) {
  const newCK = new Uint8Array(crypto.createHmac('sha256', ck).update(Buffer.from([0x02])).digest());
  const msgKey = new Uint8Array(crypto.createHmac('sha256', ck).update(Buffer.from([0x01])).digest());
  return [newCK, msgKey];
}

// Initialize ratchet as sender (after X3DH initiator)
function ratchetInitSender(sharedSecret, recipientDHPubB64) {
  // Generate our DH keypair for the ratchet
  const dhKP = nacl.box.keyPair();

  // Store shared secret as RK, CKs will be initialized on first encrypt
  const rkB64 = naclUtil.encodeBase64(sharedSecret);
  zeroOut(sharedSecret);

  return {
    DHs: { publicKey: naclUtil.encodeBase64(dhKP.publicKey), secretKey: naclUtil.encodeBase64(dhKP.secretKey) },
    DHr: recipientDHPubB64,
    RK: rkB64,
    CKs: null,  // Will be initialized on first encrypt
    CKr: null,
    Ns: 0,
    Nr: 0,
    PN: 0,
    MKSKIPPED: {},
  };
}

// Initialize ratchet as receiver (after X3DH responder)
function ratchetInitReceiver(sharedSecret, ourDHKeyPair) {
  return {
    DHs: { publicKey: ourDHKeyPair.publicKey, secretKey: ourDHKeyPair.secretKey },
    DHr: null,
    RK: naclUtil.encodeBase64(sharedSecret),
    CKs: null,
    CKr: null,
    Ns: 0,
    Nr: 0,
    PN: 0,
    MKSKIPPED: {},
  };
}

// Encrypt a message with the Double Ratchet
const MAX_RATCHET_MESSAGE_NUMBER = 1_000_000_000;
const MAX_SENDER_KEY_MESSAGE_NUMBER = 10_000;

function ratchetEncrypt(state, plaintext) {
  if (state.Ns >= MAX_RATCHET_MESSAGE_NUMBER) {
    throw new Error('Ratchet message counter exhausted — session reset required');
  }

  // If this is the first message and we have DHr (recipient's key), perform DH ratchet step
  if (!state.CKs && state.DHr) {
    const dhSec = naclUtil.decodeBase64(state.DHs.secretKey);
    const dhPub = naclUtil.decodeBase64(state.DHr);
    const rk = naclUtil.decodeBase64(state.RK);

    const dhOut = dh(dhSec, dhPub);
    const [newRK, cks] = kdfRK(rk, dhOut);

    zeroOut(dhOut);
    zeroOut(rk);

    state.RK = naclUtil.encodeBase64(newRK);
    state.CKs = naclUtil.encodeBase64(cks);

    zeroOut(newRK);
    zeroOut(cks);
    zeroOut(dhSec);
  }

  if (!state.CKs) {
    throw new Error('Sending chain not initialized — wait for first incoming message');
  }

  const ck = naclUtil.decodeBase64(state.CKs);
  const [newCK, msgKey] = kdfCK(ck);
  zeroOut(ck);
  state.CKs = naclUtil.encodeBase64(newCK);
  zeroOut(newCK);

  const nonce = nacl.randomBytes(nacl.secretbox.nonceLength);
  const paddedBytes = padMessage(plaintext);
  const ciphertext = nacl.secretbox(paddedBytes, nonce, msgKey);
  zeroOut(msgKey);
  zeroOut(paddedBytes);

  const header = {
    dh: state.DHs.publicKey,
    pn: state.PN,
    n: state.Ns,
  };

  state.Ns++;

  return {
    header,
    ciphertext: naclUtil.encodeBase64(ciphertext),
    nonce: naclUtil.encodeBase64(nonce),
  };
}

// Skip missed messages in a chain, caching their keys
function _skipMessageKeys(state, until) {
  if (!state.CKr) return;
  if (until - state.Nr > MAX_SKIP) {
    throw new Error('Too many skipped messages');
  }
  const dhKey = state.DHr || '';
  while (state.Nr < until) {
    const ck = naclUtil.decodeBase64(state.CKr);
    const [newCK, msgKey] = kdfCK(ck);
    zeroOut(ck);
    state.CKr = naclUtil.encodeBase64(newCK);
    zeroOut(newCK);
    state.MKSKIPPED[`${dhKey}:${state.Nr}`] = naclUtil.encodeBase64(msgKey);
    zeroOut(msgKey);
    state.Nr++;
  }
  // Evict oldest skipped keys if over limit
  const skippedKeys = Object.keys(state.MKSKIPPED);
  if (skippedKeys.length > MAX_MKSKIPPED) {
    const toRemove = skippedKeys.slice(0, skippedKeys.length - MAX_MKSKIPPED);
    for (const k of toRemove) delete state.MKSKIPPED[k];
  }
}

// Perform DH ratchet step (when receiving a new DH key)
function _dhRatchetStep(state, headerDH) {
  state.PN = state.Ns;
  state.Ns = 0;
  state.Nr = 0;
  state.DHr = headerDH;

  const dhSec = naclUtil.decodeBase64(state.DHs.secretKey);
  const dhPub = naclUtil.decodeBase64(headerDH);
  const rk = naclUtil.decodeBase64(state.RK);

  // Receiving chain
  const dhOut1 = dh(dhSec, dhPub);
  const [rk1, ckr] = kdfRK(rk, dhOut1);
  zeroOut(dhOut1);
  state.RK = naclUtil.encodeBase64(rk1);
  state.CKr = naclUtil.encodeBase64(ckr);
  zeroOut(rk1);
  zeroOut(ckr);

  // New DH keypair for sending
  const newDH = nacl.box.keyPair();
  state.DHs = {
    publicKey: naclUtil.encodeBase64(newDH.publicKey),
    secretKey: naclUtil.encodeBase64(newDH.secretKey),
  };

  // Sending chain
  const rk2raw = naclUtil.decodeBase64(state.RK);
  const dhOut2 = dh(newDH.secretKey, dhPub);
  const [rk2, cks] = kdfRK(rk2raw, dhOut2);
  zeroOut(dhOut2);
  zeroOut(newDH.secretKey);
  state.RK = naclUtil.encodeBase64(rk2);
  state.CKs = naclUtil.encodeBase64(cks);
  zeroOut(rk2);
  zeroOut(cks);
  zeroOut(dhSec);
  zeroOut(rk);
}

// Decrypt a message with the Double Ratchet
function ratchetDecrypt(state, header, ciphertextB64, nonceB64) {
  try {
    const ciphertext = naclUtil.decodeBase64(ciphertextB64);
    const nonce = naclUtil.decodeBase64(nonceB64);

    // Check skipped message keys first
    const skippedKey = `${header.dh}:${header.n}`;
    if (state.MKSKIPPED[skippedKey]) {
      const msgKey = naclUtil.decodeBase64(state.MKSKIPPED[skippedKey]);
      delete state.MKSKIPPED[skippedKey];
      const decrypted = nacl.secretbox.open(ciphertext, nonce, msgKey);
      zeroOut(msgKey);
      if (!decrypted) return null;
      const result = unpadMessage(decrypted) || naclUtil.encodeUTF8(decrypted);
      zeroOut(decrypted);
      return result;
    }

    // DH ratchet step if new DH key
    if (header.dh !== state.DHr) {
      if (state.CKr) {
        _skipMessageKeys(state, header.pn);
      }
      _dhRatchetStep(state, header.dh);
    }

    // Skip any missed messages in current chain
    _skipMessageKeys(state, header.n);

    // Derive message key
    const ck = naclUtil.decodeBase64(state.CKr);
    const [newCK, msgKey] = kdfCK(ck);
    zeroOut(ck);
    state.CKr = naclUtil.encodeBase64(newCK);
    zeroOut(newCK);
    state.Nr++;

    const decrypted = nacl.secretbox.open(ciphertext, nonce, msgKey);
    zeroOut(msgKey);
    if (!decrypted) return null;

    const result = unpadMessage(decrypted) || naclUtil.encodeUTF8(decrypted);
    zeroOut(decrypted);
    return result;
  } catch {
    return null;
  }
}

// ============================================================================
// SENDER KEYS — Per-sender symmetric ratchet for group PFS
// ============================================================================

function generateSenderKey() {
  const chainKey = nacl.randomBytes(32);
  const result = {
    chainKey: naclUtil.encodeBase64(chainKey),
    messageNumber: 0,
  };
  zeroOut(chainKey);
  return result;
}

// Build signature payload: ciphertext || uint32(messageNumber) || utf8(contextId).
// contextId binds the signature to a specific group/channel, preventing cross-group replay.
function buildSigData(ciphertext, messageNumber, contextId) {
  const ctxBytes = contextId ? naclUtil.decodeUTF8(contextId) : new Uint8Array(0);
  const sigData = new Uint8Array(ciphertext.length + 4 + ctxBytes.length);
  sigData.set(ciphertext, 0);
  new DataView(sigData.buffer, sigData.byteOffset).setUint32(ciphertext.length, messageNumber, false);
  if (ctxBytes.length > 0) sigData.set(ctxBytes, ciphertext.length + 4);
  return sigData;
}

function senderKeyEncrypt(state, plaintext, signingSecretKeyB64, contextId) {
  if (state.messageNumber >= MAX_SENDER_KEY_MESSAGE_NUMBER) {
    throw new Error('Sender key counter exhausted — rekey required');
  }

  const ck = naclUtil.decodeBase64(state.chainKey);
  const [newCK, msgKey] = kdfCK(ck);
  zeroOut(ck);
  state.chainKey = naclUtil.encodeBase64(newCK);
  zeroOut(newCK);

  const nonce = nacl.randomBytes(nacl.secretbox.nonceLength);
  const paddedBytes = padMessage(plaintext);
  const ciphertext = nacl.secretbox(paddedBytes, nonce, msgKey);
  zeroOut(msgKey);
  zeroOut(paddedBytes);

  // Sign ciphertext + messageNumber + contextId for authentication
  const sigData = buildSigData(ciphertext, state.messageNumber, contextId || '');
  const signature = signBytes(sigData, signingSecretKeyB64);

  const messageNumber = state.messageNumber;
  state.messageNumber++;

  return {
    ciphertext: naclUtil.encodeBase64(ciphertext),
    nonce: naclUtil.encodeBase64(nonce),
    messageNumber,
    signature,
  };
}

function senderKeyDecrypt(state, ciphertextB64, nonceB64, messageNumber, signature, signingKeyB64, contextId) {
  try {
    const ciphertext = naclUtil.decodeBase64(ciphertextB64);
    const nonce = naclUtil.decodeBase64(nonceB64);

    // Verify signature with contextId binding — legacy fallback (no contextId) removed.
    // All clients since v2.17.3 sign with contextId.
    const sigData = buildSigData(ciphertext, messageNumber, contextId || '');
    if (!verifyBytes(sigData, signature, signingKeyB64)) {
      return null; // Signature verification failed
    }

    // Advance chain to the correct message number
    if (messageNumber < state.messageNumber) {
      return null; // Old message (no backward ratchet)
    }

    // Prevent DoS via massive forward skip
    const MAX_SKIP = 1000;
    if (messageNumber - state.messageNumber > MAX_SKIP) {
      return null; // Skip too large — reject
    }

    let ck = naclUtil.decodeBase64(state.chainKey);
    let msgKey;

    // Skip forward to the right message number
    for (let i = state.messageNumber; i <= messageNumber; i++) {
      const [newCK, mk] = kdfCK(ck);
      zeroOut(ck);
      ck = newCK;
      if (i === messageNumber) {
        msgKey = mk;
      } else {
        zeroOut(mk);
      }
    }

    state.chainKey = naclUtil.encodeBase64(ck);
    zeroOut(ck);
    state.messageNumber = messageNumber + 1;

    const decrypted = nacl.secretbox.open(ciphertext, nonce, msgKey);
    zeroOut(msgKey);
    if (!decrypted) return null;

    const result = unpadMessage(decrypted) || naclUtil.encodeUTF8(decrypted);
    zeroOut(decrypted);
    return result;
  } catch {
    return null;
  }
}

// ============================================================================
// SCRYPT KEY DERIVATION — Hardened to N=2^20
// ============================================================================

const SCRYPT_N_CURRENT = 1048576; // 2^20
const SCRYPT_N_LEGACY = 16384;    // 2^14

function deriveKey(passphrase, salt, scryptN) {
  const N = scryptN || SCRYPT_N_CURRENT;
  // maxmem must accommodate N * r * 128 bytes + overhead
  const maxmem = N * 8 * 128 + 1024 * 1024;
  return crypto.scryptSync(passphrase, salt, 32, { N, r: 8, p: 1, maxmem });
}

// ============================================================================
// PROFILE ENCRYPTION (at rest, with HMAC integrity + scrypt migration)
// ============================================================================

function encryptProfile(data, passphrase) {
  const salt = crypto.randomBytes(16);
  const key = new Uint8Array(deriveKey(passphrase, salt, SCRYPT_N_CURRENT));
  const nonce = nacl.randomBytes(nacl.secretbox.nonceLength);
  const jsonStr = JSON.stringify(data);
  const plaintext = naclUtil.decodeUTF8(jsonStr);
  const encrypted = nacl.secretbox(plaintext, nonce, key);
  zeroOut(key);
  zeroOut(plaintext);

  const hmacKey = deriveKey(passphrase, Buffer.concat([salt, Buffer.from('hmac')]), SCRYPT_N_CURRENT);
  const hmac = crypto.createHmac('sha256', hmacKey).update(encrypted).digest('hex');
  hmacKey.fill(0);

  return {
    salt: naclUtil.encodeBase64(salt),
    nonce: naclUtil.encodeBase64(nonce),
    data: naclUtil.encodeBase64(encrypted),
    hmac,
    scryptN: SCRYPT_N_CURRENT,
  };
}

function decryptProfile(envelope, passphrase) {
  try {
    const salt = naclUtil.decodeBase64(envelope.salt);
    const encrypted = naclUtil.decodeBase64(envelope.data);
    // Determine scrypt N (legacy migration: if not set, try current then fallback)
    const scryptN = envelope.scryptN || SCRYPT_N_LEGACY;

    if (envelope.hmac) {
      const hmacKey = deriveKey(passphrase, Buffer.concat([salt, Buffer.from('hmac')]), scryptN);
      const expected = crypto.createHmac('sha256', hmacKey).update(encrypted).digest('hex');
      hmacKey.fill(0);
      if (!crypto.timingSafeEqual(Buffer.from(envelope.hmac), Buffer.from(expected))) {
        return null;
      }
    }

    const key = new Uint8Array(deriveKey(passphrase, salt, scryptN));
    const nonce = naclUtil.decodeBase64(envelope.nonce);
    const decrypted = nacl.secretbox.open(encrypted, nonce, key);
    zeroOut(key);
    if (!decrypted) return null;
    const result = JSON.parse(naclUtil.encodeUTF8(decrypted));
    zeroOut(decrypted);
    return result;
  } catch {
    return null;
  }
}

// ============================================================================
// CLIENT-SIDE NONCE DEDUPLICATION
// ============================================================================

const _seenNonces = new Map();
const NONCE_MAX_AGE = 3600000;
const NONCE_MAX_SIZE = 10000;

function checkClientNonce(nonceB64) {
  if (_seenNonces.size > NONCE_MAX_SIZE) {
    const now = Date.now();
    for (const [k, ts] of _seenNonces) {
      if (now - ts > NONCE_MAX_AGE) _seenNonces.delete(k);
    }
  }
  if (_seenNonces.has(nonceB64)) return false;
  _seenNonces.set(nonceB64, Date.now());
  return true;
}

// ============================================================================
// AUTHENTICATED GROUP KEY DISTRIBUTION (legacy, kept for backward compat)
// ============================================================================

function signGroupKey(groupId, groupName, groupKey, signingSecretKeyB64) {
  const payload = `GROUPKEY:${groupId}:${groupName}:${groupKey}`;
  return signString(payload, signingSecretKeyB64);
}

function verifyGroupKey(groupId, groupName, groupKey, signature, signingKeyB64) {
  const payload = `GROUPKEY:${groupId}:${groupName}:${groupKey}`;
  try {
    const data = naclUtil.decodeUTF8(payload);
    const sig = naclUtil.decodeBase64(signature);
    const pk = naclUtil.decodeBase64(signingKeyB64);
    return nacl.sign.detached.verify(data, sig, pk);
  } catch {
    return false;
  }
}

// ============================================================================
// PASSPHRASE ENTROPY ESTIMATION
// ============================================================================

function estimatePassphraseStrength(passphrase) {
  let score = 0;
  const len = passphrase.length;

  // Length scoring (entropy basis)
  if (len >= 20) score += 50;
  else if (len >= 16) score += 40;
  else if (len >= 12) score += 30;
  else score += len * 2;

  // Character class diversity
  const classes = [/[a-z]/, /[A-Z]/, /[0-9]/, /[^a-zA-Z0-9]/];
  const classCount = classes.filter(r => r.test(passphrase)).length;
  score += classCount * 10;

  // Unique character ratio (penalize low entropy)
  const unique = new Set(passphrase.toLowerCase()).size;
  if (unique >= 10) score += 15;
  else if (unique >= 6) score += 5;
  if (unique < len * 0.4) score -= 15;

  // Penalize common patterns
  const lower = passphrase.toLowerCase();
  const commonWords = ['password', 'passphrase', 'letmein', 'welcome', 'admin', 'master', 'dragon', 'monkey', 'shadow', 'sunshine'];
  for (const w of commonWords) {
    if (lower.includes(w)) { score -= 25; break; }
  }

  // Sequential patterns
  if (/(.)\1{3,}/.test(passphrase)) score -= 20;
  if (/1234|2345|3456|abcd|bcde|qwer|asdf/i.test(passphrase)) score -= 20;

  // All same class
  if (/^[a-z]+$/i.test(passphrase)) score -= 15;
  if (/^[0-9]+$/.test(passphrase)) score -= 30;

  // Keyboard walk patterns
  if (/qwerty|asdfgh|zxcvbn/i.test(passphrase)) score -= 25;

  return Math.max(0, Math.min(100, score));
}

// ============================================================================
// SERVER SIGNATURE VERIFICATION
// ============================================================================

function verifyServerSignature(msg, serverSigningKeyB64) {
  if (!msg.serverSig || !serverSigningKeyB64) return false;
  try {
    const body = { ...msg };
    delete body.serverSig;
    const canonical = JSON.stringify(body);
    const data = naclUtil.decodeUTF8(canonical);
    const sig = naclUtil.decodeBase64(msg.serverSig);
    const pk = naclUtil.decodeBase64(serverSigningKeyB64);
    return nacl.sign.detached.verify(data, sig, pk);
  } catch {
    return false;
  }
}

// ============================================================================
// SEALED SENDER — Hide sender identity from server
// ============================================================================

// Encrypt an inner payload so only the recipient can unseal it.
// Uses a one-time ephemeral key for the outer box.
const SEALED_SENDER_DOMAIN_TAG = naclUtil.decodeUTF8('SEALED_SENDER_V1:');

function sealMessage(innerPayloadJson, recipientIdentityPubKeyB64) {
  const ephKP = nacl.box.keyPair();
  const recipPub = naclUtil.decodeBase64(recipientIdentityPubKeyB64);
  const nonce = nacl.randomBytes(nacl.box.nonceLength);
  const jsonBytes = naclUtil.decodeUTF8(innerPayloadJson);
  // Prepend domain tag for domain separation
  const plaintext = new Uint8Array(SEALED_SENDER_DOMAIN_TAG.length + jsonBytes.length);
  plaintext.set(SEALED_SENDER_DOMAIN_TAG, 0);
  plaintext.set(jsonBytes, SEALED_SENDER_DOMAIN_TAG.length);
  const ciphertext = nacl.box(plaintext, nonce, recipPub, ephKP.secretKey);
  zeroOut(ephKP.secretKey);
  zeroOut(plaintext);
  return {
    ephemeralKey: naclUtil.encodeBase64(ephKP.publicKey),
    nonce: naclUtil.encodeBase64(nonce),
    ciphertext: naclUtil.encodeBase64(ciphertext),
  };
}

// Decrypt a sealed envelope using our identity secret key.
function unsealMessage(sealedEnvelope, recipientIdentitySecretKeyB64) {
  try {
    const ephPub = naclUtil.decodeBase64(sealedEnvelope.ephemeralKey);
    const nonce = naclUtil.decodeBase64(sealedEnvelope.nonce);
    const ciphertext = naclUtil.decodeBase64(sealedEnvelope.ciphertext);
    const secretKey = naclUtil.decodeBase64(recipientIdentitySecretKeyB64);
    const decrypted = nacl.box.open(ciphertext, nonce, ephPub, secretKey);
    if (!decrypted) return null;
    // Verify and strip domain separation tag
    if (decrypted.length < SEALED_SENDER_DOMAIN_TAG.length) { zeroOut(decrypted); return null; }
    for (let i = 0; i < SEALED_SENDER_DOMAIN_TAG.length; i++) {
      if (decrypted[i] !== SEALED_SENDER_DOMAIN_TAG[i]) { zeroOut(decrypted); return null; }
    }
    const jsonBytes = decrypted.subarray(SEALED_SENDER_DOMAIN_TAG.length);
    const result = naclUtil.encodeUTF8(jsonBytes);
    zeroOut(decrypted);
    return JSON.parse(result);
  } catch {
    return null;
  }
}

// ============================================================================
// EXPORTS
// ============================================================================

module.exports = {
  // Memory
  zeroOut,
  // HKDF
  hkdf,
  // Key generation
  generateKeyPair,
  generateSigningKeyPair,
  // Signatures
  sign,
  signString,
  signBytes,
  verify,
  verifyBytes,
  fingerprint,
  // Legacy encryption (backward compat)
  encryptFor,
  decryptFrom,
  generateGroupKey,
  encryptGroup,
  decryptGroup,
  // X3DH
  generatePreKeyBundle,
  x3dhInitiate,
  x3dhRespond,
  // Double Ratchet
  ratchetInitSender,
  ratchetInitReceiver,
  ratchetEncrypt,
  ratchetDecrypt,
  // Sender Keys
  generateSenderKey,
  senderKeyEncrypt,
  senderKeyDecrypt,
  // Profile encryption
  encryptProfile,
  decryptProfile,
  SCRYPT_N_CURRENT,
  SCRYPT_N_LEGACY,
  // Nonce dedup
  checkClientNonce,
  // Group key auth (legacy)
  signGroupKey,
  verifyGroupKey,
  // Passphrase
  estimatePassphraseStrength,
  // Server sig verification
  verifyServerSignature,
  // Message padding
  padMessage,
  unpadMessage,
  // Sealed sender
  sealMessage,
  unsealMessage,
  // Base64 helpers (for key validation)
  decodeBase64: naclUtil.decodeBase64,
  encodeBase64: naclUtil.encodeBase64,
};
