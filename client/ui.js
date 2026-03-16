'use strict';

// Pure ANSI terminal UI — no blessed, no dependencies
// Works reliably on Windows CMD, PowerShell, and Linux terminals

const ESC = '\x1b[';

class RedeUI {
  constructor() {
    this.onCommand = null;
    this.onMessage = null;
    this.currentChat = null;

    this._mode = 'input';
    this._inputText = '';
    this._status = 'disconnected';
    this._contacts = [];
    this._contactIndex = 0;
    this._chatLines = [];
    this._cols = process.stdout.columns || 80;
    this._rows = process.stdout.rows || 24;

    // Track terminal size
    process.stdout.on('resize', () => {
      this._cols = process.stdout.columns || 80;
      this._rows = process.stdout.rows || 24;
      this._fullRender();
    });

    this._setupInput();
    this._fullRender();
  }

  // --- ANSI helpers ---
  _write(s) { process.stdout.write(s); }
  _goto(row, col) { this._write(`${ESC}${row};${col}H`); }
  _clear() { this._write(`${ESC}2J${ESC}H`); }
  _clearLine() { this._write(`${ESC}2K`); }
  _inverse(s) { return `${ESC}7m${s}${ESC}0m`; }
  _dim(s) { return `${ESC}2m${s}${ESC}0m`; }
  _bold(s) { return `${ESC}1m${s}${ESC}0m`; }
  _hideCursor() { this._write(`${ESC}?25l`); }
  _showCursor() { this._write(`${ESC}?25h`); }

  // --- Full screen render ---
  _fullRender() {
    this._hideCursor();
    this._clear();

    const w = this._cols;
    const h = this._rows;
    const contactW = Math.max(16, Math.floor(w * 0.25));
    const chatW = w - contactW;

    // Row 1: status bar
    this._goto(1, 1);
    const statusText = ` rede :: ${this._status}`;
    this._write(this._inverse(statusText.padEnd(w)));

    // Rows 2 to h-2: contacts (left) + chat (right)
    const bodyH = h - 3;
    for (let r = 0; r < bodyH; r++) {
      const row = r + 2;
      this._goto(row, 1);

      // Contact column
      let contactLine = '';
      if (r < this._contacts.length) {
        const name = this._contacts[r].slice(0, contactW - 3);
        if (this._mode === 'contacts' && r === this._contactIndex) {
          contactLine = this._inverse(` ${name} `.padEnd(contactW - 1));
        } else {
          contactLine = ` ${name}`.padEnd(contactW - 1);
        }
      } else {
        contactLine = ' '.repeat(contactW - 1);
      }
      this._write(contactLine);
      this._write(this._dim('|'));

      // Chat column
      const chatIdx = this._chatLines.length - bodyH + r;
      if (chatIdx >= 0 && chatIdx < this._chatLines.length) {
        this._write(' ' + this._stripAnsi(this._chatLines[chatIdx]).slice(0, chatW - 2));
      }
    }

    // Separator line
    this._goto(h - 1, 1);
    this._write(this._dim('-'.repeat(w)));

    // Input line
    this._renderInputLine();
  }

  _renderInputLine() {
    const h = this._rows;
    this._goto(h, 1);
    this._clearLine();
    const prefix = this._mode === 'contacts' ? this._dim('[contacts] ') : '> ';
    const cursor = this._mode === 'input' ? '_' : '';
    this._write(prefix + this._inputText + cursor);
  }

  _stripAnsi(s) {
    return s.replace(/\x1b\[[0-9;]*m/g, '').replace(/\{[^}]*\}/g, '');
  }

