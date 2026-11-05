#!/bin/sh
# Builds the native plugin for Windows x64 and macOS (arm64 + x86_64 → universal) with the Zig toolchain
# (no Visual Studio / Xcode needed). Usage: ZIG=/path/to/zig ./build-zig.sh
set -e
cd "$(dirname "$0")"
ZIG="${ZIG:-zig}"
OUT=../Plugins
SRC=src/smartcolliders_native.cpp
FLAGS="-std=c++17 -O2 -fno-sanitize=undefined -Ithird_party/vhacd -shared"
mkdir -p build "$OUT/Windows/x86_64" "$OUT/macOS"

"$ZIG" c++ $FLAGS -target x86_64-windows-gnu -static-libgcc $SRC -o build/smartcolliders_native.dll
cp build/smartcolliders_native.dll "$OUT/Windows/x86_64/smartcolliders_native.dll"

"$ZIG" c++ $FLAGS -target aarch64-macos $SRC -o build/smartcolliders_native_arm64.dylib
"$ZIG" c++ $FLAGS -target x86_64-macos $SRC -o build/smartcolliders_native_x64.dylib
echo "built: build/*"
# Universal macOS binary (no lipo needed).
powershell -NoProfile -ExecutionPolicy Bypass -Command \
  "& ./make-universal.ps1 -Out '$OUT/macOS/smartcolliders_native.dylib' -Inputs 'build/smartcolliders_native_arm64.dylib','build/smartcolliders_native_x64.dylib'"
