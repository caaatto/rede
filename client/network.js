'use strict';

const WebSocket = require('ws');
const fs = require('fs');
const path = require('path');
const net = require('net');
const http = require('http');
const nacl = require('tweetnacl');
const naclUtil = require('tweetnacl-util');
const { SocksProxyAgent } = require('socks-proxy-agent');
const { createClientMessage, parseMessage } = require('../shared/protocol');

const PINFILE = path.join(require('os').homedir(), '.rede', '.cert_pin');

class RedeConnection {
  constructor(serverUrl, options = {}) {
    this.serverUrl = serverUrl;
    this.useTor = options.useTor || false;
    this.useI2P = options.useI2P || false;
    this.torProxy = options.torProxy || 'socks5h://127.0.0.1:9050';
    this.i2pProxy = options.i2pProxy || 'socks5h://127.0.0.1:4447';
    this.ws = null;
    this.serverSigningKey = null; // Set after auth/register to verify server messages
    this.handlers = new Map();
    this.reconnectDelay = this.useI2P ? 5000 : 2000;
    this.shouldReconnect = true;
    this.onReconnect = null;

    // Load pinned certificate fingerprint from disk (TOFU)
    this.certFingerprint = this._loadPinnedCert();

    // Warn about insecure connections
    if (this.useI2P) {
      if (!serverUrl.includes('.i2p')) {
        console.error('[WARNING] Using --i2p but server address is not a .i2p address.');
      }
    } else if (!serverUrl.startsWith('wss://') && !serverUrl.includes('localhost') && !serverUrl.includes('127.0.0.1')) {
      console.error('[WARNING] Connecting over unencrypted WebSocket to a remote host!');
      console.error('[WARNING] Use wss:// for secure connections.');
    }
    if (this.useTor && !serverUrl.includes('.onion')) {
      console.error('[WARNING] Using Tor without .onion address. Exit node can see your traffic.');
    }
  }

  _loadPinnedCert() {
    try {
      if (fs.existsSync(PINFILE)) {
        const pins = JSON.parse(fs.readFileSync(PINFILE, 'utf8'));
        return pins[this.serverUrl] || null;
      }
    } catch {}
    return null;
  }