  // --- Input handling ---
  _setupInput() {
    const stdin = process.stdin;
    // Remove any prior listeners to avoid conflicts with passphrase prompt
    stdin.removeAllListeners('data');
    stdin.removeAllListeners('keypress');
    if (stdin.isTTY) stdin.setRawMode(true);
    stdin.setEncoding('utf8');
    stdin.resume();

    stdin.on('data', (buf) => {
      const data = buf.toString('utf8');

      for (let i = 0; i < data.length; i++) {
        const code = data.charCodeAt(i);

        // Ctrl+C
        if (code === 3) {
          this._clear();
          this._showCursor();
          this._goto(1, 1);
          if (this.onCommand) this.onCommand('quit');
          process.exit(0);
        }

        // Tab
        if (code === 9) {
          this._mode = this._mode === 'input' ? 'contacts' : 'input';
          this._fullRender();
          continue;
        }

        // ESC sequence
        if (code === 27 && i + 2 < data.length && data.charCodeAt(i + 1) === 91) {
          const arrow = data.charCodeAt(i + 2);
          i += 2;
          if (this._mode === 'contacts') {
            if (arrow === 65 && this._contactIndex > 0) { this._contactIndex--; this._fullRender(); }
            if (arrow === 66 && this._contactIndex < this._contacts.length - 1) { this._contactIndex++; this._fullRender(); }
          }
          continue;
        }
        if (code === 27) continue;

        // Contact mode
        if (this._mode === 'contacts') {
          if (code === 13 || code === 10) {
            if (this._contacts[this._contactIndex] && this.onCommand) {
              this.onCommand('select', this._contacts[this._contactIndex]);
            }
            this._mode = 'input';
            this._fullRender();
          }
          if (data[i] === 'k' && this._contactIndex > 0) { this._contactIndex--; this._fullRender(); }
          if (data[i] === 'j' && this._contactIndex < this._contacts.length - 1) { this._contactIndex++; this._fullRender(); }
          continue;
        }

        // Input mode — Enter
        if (code === 13 || code === 10) {
          const text = this._inputText.trim();
          this._inputText = '';
          this._renderInputLine();
          if (!text) continue;

          if (text.startsWith('/')) {
            const parts = text.slice(1).split(/\s+/);
            if (this.onCommand) this.onCommand(parts[0], ...parts.slice(1));
          } else {
            if (this.onMessage) this.onMessage(text);
          }
          continue;
        }

        // Backspace
        if (code === 127 || code === 8) {
          this._inputText = this._inputText.slice(0, -1);
          this._renderInputLine();
          continue;
        }

        // Ignore control chars
        if (code < 32) continue;

        // Normal character
        this._inputText += data[i];
        this._renderInputLine();
      }
    });
  }

  // --- Public API (same as before) ---

  setStatus(text) {
    this._status = text;
    const w = this._cols;
    this._goto(1, 1);
    const statusText = ` rede :: ${this._status}`;
    this._write(this._inverse(statusText.padEnd(w)));
    this._renderInputLine();
  }

  updateContacts(contacts) {
    this._contacts = contacts;
    if (this._contactIndex >= contacts.length) this._contactIndex = Math.max(0, contacts.length - 1);
    this._fullRender();
  }

  setCurrentChat(name) {
    this.currentChat = name;
  }

  clearChat() {
    this._chatLines = [];
    this._fullRender();
  }

  addChatLine(line) {
    this._chatLines.push(line);
    // Keep max 500 lines
    if (this._chatLines.length > 500) this._chatLines = this._chatLines.slice(-500);
    this._fullRender();
  }

  showSystemMessage(text) {
    this.addChatLine(`> ${text}`);
  }

  showHelp() {
    const help = [
      'commands:',
      '  /add <id#xxxx>       add contact',
      '  /confirm <id#xxxx>   accept key change',
      '  /reset <id#xxxx>     reset ratchet session',
      '  /fingerprint [user]  show fingerprint',
      '  /group <name>        create group',
      '  /ginvite <grp> <usr> invite to group',
      '  /kick <grp> <usr>    remove from group',
      '  /rekey <group>       rotate group key',
      '  /ttl <seconds>       self-destruct (0=off)',
      '  /contacts            list contacts',
      '  /groups              list groups',
      '  /key                 show your public key',
      '  /help                show this',
      '  /quit                exit',
      '',
      'tab = switch focus | ctrl+c = quit',
    ];
    for (const line of help) this.addChatLine(line);
  }

  render() {
    // compat
  }
}

module.exports = { RedeUI };
