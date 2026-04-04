'use strict';

const crypto = require('crypto');

const C = {
  reset: '\x1b[0m',
  dim: '\x1b[2m',
  bold: '\x1b[1m',
  red: '\x1b[31m',
  brightRed: '\x1b[91m',
  yellow: '\x1b[33m',
};

function randomHex(len) {
  return crypto.randomBytes(len).toString('hex');
}

function sleep(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

function clearScreen() {
  process.stdout.write('\x1b[2J\x1b[H');
}

async function typeLine(text, delay = 2) {
  for (const ch of text) {
    process.stdout.write(ch);
    if (delay > 0) await sleep(delay);
  }
  process.stdout.write('\n');
}

async function statusLine(label, value) {
  process.stdout.write(`  ${C.dim}${C.red}[${C.reset}${C.red}${label}${C.dim}${C.red}]${C.reset} `);
  for (let i = 0; i < 2 + Math.floor(Math.random() * 3); i++) {
    process.stdout.write(`${C.dim}${C.red}.${C.reset}`);
    await sleep(40 + Math.random() * 80);
  }
  console.log(` ${C.red}${value}${C.reset}`);
}

async function hexDump(lines = 2) {
  for (let i = 0; i < lines; i++) {
    const addr = (i * 16).toString(16).padStart(8, '0');
    const hex = randomHex(16).match(/.{2}/g).join(' ');
    console.log(`  ${C.dim}${C.red}${addr}  ${hex}${C.reset}`);
    await sleep(20);
  }
}

const LOGO_REDE = [
  ' ____   _____  ____   _____ ',
  '|  _ \\ | ____||  _ \\ | ____|',
  '| |_) ||  _|  | | | ||  _|  ',
  '|  _ < | |___ | |_| || |___ ',
  '|_| \\_\\|_____||____/ |_____|',
];

const LOGO_R3D3 = [
  ' ____   _____  ____   _____ ',
  '|  _ \\ |___ / |  _ \\ |___ / ',
  '| |_) |  |_ \\ | | | |  |_ \\ ',
  '|  _ <  ___) || |_| | ___) |',
  '|_| \\_\\|____/ |____/ |____/ ',
];

async function animateLogo() {
  const frames = 8;
  const lines = LOGO_REDE.length;

  // Show REDE first
  for (let l = 0; l < lines; l++) {
    process.stdout.write(`\x1b[${l + 2};3H${C.brightRed}${LOGO_REDE[l]}${C.reset}`);
  }
  await sleep(600);

  // Glitch transition: REDE -> R3D3
  const glitchChars = '@#$%&*!=+~<>/?';
  for (let f = 0; f < frames; f++) {
    const progress = f / frames;
    for (let l = 0; l < lines; l++) {
      let line = '';
      const src = LOGO_REDE[l];
      const dst = LOGO_R3D3[l];
      const len = Math.max(src.length, dst.length);
      for (let c = 0; c < len; c++) {
        if (Math.random() < progress) {
          line += dst[c] || ' ';
        } else if (Math.random() < 0.3) {
          line += glitchChars[Math.floor(Math.random() * glitchChars.length)];
        } else {
          line += src[c] || ' ';
        }
      }
      process.stdout.write(`\x1b[${l + 2};3H${C.brightRed}${line}${C.reset}`);
    }
    await sleep(80);
  }

  // Final R3D3
  for (let l = 0; l < lines; l++) {
    process.stdout.write(`\x1b[${l + 2};3H${C.brightRed}${C.bold}${LOGO_R3D3[l]}${C.reset}`);
  }
  await sleep(200);
}

// --- Main Boot Sequence ---
async function bootSequence(options = {}) {
  const { userId, isNewUser, useI2P, useTor, serverUrl } = options;

  clearScreen();
  process.stdout.write('\x1b[?25l'); // hide cursor

  await animateLogo();

  // Move below logo
  const startRow = 8;
  process.stdout.write(`\x1b[${startRow};1H`);

  console.log(`  ${C.dim}${C.red}============================================${C.reset}`);
  await typeLine(`  ${C.dim}${C.red}[${new Date().toISOString()}] SYSTEM INIT${C.reset}`, 1);
  console.log(`  ${C.dim}${C.red}============================================${C.reset}`);
  console.log('');

  await statusLine('CRYPTO  ', 'X3DH + Double Ratchet + XSalsa20-Poly1305');
  await statusLine('ENTROPY ', `${randomHex(8)} ................ OK`);
  await statusLine('KEYSTORE', 'scrypt(N=2^20,r=8,p=1) ......... SEALED');
  await statusLine('PREKEYS ', 'Signed pre-key + 20 OTPKs ...... READY');

  console.log('');

  if (useI2P) {
    await statusLine('NETWORK ', 'I2P GARLIC ROUTING ............. INIT');
    await statusLine('SOCKS5  ', '127.0.0.1:14447 ................ BOUND');
    await statusLine('TUNNELS ', 'in=3 out=3 hops=3 ............. BUILD');
  } else if (useTor) {
    await statusLine('NETWORK ', 'TOR ONION ROUTING .............. INIT');
    await statusLine('SOCKS5  ', '127.0.0.1:9050 ................ BOUND');
    await statusLine('CIRCUIT ', '3-hop circuit .................. BUILD');
  } else {
    await statusLine('NETWORK ', 'WSS/TLS DIRECT ................ INIT');
    await statusLine('TLS     ', 'ECDHE-P256 + AES-256-GCM ...... HANDSHAKE');
  }

  await statusLine('ENDPOINT', serverUrl.slice(0, 40) + (serverUrl.length > 40 ? '...' : ''));

  console.log('');

  if (isNewUser) {
    await statusLine('IDENTITY', 'GENERATING NEW KEYPAIR ......... WAIT');
    await hexDump(3);
    await statusLine('REGISTER', `${userId} .................. NEW`);
  } else {
    await statusLine('IDENTITY', `${userId} .............. DECRYPT`);
    await hexDump(2);
    await statusLine('AUTH    ', 'CHALLENGE-RESPONSE ............. SIGN');
  }

  console.log('');
  console.log(`  ${C.dim}${C.red}[!] NO LOGS  [!] NO METADATA  [!] NO TRACES${C.reset}`);
  console.log(`  ${C.red}[!] E2EE + PERFECT FORWARD SECRECY${C.reset}`);

  console.log('');
  console.log(`  ${C.dim}${C.red}============================================${C.reset}`);
  await typeLine(`  ${C.brightRed}${C.bold}>> SYSTEM READY :: ENTERING SECURE CHANNEL <<${C.reset}`, 2);
  console.log(`  ${C.dim}${C.red}============================================${C.reset}`);
  console.log('');

  process.stdout.write('\x1b[?25h'); // show cursor
  await sleep(400);
}

// Minimal boot for CLI mode
async function cliBoot(options = {}) {
  const { userId, useI2P, useTor, command } = options;
  const net = useI2P ? 'I2P' : useTor ? 'TOR' : 'WSS';

  process.stderr.write(`${C.dim}${C.red}>>>>${C.reset} ${C.bold}${C.red}R3D3${C.reset} ${C.dim}${C.red}::${C.reset} ${C.red}${command}${C.reset} ${C.dim}${C.red}::${C.reset} ${C.red}${userId}${C.reset} ${C.dim}${C.red}::${C.reset} ${C.red}${net}${C.reset} `);

  const frames = ['/', '-', '\\', '|'];
  let i = 0;
  const spinner = setInterval(() => {
    process.stderr.write(`\r${C.dim}${C.red}>>>>${C.reset} ${C.bold}${C.red}R3D3${C.reset} ${C.dim}${C.red}::${C.reset} ${C.red}${command}${C.reset} ${C.dim}${C.red}::${C.reset} ${C.red}${userId}${C.reset} ${C.dim}${C.red}::${C.reset} ${C.red}${net}${C.reset} ${C.brightRed}${frames[i % frames.length]}${C.reset}`);
    i++;
  }, 80);

  return {
    update: (status) => {
      process.stderr.write(`\r${C.dim}${C.red}>>>>${C.reset} ${C.bold}${C.red}R3D3${C.reset} ${C.dim}${C.red}::${C.reset} ${C.red}${command}${C.reset} ${C.dim}${C.red}::${C.reset} ${C.yellow}${status}${C.reset}   `);
    },
    stop: (status = 'OK') => {
      clearInterval(spinner);
      process.stderr.write(`\r${C.dim}${C.red}>>>>${C.reset} ${C.bold}${C.red}R3D3${C.reset} ${C.dim}${C.red}::${C.reset} ${C.red}${command}${C.reset} ${C.dim}${C.red}::${C.reset} ${C.red}${userId}${C.reset} ${C.dim}${C.red}::${C.reset} ${C.red}${status}${C.reset}\n`);
    },
    fail: (msg) => {
      clearInterval(spinner);
      process.stderr.write(`\r${C.dim}${C.red}>>>>${C.reset} ${C.bold}${C.red}R3D3${C.reset} ${C.dim}${C.red}::${C.reset} ${C.red}${command}${C.reset} ${C.dim}${C.red}::${C.reset} ${C.brightRed}FAIL: ${msg}${C.reset}\n`);
    },
  };
}

module.exports = { bootSequence, cliBoot, C };
