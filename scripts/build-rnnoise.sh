#!/bin/bash
# Build librnnoise.so for noise suppression
# Requires: gcc, git, wget
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
BUILD_DIR="/tmp/rnnoise-build"
OUT_DIR="$SCRIPT_DIR/../src/Rede.Core/runtimes/linux-x64/native"

rm -rf "$BUILD_DIR"
git clone --depth 1 https://github.com/xiph/rnnoise.git "$BUILD_DIR"

cd "$BUILD_DIR"
bash download_model.sh

cd src
gcc -shared -fPIC -O3 -march=native -I../include -I. \
  denoise.c kiss_fft.c pitch.c celt_lpc.c rnn.c nnet.c nnet_default.c \
  rnnoise_tables.c rnnoise_data.c parse_lpcnet_weights.c \
  x86/x86cpu.c x86/x86_dnn_map.c x86/nnet_avx2.c x86/nnet_sse4_1.c \
  -o librnnoise.so -lm

strip librnnoise.so

mkdir -p "$OUT_DIR"
cp librnnoise.so "$OUT_DIR/"
echo "Built librnnoise.so → $OUT_DIR/librnnoise.so ($(du -h "$OUT_DIR/librnnoise.so" | cut -f1))"

rm -rf "$BUILD_DIR"
