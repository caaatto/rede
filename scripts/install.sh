#!/usr/bin/env bash
set -euo pipefail

# Rede Desktop — Download Installer (Linux)
#
# Downloads the prebuilt, self-contained REDE binary from GitHub Releases,
# verifies its Ed25519 signature against the release signing key, installs it
# to ~/.local/bin, and registers an app icon + .desktop entry.
#
# No .NET SDK, no git, no build step. The app auto-updates itself afterwards,
# so this script normally only needs to run once.
#
# Usage:
#   curl -sSL https://raw.githubusercontent.com/caaatto/rede/main/scripts/install.sh | bash
#
# Options (flags when run as a file, or env vars when piped):
#   --version vX.Y.Z   REDE_VERSION=vX.Y.Z   install a specific release tag
#   --prefix DIR       REDE_PREFIX=DIR       install root (default ~/.local)
#   --no-verify        REDE_NO_VERIFY=1      skip signature check (NOT recommended)
#   --uninstall                              remove an existing install

REPO="caaatto/rede"
# Ed25519 release signing public key (base64) — mirrors UpdateService.cs
PUBKEY_B64="SPON95u43RxzipArSW1Ntyk9eQ6hHCaf8UJlzOR+vas="
ICON_URL="https://raw.githubusercontent.com/${REPO}/main/src/Rede.Desktop/Assets/icon.png"

VERSION="${REDE_VERSION:-}"
PREFIX="${REDE_PREFIX:-$HOME/.local}"
NO_VERIFY="${REDE_NO_VERIFY:-0}"
ACTION="install"

while [ $# -gt 0 ]; do
  case "$1" in
    --version) VERSION="$2"; shift 2 ;;
    --prefix)  PREFIX="$2";  shift 2 ;;
    --no-verify) NO_VERIFY=1; shift ;;
    --uninstall) ACTION="uninstall"; shift ;;
    -h|--help) grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "[!] Unknown option: $1" >&2; exit 1 ;;
  esac
done

BIN_DIR="$PREFIX/bin"
ICON_DIR="$PREFIX/share/icons/hicolor/256x256/apps"
DESKTOP_DIR="$PREFIX/share/applications"
BIN_PATH="$BIN_DIR/REDE"

say()  { printf '%s\n' "$*"; }
info() { printf '[*] %s\n' "$*"; }
ok()   { printf '[+] %s\n' "$*"; }
err()  { printf '[!] %s\n' "$*" >&2; }

# ---------------------------------------------------------------- uninstall ---
if [ "$ACTION" = "uninstall" ]; then
  info "Removing Rede..."
  rm -f "$BIN_PATH" "$BIN_DIR/rede" \
        "$DESKTOP_DIR/rede.desktop" \
        "$ICON_DIR/rede.png"
  update-desktop-database "$DESKTOP_DIR" 2>/dev/null || true
  gtk-update-icon-cache "$PREFIX/share/icons/hicolor" 2>/dev/null || true
  ok "Uninstalled. (Your profile in ~/.rede was left untouched.)"
  exit 0
fi

# ----------------------------------------------------------------- preflight ---
ARCH="$(uname -m)"
if [ "$ARCH" != "x86_64" ]; then
  err "Only linux-x64 prebuilt binaries are published (found: $ARCH)."
  err "Build from source instead: scripts/install-from-source.sh"
  exit 1
fi

for cmd in curl base64 sha256sum; do
  command -v "$cmd" >/dev/null 2>&1 || { err "Missing required tool: $cmd"; exit 1; }
done

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# ------------------------------------------------------------ resolve version ---
# All releases are marked --prerelease (beta), so /releases/latest returns 404.
# Take the most recent tag from the releases list instead.
if [ -z "$VERSION" ]; then
  info "Resolving latest release..."
  VERSION="$(curl -fsSL "https://api.github.com/repos/${REPO}/releases" \
              | grep -m1 '"tag_name":' | sed -E 's/.*"tag_name": *"([^"]+)".*/\1/')"
  [ -n "$VERSION" ] || { err "Could not determine latest release tag."; exit 1; }
fi
ok "Release: $VERSION"

BASE="https://github.com/${REPO}/releases/download/${VERSION}"

# ---------------------------------------------------------------- download ---
info "Downloading REDE ($VERSION, linux-x64)..."
curl -fSL --progress-bar -o "$TMP/REDE"        "$BASE/REDE"
curl -fsSL                -o "$TMP/REDE.sig"    "$BASE/REDE.sig"     || true
curl -fsSL                -o "$TMP/SHA256SUMS"  "$BASE/SHA256SUMS"   || true

