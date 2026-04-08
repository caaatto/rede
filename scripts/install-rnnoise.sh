#!/usr/bin/env bash
set -euo pipefail

# Install RNNoise noise suppression for REDE (Linux)
# Downloads or builds librnnoise.so and places it in ~/.rede/libs/

LIBS_DIR="$HOME/.rede/libs"
LIB_FILE="$LIBS_DIR/librnnoise.so"

if [ -f "$LIB_FILE" ]; then
    echo "[*] RNNoise already installed at $LIB_FILE"
    exit 0
fi

mkdir -p "$LIBS_DIR"

# Try system package first
if ldconfig -p 2>/dev/null | grep -q librnnoise; then
    SYS_LIB="$(ldconfig -p | grep librnnoise | head -1 | awk '{print $NF}')"
    cp "$SYS_LIB" "$LIB_FILE"
    echo "[+] Copied system librnnoise from $SYS_LIB"
    exit 0
fi

# Try apt/dnf install
if command -v apt-get &>/dev/null; then
    echo "[*] Installing librnnoise via apt..."
    sudo apt-get install -y librnnoise0 || sudo apt-get install -y librnnoise-dev || true
    if ldconfig -p 2>/dev/null | grep -q librnnoise; then
        SYS_LIB="$(ldconfig -p | grep librnnoise | head -1 | awk '{print $NF}')"
        cp "$SYS_LIB" "$LIB_FILE"
        echo "[+] Installed and copied librnnoise"
        exit 0
    fi
elif command -v dnf &>/dev/null; then
    echo "[*] Installing librnnoise via dnf..."
    sudo dnf install -y rnnoise || true
    if ldconfig -p 2>/dev/null | grep -q librnnoise; then
        SYS_LIB="$(ldconfig -p | grep librnnoise | head -1 | awk '{print $NF}')"
        cp "$SYS_LIB" "$LIB_FILE"
        echo "[+] Installed and copied librnnoise"
        exit 0
    fi
fi

# Build from source as fallback
echo "[*] Building RNNoise from source..."
TMPDIR="$(mktemp -d)"
trap "rm -rf '$TMPDIR'" EXIT

cd "$TMPDIR"
git clone --depth 1 https://github.com/xiph/rnnoise.git
cd rnnoise
./autogen.sh
./configure --disable-examples --disable-doc
make -j"$(nproc)"
cp .libs/librnnoise.so "$LIB_FILE"

echo "[+] RNNoise installed to $LIB_FILE"
echo "    Restart REDE to enable noise suppression."
