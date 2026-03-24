#!/usr/bin/env bash
set -euo pipefail

# Rede Desktop — Build Release
# Creates a self-contained executable for distribution.

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
OUTPUT_DIR="$PROJECT_DIR/dist"

# Detect platform
case "$(uname -s)" in
    Linux*)  RID="linux-x64" ;;
    Darwin*) RID="osx-x64" ;;
    MINGW*|MSYS*|CYGWIN*) RID="win-x64" ;;
    *) echo "[!] Unknown platform: $(uname -s)"; exit 1 ;;
esac

# Allow override
RID="${REDE_RID:-$RID}"

echo "=== Building Rede Desktop ==="
echo "  Platform: $RID"
echo "  Output:   $OUTPUT_DIR/$RID/"
echo ""

cd "$PROJECT_DIR"

dotnet publish src/Rede.Desktop/Rede.Desktop.csproj \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:PublishTrimmed=false \
    -o "$OUTPUT_DIR/$RID/"

echo ""
echo "=== Build complete ==="
echo ""
ls -lh "$OUTPUT_DIR/$RID/Rede.Desktop"* 2>/dev/null || ls -lh "$OUTPUT_DIR/$RID/"
echo ""
echo "  Run: $OUTPUT_DIR/$RID/Rede.Desktop"
