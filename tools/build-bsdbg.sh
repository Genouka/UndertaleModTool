#!/usr/bin/env bash
# Builds the ButterscotchDbg embedded runner (libbsdbg.so / libbsdbg.dylib) for
# Linux and macOS, mirroring tools/build-bsdbg.ps1 on Windows.
#
# Requirements:
#   - cmake, ninja (or make), a C compiler
#   - BACKEND=sdl2 (default) needs SDL2 development headers (sdl2-config or
#     pkg-config); BACKEND=noop builds headless with no SDL2 dependency
#   - BACKEND=sdl3 is experimental and needs SDL3 development files
#
# Usage: tools/build-bsdbg.sh [--backend sdl2|noop|sdl3] [--config Release|Debug]
#                            [--clean]
#
# The result is staged into UndertaleModRunner/native/<rid>/ where <rid> is
# linux-x64, linux-arm64, osx-x64 or osx-arm64.
#
# Android is not covered: Butterscotch's PLATFORM=android is a JNI front end that
# does not compile src/loop.c, so the embedded runner cannot be produced from this
# tree yet. To use the embedded runner on Android, stage a libbsdbg.so built
# against an ANativeWindow backend under UndertaleModRunner/native/android-<abi>/;
# the Android project packs it into the APK and ExternalButterscotch loads it.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
BUTTERSCOTCH_DIR="$REPO_ROOT/Butterscotch"
RUNNER_DIR="$REPO_ROOT/UndertaleModRunner"

BACKEND="sdl2"
CONFIG="Release"
CLEAN=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --backend) BACKEND="$2"; shift 2 ;;
        --config)  CONFIG="$2"; shift 2 ;;
        --clean)   CLEAN=1; shift ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

case "$(uname -s)-$(uname -m)" in
    Linux-x86_64)  RID="linux-x64" ;;
    Linux-aarch64) RID="linux-arm64" ;;
    Linux-armv7l)  RID="linux-arm" ;;
    Darwin-x86_64) RID="osx-x64" ;;
    Darwin-arm64)  RID="osx-arm64" ;;
    *) echo "Unsupported platform: $(uname -s)-$(uname -m)" >&2; exit 1 ;;
esac

BUILD_DIR="$REPO_ROOT/build/bsdbg-$RID"
OUT_DIR="$RUNNER_DIR/native/$RID"

if [[ ! -f "$BUTTERSCOTCH_DIR/CMakeLists.txt" ]]; then
    echo "==> Initializing Butterscotch submodule"
    git -C "$REPO_ROOT" submodule update --init Butterscotch
fi

if [[ $CLEAN -eq 1 && -d "$BUILD_DIR" ]]; then
    echo "==> Removing $BUILD_DIR"
    rm -rf "$BUILD_DIR"
fi

# A cached configure from another backend carries FORCE'd renderer options, so
# start over when the backend changed.
if [[ -f "$BUILD_DIR/CMakeCache.txt" ]]; then
    cachedPlatform="$(grep -E '^PLATFORM:' "$BUILD_DIR/CMakeCache.txt" | head -1 | cut -d= -f2- || true)"
    cachedBackend="$(grep -E '^BACKEND:' "$BUILD_DIR/CMakeCache.txt" | head -1 | cut -d= -f2- || true)"
    if [[ "$cachedPlatform" != "embedded" || "$cachedBackend" != "$BACKEND" ]]; then
        echo "==> Build directory configured for a different platform/backend; removing $BUILD_DIR"
        rm -rf "$BUILD_DIR"
    fi
fi

echo "==> [$RID] Configuring ($BACKEND, $CONFIG)"
GENERATOR="Ninja"
if ! command -v ninja >/dev/null 2>&1; then
    GENERATOR="Unix Makefiles"
fi
cmake -S "$BUTTERSCOTCH_DIR" -B "$BUILD_DIR" -G "$GENERATOR" \
    -DPLATFORM=embedded \
    -DBACKEND="$BACKEND" \
    -DCMAKE_BUILD_TYPE="$CONFIG" \
    -DVIDEO_BACKEND=none \
    -DENABLE_FMOD=OFF

echo "==> [$RID] Building"
cmake --build "$BUILD_DIR" --parallel "$(nproc 2>/dev/null || sysctl -n hw.ncpu 2>/dev/null || echo 4)"

LIB_NAME="libbsdbg.so"
if [[ "$(uname -s)" == "Darwin" ]]; then
    LIB_NAME="libbsdbg.dylib"
fi

BUILT_LIB="$(find "$BUILD_DIR" -name "$LIB_NAME" -type f | head -1)"
if [[ -z "$BUILT_LIB" ]]; then
    echo "ERROR: $LIB_NAME was not produced under $BUILD_DIR" >&2
    exit 1
fi

mkdir -p "$OUT_DIR"
cp -f "$BUILT_LIB" "$OUT_DIR/$LIB_NAME"
touch "$OUT_DIR/$LIB_NAME"
echo "==> [$RID] Staged $BUILT_LIB -> $OUT_DIR"

# Stage a private SDL2 runtime next to the library when one is available, so the
# embedded runner does not depend on the system package at runtime. libbsdbg.so
# carries an $ORIGIN RPATH for exactly this layout.
if [[ "$BACKEND" == "sdl2" && "$(uname -s)" == "Linux" ]]; then
    SDL_RUNTIME="$(ldd "$BUILT_LIB" | awk '/libSDL2(-2\.0)?\.so/ {print $3; exit}')"
    if [[ -n "${SDL_RUNTIME:-}" && -f "$SDL_RUNTIME" ]]; then
        cp -f "$SDL_RUNTIME" "$OUT_DIR/libSDL2-2.0.so.0"
        touch "$OUT_DIR/libSDL2-2.0.so.0"
        echo "==> [$RID] Staged SDL2 runtime $SDL_RUNTIME -> $OUT_DIR"
    fi
fi

echo "Build succeeded ($RID): $OUT_DIR/$LIB_NAME"
