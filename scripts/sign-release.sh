#!/usr/bin/env bash
# Sign release binaries with Ed25519 detached signatures.
# Usage: ./sign-release.sh <file> [<file2> ...]
# Requires: node, tweetnacl, tweetnacl-util
# Reads secret key from $REDE_SIGNING_KEY or /home/amke/Rede/.release-signing-key.secret

set -euo pipefail

KEY_FILE="/home/amke/Rede/.release-signing-key.secret"

if [ -n "${REDE_SIGNING_KEY:-}" ]; then
  SECRET_KEY="$REDE_SIGNING_KEY"
elif [ -f "$KEY_FILE" ]; then
  SECRET_KEY=$(cat "$KEY_FILE")
else
  echo "Error: No signing key found. Set REDE_SIGNING_KEY or create $KEY_FILE" >&2
  exit 1
fi

if [ $# -eq 0 ]; then
  echo "Usage: $0 <file> [<file2> ...]" >&2
  exit 1
fi

for FILE in "$@"; do
  if [ ! -f "$FILE" ]; then
    echo "File not found: $FILE" >&2
    exit 1
  fi

  SIG=$(node -e "
    const nacl = require('tweetnacl');
    const naclUtil = require('tweetnacl-util');
    const fs = require('fs');
    const sk = naclUtil.decodeBase64('$SECRET_KEY');
    const data = fs.readFileSync('$FILE');
    const sig = nacl.sign.detached(data, sk);
    console.log(naclUtil.encodeBase64(sig));
  ")

  echo "$SIG" > "${FILE}.sig"
  echo "Signed: ${FILE}.sig"
done
