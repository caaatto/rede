```
 ____   _____  ____   _____
|  _ \ |___ / |  _ \ |___ /
| |_) |  |_ \ | | | |  |_ \
|  _ <  ___) || |_| | ___) |
|_| \_\|____/ |____/ |____/

  [!] NO LOGS  [!] NO METADATA  [!] NO TRACES
  [!] E2EE + PERFECT FORWARD SECRECY
```

---

Rede is an end-to-end encrypted messenger.
No phone number. No email. No metadata. Just keys.

All messages are encrypted on the sender's device and decrypted
on the recipient's device. The server never sees plaintext, never
knows who is talking to whom, and stores nothing it doesn't have to.

Two client modes:
- **Desktop** (v2) — Avalonia 11 native GUI (.NET 8, cross-platform)
- **Terminal** (v1) — Node.js TUI + CLI


## protocol

```
  CRYPTO ....... X3DH + Double Ratchet + XSalsa20-Poly1305
  SIGNING ...... Ed25519
  KEY STORE .... scrypt(N=2^20, r=8, p=1) + NaCl secretbox
  PFS .......... per-message (1:1) / Sender Keys (groups + places)
  TRANSPORT .... WSS/TLS, Tor (.onion), I2P (.i2p garlic)
  SEALED ....... sender identity hidden from server (nacl.box envelope)
  PADDING ...... fixed-size buckets (256/1024/4096/16384 bytes)
  PLACES ....... E2EE channel metadata, server sees only opaque IDs
  VOICE ........ E2EE calls via SRTP (AES-128-CM + HMAC-SHA1-80)
  SFU .......... mediasoup relay (forwards encrypted audio, never decrypts)
```

The cryptographic design follows the Signal Protocol. X3DH handles
key agreement, Double Ratchet provides forward secrecy per message,
and Sender Keys extend PFS to group conversations. All keys are
signed with Ed25519.

Sealed sender hides who sent a message from the server. The server
only sees the recipient. Sender identity is encrypted inside the
message envelope using the recipient's public key.

Message padding normalizes all ciphertexts to fixed size buckets
so an observer cannot infer content from message length.

Voice calls use SRTP with keys exchanged over the existing Double
Ratchet session. The server (SFU) forwards encrypted audio packets
without decryption. Two transport modes:
- **Fast**: WebRTC/UDP direct to SFU — low latency (~50-100ms), SFU sees your IP
- **Secure**: SRTP tunneled in binary WebSocket frames over I2P — high latency (~500ms-2s), fully anonymous

Both modes use E2EE. The user chooses per call. Incoming fast calls
can be blocked in settings.


## requirements

### desktop client (v2)
- Windows x64 or Linux x64
- Optional: .NET 8 SDK (only if building from source)
- Optional: i2pd or Tor for anonymous transport
- Optional: PortAudio library for voice calls (bundled in standalone builds)

### terminal client (v1)
- Node.js >= 18
- A running Rede server with an invite code
- Optional: Tor or I2P for anonymous transport

### server
- Node.js >= 18
- SQLite (default, zero config) or PostgreSQL (`REDE_PG_URL`)
- Optional: Redis for multi-worker scaling (`REDE_REDIS_URL`)
- Optional: mediasoup for voice calls (included in dependencies)


## install

### desktop client — standalone executable (recommended)

