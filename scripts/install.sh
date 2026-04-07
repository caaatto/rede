#!/usr/bin/env bash
set -euo pipefail

# Rede Desktop — Install Script
# Clones the repo, builds the desktop client, and creates a launcher.

REPO_URL="git@github.com:caaatto/rede.git"
BRANCH="main"
INSTALL_DIR="${REDE_INSTALL_DIR:-$HOME/.local/share/rede}"
BIN_DIR="${HOME}/.local/bin"
DESKTOP_DIR="${HOME}/.local/share/applications"

echo "=== Rede Desktop Installer ==="
echo ""

# Check dependencies
for cmd in git dotnet; do
    if ! command -v "$cmd" &>/dev/null; then
        echo "[!] Missing dependency: $cmd"
        case "$cmd" in
            dotnet) echo "    Install .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0" ;;
            git)    echo "    Install git: sudo apt install git" ;;
        esac
        exit 1
    fi
done

# Check .NET version
DOTNET_VERSION=$(dotnet --version 2>/dev/null || echo "0")
if [[ "${DOTNET_VERSION%%.*}" -lt 8 ]]; then
    echo "[!] .NET 8+ required (found: $DOTNET_VERSION)"
    exit 1
fi

# Clone or update
if [ -d "$INSTALL_DIR/.git" ]; then
    echo "[*] Updating existing installation..."
    cd "$INSTALL_DIR"
    git fetch origin "$BRANCH"
    git checkout "$BRANCH"
    git pull origin "$BRANCH"
else
    echo "[*] Cloning repository..."
    git clone -b "$BRANCH" "$REPO_URL" "$INSTALL_DIR"
    cd "$INSTALL_DIR"
fi

# Install Node.js deps for v1 terminal client (if Node is available)
if command -v npm &>/dev/null; then
    echo "[*] Installing Node.js dependencies (v1 terminal client)..."
    npm install --silent 2>/dev/null || true
fi

# Build desktop client
echo "[*] Building desktop client..."
dotnet build "$INSTALL_DIR/Rede.sln" -c Release --nologo -v q

# Create launcher script
mkdir -p "$BIN_DIR"
cat > "$BIN_DIR/rede" << 'LAUNCHER'
#!/usr/bin/env bash
INSTALL_DIR="${REDE_INSTALL_DIR:-$HOME/.local/share/rede}"
cd "$INSTALL_DIR"
exec dotnet run --project src/Rede.Desktop -c Release --no-build -- "$@"
LAUNCHER
chmod +x "$BIN_DIR/rede"

# Install icon
ICON_DIR="${HOME}/.local/share/icons/hicolor/256x256/apps"
mkdir -p "$ICON_DIR"
cp "$INSTALL_DIR/src/Rede.Desktop/Assets/icon.png" "$ICON_DIR/rede.png" 2>/dev/null || true

# Create .desktop entry (Linux)
if [ -d "$DESKTOP_DIR" ] || mkdir -p "$DESKTOP_DIR"; then
    cat > "$DESKTOP_DIR/rede.desktop" << DESKTOP
[Desktop Entry]
Name=REDE
GenericName=Secure Messenger
Comment=Secure, anonymous E2EE messenger
Exec=$BIN_DIR/rede
Icon=rede
Terminal=false
Type=Application
Categories=Network;Chat;InstantMessaging;
Keywords=messenger;encrypted;e2ee;secure;anonymous;
StartupWMClass=Rede.Desktop
DESKTOP
    # Update desktop database if available
    update-desktop-database "$DESKTOP_DIR" 2>/dev/null || true
    gtk-update-icon-cache "$HOME/.local/share/icons/hicolor" 2>/dev/null || true
fi

echo ""
echo "=== Installation complete ==="
echo ""
echo "  Install dir:  $INSTALL_DIR"
echo "  Launcher:     $BIN_DIR/rede"
echo ""
echo "  Run with:     rede"
echo ""

# Check if BIN_DIR is in PATH
if [[ ":$PATH:" != *":$BIN_DIR:"* ]]; then
    echo "  [!] $BIN_DIR is not in your PATH."
    echo "      Add this to your ~/.bashrc or ~/.zshrc:"
    echo "      export PATH=\"\$HOME/.local/bin:\$PATH\""
    echo ""
fi
