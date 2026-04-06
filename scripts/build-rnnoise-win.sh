#!/bin/bash
# Cross-compile rnnoise.dll for Windows x64
# Requires: x86_64-w64-mingw32-gcc (apt install gcc-mingw-w64-x86-64)
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
BUILD_DIR="/tmp/rnnoise-build-win"
OUT_DIR="$SCRIPT_DIR/../src/Rede.Core/runtimes/win-x64/native"

rm -rf "$BUILD_DIR"
git clone --depth 1 https://github.com/xiph/rnnoise.git "$BUILD_DIR"

cd "$BUILD_DIR"
bash download_model.sh

cd src
x86_64-w64-mingw32-gcc -shared -O3 -I../include -I. \
  denoise.c kiss_fft.c pitch.c celt_lpc.c rnn.c nnet.c nnet_default.c \
  rnnoise_tables.c rnnoise_data.c parse_lpcnet_weights.c \
  -o rnnoise.dll -lm -static-libgcc
x86_64-w64-mingw32-strip rnnoise.dll

mkdir -p "$OUT_DIR"
cp rnnoise.dll "$OUT_DIR/"
echo "Built rnnoise.dll -> $OUT_DIR/rnnoise.dll ($(du -h "$OUT_DIR/rnnoise.dll" | cut -f1))"

rm -rf "$BUILD_DIR"
