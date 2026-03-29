# Rede

Rede is an end-to-end encrypted messenger.
No phone number. No email. No metadata. Just keys.

All messages are encrypted on your device and decrypted on the recipient's
device. The server never sees plaintext, never knows who is talking to whom,
and stores nothing it doesn't have to.


## download

Grab the latest release for your platform:

**[Download for Linux / Windows](https://github.com/caaatto/rede/releases)**

**Linux:**
```
chmod +x Rede-Desktop-linux-x64
./Rede-Desktop-linux-x64
```

**Windows:**
Double-click `Rede-Desktop-win-x64.exe`.

No runtime or SDK required — the app is fully self-contained and
auto-updates when a new version is published.


## features

- **End-to-end encryption** — X3DH + Double Ratchet (Signal Protocol), XSalsa20-Poly1305
- **Sealed sender** — the server can't see who sent a message
- **Groups** — Sender Keys for group PFS, Ed25519 signed
- **Places** — Discord-like servers with channels, customizable profile (icon, accent color). All metadata is E2EE
- **Voice calls** — E2EE audio via SRTP (AES-128-CM + HMAC-SHA1-80), Opus 96kbps, server relays encrypted packets
- **Profile customization** — accent colors, avatar images (PNG/GIF/JPEG), shared with contacts
- **Multi-device** — each device has its own keys, messages delivered to all devices
- **Anonymous transport** — connect via I2P or Tor to hide your IP from the server
- **Message padding** — fixed-size buckets prevent traffic analysis
- **Self-destructing messages** — TTL-based auto-delete
- **No tracking** — no phone number, no email, no analytics, no ads


## getting started

1. Download and launch Rede
2. Choose a display name and a strong passphrase (min 12 characters)
3. Enter the server address and an invite code from the server admin
4. Click **Register**

Your user ID will be `displayname#tag` (e.g. `alice#a3f1`).
The passphrase encrypts your profile locally — there is no recovery if you lose it.


## transport options

Rede supports three connection modes. Select your transport on the login screen.

| Transport | Latency | IP hidden | How |
|---|---|---|---|
| Direct (WSS) | ~50-100ms | No | Connect via `wss://` |
| I2P | ~500-2000ms | Yes | Garlic routed via i2pd |
| Tor | ~300-1000ms | Yes | Onion routed via Tor |

Your messages are always E2EE regardless of transport.
Other users never see your IP in any mode.

For I2P or Tor, you need the respective daemon running locally.
The desktop client picks up proxy settings from the `.env` file or
environment variables:

```
REDE_SERVER=ws://address.i2p
REDE_TRANSPORT=i2p
REDE_I2P_PROXY=socks5h://127.0.0.1:4447
REDE_TOR_PROXY=socks5h://127.0.0.1:9050
```


## commands

Type these in the message input box.

```
/add <user#id>              add a contact
/confirm <user#id>          accept a key change
/fingerprint [user]         show identity fingerprint
/group <name>               create a group
/ginvite <group> <user>     invite someone to a group
/kick <group> <user>        remove someone from a group
/rekey <group>              rotate the group key
/place <name>               create a place (server with channels)
/pchannel <place> <name>    add a channel to a place
/pinvite <place> <user>     invite someone to a place
/pkick <place> <user>       remove someone from a place
/pleave <place>             leave a place
/prekey <place>             rotate the place metadata key
/ttl <days>                 auto-delete messages after N days (0 = off)
/call <user#id>             start a voice call
/hangup                     end the call
/mute                       toggle microphone
/link                       generate a device link code
/devices                    show linked devices
/settings                   open settings
/help                       show help
```

**Keyboard shortcuts:**
```
Enter ......... send message
Escape ........ toggle sidebar
Ctrl+Q ........ quit
```

Right-click contacts to invite them to groups/places or view fingerprints.
Right-click groups and places for management options (invite, kick, rotate key).


## voice calls

Audio is encrypted with SRTP at 96kbps Opus (above Discord standard).
The call transport matches your connection — if you're on I2P, your call
is anonymous. SRTP keys are exchanged over your existing Double Ratchet
session, so the server never has access to audio.

Calls appear as an overlay in the chat area with accept/decline, mute, and
hang up controls.


## places

Places work like Discord servers — a place has channels, members, and roles.
Unlike Discord, all metadata (names, topics, icons, colors) is end-to-end
encrypted. The server only sees opaque IDs.

Each place has its own profile — accent color and icon — visible in the
sidebar. Right-click a place to manage members, channels, keys, and the
place profile. Only the creator can edit the profile, kick members, or
delete channels.


## profile

Customize your profile in Settings:
- **Accent color** — 12 preset colors, visible to your contacts in chat
- **Avatar** — upload a PNG, JPEG, or GIF (max 256KB)

Changes are previewed locally. Click **Apply** to save and share with contacts.


## multi-device

Link additional devices to receive messages on all of them.

1. On your existing device: `/link` (generates a code, valid for 5 minutes)
2. On the new device: enter the link code during setup

Each device has its own cryptographic identity.


## privacy

```
message content ........... never visible to server (E2EE)
sender identity ........... hidden (sealed sender)
recipient identity ........ visible (server must route)
message size .............. hidden (fixed-size padding)
group/place membership .... visible (server manages roster)
channel names/place profile  hidden (E2EE metadata)
voice audio ............... never visible (SRTP, blind relay)
call participants ......... visible (server routes signaling)
your IP address ........... hidden with I2P/Tor, visible with direct WSS
```

Your profile (keys, contacts, chat history) is stored locally in `~/.rede/`,
encrypted with your passphrase using scrypt + NaCl secretbox.
There is no recovery mechanism — do not lose your passphrase.


## security

- Forward secrecy: past messages stay safe if current keys are compromised
- Post-compromise security: new key exchange heals after compromise
- TOFU pinning: server certificate and signing key pinned on first contact
- No legacy fallback: modern crypto required, no downgrades
- Server signatures: all server responses signed with Ed25519
- Voice E2EE: SRTP keys never leave the Double Ratchet session


## license

AGPL-3.0 -- see [LICENSE](LICENSE)
