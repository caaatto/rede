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

R3D3 is an end-to-end encrypted terminal messenger.
No phone number. No email. No metadata. Just keys.

All messages are encrypted on the sender's device and decrypted
on the recipient's device. The server never sees plaintext, never
knows who is talking to whom, and stores nothing it doesn't have to.


## protocol

```
  CRYPTO ....... X3DH + Double Ratchet + XSalsa20-Poly1305
  SIGNING ...... Ed25519
  KEY STORE .... scrypt(N=2^20, r=8, p=1) + NaCl secretbox
  PFS .......... per-message (1:1) / Sender Keys (groups)
  TRANSPORT .... WSS/TLS, Tor (.onion), I2P (.i2p garlic)
  SEALED ....... sender identity hidden from server (nacl.box envelope)
  PADDING ...... fixed-size buckets (256/1024/4096/16384 bytes)
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


## requirements

- Node.js >= 18
- A running R3D3 server with an invite code
- Optional: Tor or I2P for anonymous transport


## install

```
git clone git@github.com:caaatto/rede-client.git
cd rede-client
npm install
```


## usage

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


## TUI commands

```
  /add <id#xxxx>           add contact
  /confirm <id#xxxx>       accept key change
  /reset <id#xxxx>         reset ratchet session
  /fingerprint [user]      show fingerprint
  /group <name>            create group
  /ginvite <grp> <user>    invite to group
  /kick <grp> <user>       remove from group
  /rekey <group>           rotate sender key
  /ttl <seconds>           self-destruct timer (0 = off)
  /contacts                list contacts
  /groups                  list groups
  /link                    generate device link code
  /key                     show your public key
  /help                    show help
  /quit                    exit

  tab .................. switch focus (contacts / input)
  ctrl+c .............. quit
```


## registration

You need an invite code from the server admin to register.

First run:
```
node client/index.js -s wss://<server>:9377 -i <invite-code>
```

You will be asked to choose a display name and a passphrase.
The passphrase encrypts your profile at rest (min 12 characters).

Your user ID will be `<displayname>#<tag>` (e.g. `alice#a3f1`).


## transport

### clearnet (WSS/TLS)
Default. Requires the server to have TLS certificates.
Certificate fingerprints are pinned on first use (TOFU).

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
  IP address ............. NO  (if using Tor/I2P)
  user public keys ....... YES (required for key exchange)
```


## what the server stores

```
  user IDs + public keys .. encrypted at rest (scrypt + NaCl)
  pending messages ........ encrypted blobs, no sender for sealed
  pre-key bundles ......... for X3DH key agreement
  group membership ........ member lists
  nonce replay cache ...... hashed, no cleartext identity
```


## security model

- Forward secrecy: compromising current keys does not reveal past messages
- Post-compromise security: new DH ratchet step heals after key compromise
- TOFU pinning: server TLS certificate and signing key pinned on first use
- Server signatures: all server responses signed with Ed25519
- Rate limiting: per-user and per-target limits on all operations
- No legacy fallback: Double Ratchet required for 1:1, Sender Keys for groups


## profile storage

Your profile (keys, contacts, chat history) is stored in `~/.rede/`
encrypted with your passphrase using scrypt + NaCl secretbox.

Do not lose your passphrase. There is no recovery mechanism.


## license

ISC