  _savePinnedCert(fingerprint) {
    let pins = {};
    try {
      if (fs.existsSync(PINFILE)) {
        pins = JSON.parse(fs.readFileSync(PINFILE, 'utf8'));
      }
    } catch {}
    pins[this.serverUrl] = fingerprint;
    const dir = path.dirname(PINFILE);
    if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true, mode: 0o700 });
    fs.writeFileSync(PINFILE, JSON.stringify(pins, null, 2), { mode: 0o600 });
  }

  _checkSocksProxy(proxyUrl) {
    return new Promise((resolve, reject) => {
      const url = new URL(proxyUrl);
      const host = url.hostname;
      const port = parseInt(url.port);

      const socket = net.createConnection({ host, port, timeout: 2000 });

      socket.on('connect', () => {
        socket.destroy();
        resolve(true);
      });

      socket.on('error', (err) => {
        socket.destroy();
        reject(new Error(`SOCKS proxy not reachable at ${host}:${port}. Is i2pd/Tor running?`));
      });

      socket.on('timeout', () => {
        socket.destroy();
        reject(new Error(`SOCKS proxy timeout at ${host}:${port}`));
      });
    });
  }

  _getI2pdStatus() {
    return new Promise((resolve) => {
      const req = http.get('http://127.0.0.1:7070/?page=status', { timeout: 2000 }, (res) => {
        let data = '';
        res.on('data', chunk => data += chunk);
        res.on('end', () => {
          // Parse basic info from HTML
          const status = {};

          // Extract tunnel count
          const tunnelMatch = data.match(/Tunnels<\/b>:\s*(\d+)/i);
          if (tunnelMatch) status.tunnels = parseInt(tunnelMatch[1]);

          // Extract peer count (known routers)
          const peerMatch = data.match(/(\d+)\s+known routers/i);
          if (peerMatch) status.peers = parseInt(peerMatch[1]);

          // Extract uptime
          const uptimeMatch = data.match(/Uptime<\/b>:\s*([^<]+)/i);
          if (uptimeMatch) status.uptime = uptimeMatch[1].trim();

          resolve(status);
        });
      });

      req.on('error', () => resolve(null));
      req.on('timeout', () => {
        req.destroy();
        resolve(null);
      });
    });
  }

  async connect() {
    // Check SOCKS proxy availability before attempting connection
    if (this.useI2P) {
      try {
        await this._checkSocksProxy(this.i2pProxy);

        // Get i2pd status for better diagnostics
        const status = await this._getI2pdStatus();
        if (status) {
          const peers = status.peers || 0;
          const tunnels = status.tunnels || 0;

          console.error(`[i2pd] Uptime: ${status.uptime || 'unknown'}, Peers: ${peers}, Tunnels: ${tunnels}`);

          // Warn if network not ready
          if (peers < 10) {
            console.error('[i2pd] WARNING: Very few peers connected. Network may still be bootstrapping.');
            console.error('[i2pd] Connection may be slow or fail. Wait a few minutes and try again.');
          }
          if (tunnels === 0) {
            console.error('[i2pd] WARNING: No tunnels established yet. This is normal on first start.');
            console.error('[i2pd] First connection may take 1-2 minutes to establish tunnels.');
          }
        }
      } catch (err) {
        const status = await this._getI2pdStatus();
        let errorMsg = `${err.message}\n\nTroubleshooting:\n  1. Check if i2pd is running\n  2. Verify port 4447 is not blocked by firewall\n  3. Wait for i2pd to bootstrap (may take 5-15 minutes on first start)`;

        if (status) {
          errorMsg += `\n\ni2pd Status:\n  Uptime: ${status.uptime || 'unknown'}\n  Peers: ${status.peers || 0}\n  Tunnels: ${status.tunnels || 0}`;
          if ((status.peers || 0) < 10) {
            errorMsg += '\n\n  Network is still bootstrapping. Please wait and try again.';
          }
        }

        throw new Error(errorMsg);
      }
    } else if (this.useTor) {
      try {
        await this._checkSocksProxy(this.torProxy);
      } catch (err) {
        throw new Error(`${err.message}\n\nTroubleshooting:\n  1. Check if Tor is running\n  2. Verify Tor Browser or tor daemon is active`);
      }
    }

    return new Promise((resolve, reject) => {
      const opts = {};

      // For I2P/Tor: plain WS, no TLS needed (encrypted at transport layer)
      if (this.useI2P) {
        opts.agent = new SocksProxyAgent(this.i2pProxy);
      } else if (this.useTor) {
        opts.agent = new SocksProxyAgent(this.torProxy);
      }

      // Block plain ws:// to non-localhost addresses (no encryption)
      if (this.serverUrl.startsWith('ws://') && !this.useI2P && !this.useTor) {
        const url = new URL(this.serverUrl);
        if (url.hostname !== 'localhost' && url.hostname !== '127.0.0.1' && url.hostname !== '::1') {
          reject(new Error('Refusing unencrypted ws:// to remote host. Use wss:// or --i2p/--tor.'));
          return;
        }
      }

      // TLS: use rejectUnauthorized:false only for self-signed certs, but verify via TOFU pinning
      if (this.serverUrl.startsWith('wss://')) {
        opts.rejectUnauthorized = false; // Validated below via fingerprint pinning
      }

      // Set handshake timeout: I2P is slow, give it more time
      opts.handshakeTimeout = this.useI2P ? 90000 : (this.useTor ? 60000 : 15000);

      this.ws = new WebSocket(this.serverUrl, opts);

      const onOpen = () => {
        this.ws.removeListener('error', onError);
        // TLS certificate fingerprint pinning (TOFU — Trust On First Use)
        if (this.serverUrl.startsWith('wss://')) {
          const cert = this.ws._socket?.getPeerCertificate?.();
          if (cert) {
            const fp = cert.fingerprint256 || cert.fingerprint || null;
            if (fp) {
              if (!this.certFingerprint) {
                this.certFingerprint = fp;
                this._savePinnedCert(fp);
                console.error(`[TOFU] First connection — certificate pinned.`);
                console.error(`[TOFU] Fingerprint: ${fp}`);
                console.error(`[TOFU] Verify this with the server admin!`);
              } else if (this.certFingerprint !== fp) {
                console.error('[SECURITY] Server certificate has CHANGED!');
                console.error(`  Pinned:  ${this.certFingerprint}`);
                console.error(`  Current: ${fp}`);
                console.error('[SECURITY] Connection BLOCKED. Delete ~/.rede/.cert_pin to re-pin.');
                this.ws.close();
                reject(new Error('Server certificate changed! Possible MITM attack. Connection blocked.'));
                return;
              }
            } else {
              console.error('[WARNING] Could not extract certificate fingerprint for pinning.');
            }
          }
        }
        resolve();
      };

      const onError = (err) => {
        this.ws.removeListener('open', onOpen);
        reject(err);
      };

      this.ws.once('open', onOpen);
      this.ws.once('error', onError);

      this.ws.on('message', (raw) => {
        const msg = parseMessage(raw.toString());
        if (!msg) return;
        // Verify server signature if we have a pinned key
        if (this.serverSigningKey) {
          if (!msg.serverSig) {
            console.error('[SECURITY] Missing server signature! Message dropped (possible MITM).');
            return;
          }
          if (!this._verifyServerSig(msg)) {
            console.error('[SECURITY] Invalid server signature! Message dropped.');
            return;
          }
        }
        const handler = this.handlers.get(msg.type);
        if (handler) handler(msg);
      });

      this.ws.on('close', () => {
        if (this.shouldReconnect) {
          setTimeout(async () => {
            try {
              await this.connect();
              if (this.onReconnect) this.onReconnect();
            } catch { /* retry on next interval */ }
          }, this.reconnectDelay);
        }
      });
    });
  }

  _verifyServerSig(msg) {
    try {
      const sig = naclUtil.decodeBase64(msg.serverSig);
      const copy = Object.assign({}, msg);
      delete copy.serverSig;
      const body = naclUtil.decodeUTF8(JSON.stringify(copy));
      const pubKey = naclUtil.decodeBase64(this.serverSigningKey);
      return nacl.sign.detached.verify(body, sig, pubKey);
    } catch {
      return false;
    }
  }

  on(messageType, handler) {
    this.handlers.set(messageType, handler);
  }

  send(type, payload = {}) {
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this.ws.send(createClientMessage(type, payload));
      return true;
    }
    return false;
  }

  disconnect() {
    this.shouldReconnect = false;
    // Notify server to clean up session state
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this.ws.send(createClientMessage('session_end', {}));
    }
    if (this.ws) this.ws.close();
  }
}

module.exports = { RedeConnection };