Download the latest release for your platform from
[GitHub Releases](https://github.com/caaatto/rede/releases).

**Linux:**
```
chmod +x Rede-Desktop-linux-x64
./Rede-Desktop-linux-x64
```

**Windows:**
```
Rede-Desktop-win-x64.exe
```

No .NET SDK required — the executable is self-contained.
The client auto-updates when a new release is published.

### desktop client — from source

```
git clone https://github.com/caaatto/rede.git && cd rede/rede-client
dotnet build Rede.sln
dotnet run --project src/Rede.Desktop
```

### terminal client

```
git clone https://github.com/caaatto/rede.git && cd rede && npm install && cp .env.example .env
```

Edit `.env` to configure your server connection:
```
REDE_SERVER=wss://your-server:9377       # or ws://<address>.i2p for I2P
REDE_TRANSPORT=                          # i2p or tor (leave empty for clearnet)
REDE_I2P_PROXY=socks5h://127.0.0.1:4447 # only needed for I2P
REDE_TOR_PROXY=socks5h://127.0.0.1:9050 # only needed for Tor
```

For WSS/TLS, generate self-signed certs:
```
mkdir -p certs
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 \
  -keyout certs/key.pem -out certs/cert.pem -days 365 -nodes -subj "/CN=rede"
```

### server

```
git clone https://github.com/caaatto/rede.git && cd rede && npm install
```

**Single process (default, SQLite):**
```
npm run server
```

**Multi-worker cluster (PostgreSQL + Redis):**
```
REDE_DB_BACKEND=pg REDE_PG_URL=postgresql://user:pass@localhost:5432/rede \
  REDE_REDIS_URL=redis://localhost:6379 npm run cluster
```

**Migrate SQLite to PostgreSQL:**
```
REDE_PG_URL=postgresql://user:pass@localhost:5432/rede node server/migrate-sqlite-to-pg.js
```

**Server environment variables:**
```
REDE_DB_BACKEND     pg or sqlite (default: sqlite)
REDE_PG_URL         PostgreSQL connection string
REDE_REDIS_URL      Redis connection string (optional, falls back to in-memory)
REDE_SFU_WORKERS    mediasoup worker count (default: 1)
REDE_SFU_IP         Public IP for WebRTC (default: 0.0.0.0)
REDE_RTC_MIN_PORT   RTP port range start (default: 40000)
REDE_RTC_MAX_PORT   RTP port range end (default: 49999)
REDE_SFU_MAX_ROOMS  Max concurrent voice rooms (default: 50)
```


## usage

### desktop GUI

Launch the desktop client (standalone exe or from source):
```
./Rede-Desktop-linux-x64          # standalone
dotnet run --project src/Rede.Desktop   # from source
```

The GUI provides login/register, sidebar with contacts, groups, and
places (Discord-like servers with channels), chat view with message
history, and a settings panel. All slash commands from the terminal
client also work in the message input.

Right-click a contact to invite them to a group or view their fingerprint.

Configure via `.env` file or environment variables. The client searches
for `.env` in the repo root, `~/Rede/rede-client/`, and
`~/.local/share/Rede/`:
```
REDE_SERVER=ws://your-server.i2p
REDE_TRANSPORT=i2p              # i2p, tor, or empty for clearnet
REDE_I2P_PROXY=socks5h://127.0.0.1:4447
REDE_TOR_PROXY=socks5h://127.0.0.1:9050
```

### TUI mode (interactive)

```
node client/index.js -u <user#id> -s wss://<server>:9377
```

Options:
```
  -s, --server <url>       server address
  -u, --user <id#tag>      your user ID
  -i, --invite <code>      register with invite code
  --link <code>            link a new device
  --tor                    route through Tor
  --i2p                    route through I2P
  --tor-proxy <url>        custom Tor SOCKS5 (default: socks5h://127.0.0.1:9050)
  --i2p-proxy <url>        custom I2P SOCKS5 (default: socks5h://127.0.0.1:4447)
```

### CLI mode (scriptable)

Send a message:
```
node client/cli.js send -u <user#id> -s wss://<server> --to <recipient#id> -m "message"
```

Send to a group:
```
node client/cli.js send -u <user#id> -s wss://<server> --group <groupid> -m "message"
```

Listen for incoming messages:
```
node client/cli.js listen -u <user#id> -s wss://<server>
```

Register:
```
node client/cli.js register -s wss://<server> --invite <code>
```

Link a device:
```
node client/cli.js link -u <user#id> -s wss://<server> --link <code>
```


## commands

Available in both the desktop GUI message input and the terminal TUI.

```
  /add <id#xxxx>           add contact
  /confirm <id#xxxx>       accept key change
  /fingerprint [user]      show fingerprint
  /group <name>            create group
  /ginvite <grp> <user>    invite to group (sends group key via E2EE DM)
  /kick <grp> <user>       remove from group
  /rekey <group>           rotate group sender key
  /place <name>            create a place (server with channels)
  /pchannel <place> <name> create channel in a place
  /pinvite <place> <user>  invite user to place
  /pkick <place> <user>    remove user from place
  /pleave <place>          leave a place
  /prekey <place>          rotate place metadata key
  /ttl <days>              auto-delete messages after N days (0 = off)
  /link                    generate device link code
  /devices                 show device info
  /settings                identity & key info
  /help                    show help
```

### desktop keyboard shortcuts
```
  enter ............... send message
  ctrl+q .............. quit
  escape .............. toggle sidebar
```

### TUI keyboard shortcuts
```
  tab ................. switch focus (contacts / input)
  ctrl+c .............. quit
```


## registration

You need an invite code from the server admin to register.

**Desktop:** enter your display name, passphrase, server address,
and invite code on the login screen, then click Register.

**Terminal:**
```
node client/index.js -s wss://<server>:9377 -i <invite-code>
```

You will be asked to choose a display name and a passphrase.
The passphrase encrypts your profile at rest (min 12 characters).

Your user ID will be `<displayname>#<tag>` (e.g. `alice#a3f1`).


## transport

### direct (WSS/TLS)
Connect directly to the server over TLS. Select "Direct (WSS)" in the
desktop client or use `wss://` in the terminal client. Requires the
server to have TLS certificates. Certificate fingerprints are pinned
on first use (TOFU).

Your IP address is visible to the server with direct connections.
Messages remain end-to-end encrypted — the server cannot read them.
Other users never see your IP regardless of transport.

### Tor
```
node client/index.js -u <id> -s wss://<server>.onion --tor
```
Requires Tor running locally (SOCKS5 on port 9050).

### I2P
```
node client/index.js -u <id> -s ws://<server>.i2p --i2p
```
Requires i2pd running locally (SOCKS5 on port 4447).
I2P provides garlic routing with end-to-end tunnel encryption.
First connections may take 1-2 minutes to establish tunnels.


## voice calls

Rede supports end-to-end encrypted voice calls. The server acts as a
Selective Forwarding Unit (SFU) — it relays encrypted SRTP packets
between participants without ever decrypting the audio.

Two transport modes per call:

| | Fast | Secure |
|---|---|---|
| Transport | WebRTC/UDP direct | Binary WS over I2P |
| Latency | ~50-100ms | ~500-2000ms |
| IP visible to SFU | Yes | No (I2P tunnel) |
| Audio encryption | SRTP (E2EE) | SRTP (E2EE) |

The caller chooses the mode. The callee can block fast calls in settings
(Settings > Voice Calls > Allow Fast Calls). If modes don't match, the
call is automatically rejected with a mode mismatch notification.

SRTP master key and salt are generated by the caller and sent to the
callee encrypted via their existing Double Ratchet session. The server
never has access to the key material.

In the desktop client, calls appear as an overlay bar at the bottom of
the chat area with Accept/Decline (incoming), Mute/Unmute, Hang Up,
duration timer, and a mode indicator (lock = secure, lightning = fast).


## multi-device

Each device gets its own identity and signing keys.
Messages are delivered to all devices of a recipient.

To link a new device:
1. On existing device: `/link` (generates a one-time code, 5 min expiry)
2. On new device: `node client/cli.js link -u <id> -s <server> --link <code>`


## what the server sees

```
  message content ........ NO  (E2EE, never plaintext)
  sender identity ........ NO  (sealed sender for established sessions)
  recipient identity ..... YES (must route the message)
  message timing ......... YES (when a message arrives)
  message size ........... NO  (fixed-size padding buckets)
  group membership ....... YES (server manages group state)
  place membership ....... YES (server manages roster)
  place channel names .... NO  (E2EE metadata, server sees opaque IDs)
  voice audio ............ NO  (SRTP, server forwards encrypted packets)
  call participants ...... YES (server routes signaling)
  IP address ............. NO  (if using Tor/I2P; YES for direct WSS/fast voice)
  user public keys ....... YES (required for key exchange)
```


## what the server stores

```
  user IDs + public keys .. encrypted at rest (scrypt + NaCl)
  pending messages ........ encrypted blobs, no sender for sealed
  pre-key bundles ......... for X3DH key agreement
  group membership ........ member lists
  place membership ........ member lists + opaque channel IDs
  nonce replay cache ...... hashed, no cleartext identity
```


## security model

- Forward secrecy: compromising current keys does not reveal past messages
- Post-compromise security: new DH ratchet step heals after key compromise
- TOFU pinning: server TLS certificate and signing key pinned on first use
- Server signatures: all server responses signed with Ed25519
- Rate limiting: per-user and per-target limits on all operations
- No legacy fallback: Double Ratchet required for 1:1, Sender Keys for groups
- Voice E2EE: SRTP keys exchanged via Double Ratchet, server is a blind relay
- Voice privacy mode: user chooses fast (direct) or secure (I2P) per call


## profile storage

Your profile (keys, contacts, chat history) is stored in `~/.rede/`
encrypted with your passphrase using scrypt + NaCl secretbox.

Do not lose your passphrase. There is no recovery mechanism.


## license

AGPL-3.0 -- see [LICENSE](LICENSE)