# ---------------------------------------------------------------- verify ----
verify_ed25519() {
  # $1=file  $2=sig(base64)  -> 0 ok, 1 bad, 2 no-verifier
  local file="$1" sig_b64="$2"
  local der="$TMP/pub.der" pem="$TMP/pub.pem" sig="$TMP/sig.bin"
  # SubjectPublicKeyInfo DER prefix for an Ed25519 raw public key + the 32 key bytes
  printf '\x30\x2a\x30\x05\x06\x03\x2b\x65\x70\x03\x21\x00' > "$der"
  printf '%s' "$PUBKEY_B64" | base64 -d >> "$der"
  printf '%s' "$sig_b64" | base64 -d > "$sig"

  if command -v openssl >/dev/null 2>&1 \
     && openssl pkey -pubin -inform DER -in "$der" -out "$pem" 2>/dev/null \
     && openssl pkeyutl -verify -pubin -inkey "$pem" -rawin -in "$file" -sigfile "$sig" >/dev/null 2>&1; then
    return 0
  fi

  if command -v python3 >/dev/null 2>&1; then
    python3 - "$PUBKEY_B64" "$sig" "$file" <<'PY'
import sys, base64
try:
    from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey
except Exception:
    sys.exit(2)
pub  = base64.b64decode(sys.argv[1])
sig  = open(sys.argv[2], 'rb').read()
data = open(sys.argv[3], 'rb').read()
try:
    Ed25519PublicKey.from_public_bytes(pub).verify(sig, data)
except Exception:
    sys.exit(1)
sys.exit(0)
PY
    return $?
  fi
  return 2
}

if [ "$NO_VERIFY" = "1" ]; then
  err "Signature verification SKIPPED (--no-verify). You are trusting the network."
else
  [ -s "$TMP/REDE.sig" ] || { err "No REDE.sig in release — refusing to install. Use --no-verify to override."; exit 1; }
  info "Verifying Ed25519 signature..."
  set +e
  verify_ed25519 "$TMP/REDE" "$(cat "$TMP/REDE.sig")"
  rc=$?
  set -e
  case "$rc" in
    0) ok "Signature valid." ;;
    1) err "SIGNATURE INVALID — binary does not match the release signing key. Aborting."; exit 1 ;;
    2) err "No Ed25519 verifier available (need openssl 3.0+ or python3-cryptography)."
       err "Install one, or re-run with --no-verify to bypass (not recommended)."; exit 1 ;;
  esac

  # Defense in depth: also check the published SHA256SUMS entry if present.
  if [ -s "$TMP/SHA256SUMS" ] && grep -q ' REDE$' "$TMP/SHA256SUMS"; then
    ( cd "$TMP" && grep ' REDE$' SHA256SUMS | sha256sum -c - >/dev/null 2>&1 ) \
      && ok "SHA256 checksum matches." \
      || { err "SHA256SUMS mismatch. Aborting."; exit 1; }
  fi
fi

# ---------------------------------------------------------------- install ---
info "Installing to $BIN_PATH"
mkdir -p "$BIN_DIR"
install -m 755 "$TMP/REDE" "$BIN_PATH"
ln -sf "$BIN_PATH" "$BIN_DIR/rede"   # lowercase convenience alias

# Icon (best effort — not in the release, fetched from the repo)
mkdir -p "$ICON_DIR"
if curl -fsSL -o "$ICON_DIR/rede.png" "$ICON_URL" 2>/dev/null; then
  ok "Installed app icon."
else
  err "Could not fetch app icon (non-fatal)."
fi

# Desktop entry
mkdir -p "$DESKTOP_DIR"
cat > "$DESKTOP_DIR/rede.desktop" <<DESKTOP
[Desktop Entry]
Name=REDE
GenericName=Secure Messenger
Comment=Secure, anonymous E2EE messenger
Exec=$BIN_PATH %u
Icon=rede
Terminal=false
Type=Application
Categories=Network;Chat;InstantMessaging;
Keywords=messenger;encrypted;e2ee;secure;anonymous;
StartupWMClass=Rede.Desktop
MimeType=x-scheme-handler/rede;
DESKTOP
update-desktop-database "$DESKTOP_DIR" 2>/dev/null || true
gtk-update-icon-cache "$PREFIX/share/icons/hicolor" 2>/dev/null || true

# ---------------------------------------------------------------- done ------
say ""
ok "Rede $VERSION installed."
say "    Binary:   $BIN_PATH"
say "    Launch:   rede   (or find 'REDE' in your app menu)"
say "    Update:   automatic — the app updates itself in place"
say "    Remove:   $0 --uninstall"
say ""
if [[ ":$PATH:" != *":$BIN_DIR:"* ]]; then
  err "$BIN_DIR is not in your PATH. Add to ~/.bashrc or ~/.zshrc:"
  say "      export PATH=\"$BIN_DIR:\$PATH\""
fi
