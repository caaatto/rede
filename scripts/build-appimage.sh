#!/usr/bin/env bash
set -euo pipefail

# Rede Desktop — AppImage builder (linux-x64)
#
# Wraps the self-contained REDE binary into a portable .AppImage that runs by
# double-click on any glibc desktop, with an app-menu entry and icon once
# integrated. The app's self-update is AppImage-aware (UpdateService.cs reads
# $APPIMAGE), so an AppImage keeps updating itself in place.
#
# Usage:
#   scripts/build-appimage.sh [path/to/REDE]
#
# Default input:  publish/linux-x64/REDE  (run `dotnet publish` first — see CLAUDE.md)
# Output:         publish/REDE-x86_64.AppImage

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BIN="${1:-$ROOT/publish/linux-x64/REDE}"
ICON="$ROOT/src/Rede.Desktop/Assets/icon.png"
OUT_DIR="$ROOT/publish"
OUT="$OUT_DIR/REDE-x86_64.AppImage"
TOOL_CACHE="${XDG_CACHE_HOME:-$HOME/.cache}/rede-appimagetool"

info() { printf '[*] %s\n' "$*"; }
ok()   { printf '[+] %s\n' "$*"; }
err()  { printf '[!] %s\n' "$*" >&2; }

[ -f "$BIN" ]  || { err "REDE binary not found: $BIN"; err "Publish it first (see CLAUDE.md Release Process), or pass the path as arg 1."; exit 1; }
[ -f "$ICON" ] || { err "Icon not found: $ICON"; exit 1; }

# ----------------------------------------------------------- appimagetool ---
TOOL="$TOOL_CACHE/appimagetool-x86_64.AppImage"
if [ ! -x "$TOOL" ]; then
  info "Fetching appimagetool..."
  mkdir -p "$TOOL_CACHE"
  curl -fSL --progress-bar -o "$TOOL" \
    "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"
  chmod +x "$TOOL"
fi

# ----------------------------------------------------------------- AppDir ---
APPDIR="$(mktemp -d)/REDE.AppDir"
trap 'rm -rf "$(dirname "$APPDIR")"' EXIT
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/share/icons/hicolor/256x256/apps"

info "Assembling AppDir..."
install -m 755 "$BIN" "$APPDIR/usr/bin/REDE"
cp "$ICON" "$APPDIR/usr/share/icons/hicolor/256x256/apps/rede.png"
cp "$ICON" "$APPDIR/rede.png"            # appimagetool wants the icon at the root too

# AppRun — launch the bundled binary, forwarding args (e.g. rede:// URLs)
cat > "$APPDIR/AppRun" <<'APPRUN'
#!/usr/bin/env bash
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/REDE" "$@"
APPRUN
chmod +x "$APPDIR/AppRun"

# Desktop entry (lives at AppDir root, the source for menu integration)
cat > "$APPDIR/rede.desktop" <<'DESKTOP'
[Desktop Entry]
Name=REDE
GenericName=Secure Messenger
Comment=Secure, anonymous E2EE messenger
Exec=REDE %u
Icon=rede
Terminal=false
Type=Application
Categories=Network;Chat;InstantMessaging;
Keywords=messenger;encrypted;e2ee;secure;anonymous;
StartupWMClass=Rede.Desktop
MimeType=x-scheme-handler/rede;
DESKTOP

# ----------------------------------------------------------------- build ----
mkdir -p "$OUT_DIR"
info "Building AppImage..."
# --appimage-extract-and-run avoids needing FUSE on the build host.
ARCH=x86_64 "$TOOL" --appimage-extract-and-run "$APPDIR" "$OUT"
chmod +x "$OUT"

ok "Built: $OUT  ($(du -h "$OUT" | cut -f1))"
say_sig() { printf '    Sign it for release:  scripts/sign-release.sh %s\n' "$OUT"; }
say_sig
