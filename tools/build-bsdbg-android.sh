#!/usr/bin/env bash
# Builds the ButterscotchDbg embedded runner (libbsdbg.so) for Android with the
# NDK, and stages it under UndertaleModRunner/native/android-<abi>/ so the
# Android project packs it into the APK.
#
# The engine's PLATFORM=android is a JNI front end without src/loop.c, so the
# embedded runner uses PLATFORM=embedded with the android_egl backend: the host
# hands it the SurfaceView's android.view.Surface through
# platformSetEmbeddedWindow() and the backend renders into it with EGL/GLES3.
#
# Requirements:
#   - Android NDK r26+ (ANDROID_NDK_ROOT, or the newest one under
#     $LOCALAPPDATA/Android/Sdk/ndk)
#   - cmake and ninja on PATH
#
# Usage: tools/build-bsdbg-android.sh [--abi arm64-v8a|x86_64|all] [--config Release|Debug]

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
BUTTERSCOTCH_DIR="$REPO_ROOT/Butterscotch"
RUNNER_DIR="$REPO_ROOT/UndertaleModRunner"

ABI="all"
CONFIG="Release"
CLEAN=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --abi)    ABI="$2"; shift 2 ;;
        --config) CONFIG="$2"; shift 2 ;;
        --clean)  CLEAN=1; shift ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

# The Android SDK lives under a per-user profile path. Environment variables are
# not always inherited (a WSL shell has none of the Windows ones), so probe the
# usual locations: the Windows env vars, $HOME, and the mounted drive roots.
ndk_bases=()
[[ -n "${ANDROID_NDK_ROOT:-}" ]] && ndk_bases+=("$ANDROID_NDK_ROOT")
for base in \
    "${LOCALAPPDATA:-}/Android/Sdk/ndk" \
    "${USERPROFILE:-}/AppData/Local/Android/Sdk/ndk" \
    "$HOME/AppData/Local/Android/Sdk/ndk" \
    /mnt/c/Users/*/AppData/Local/Android/Sdk/ndk \
    /c/Users/*/AppData/Local/Android/Sdk/ndk; do
    [[ -d "$base" ]] && ndk_bases+=("$base")
done

if [[ -z "${ANDROID_NDK_ROOT:-}" ]]; then
    for base in "${ndk_bases[@]}"; do
        if [[ -f "$base/build/cmake/android.toolchain.cmake" ]]; then
            ANDROID_NDK_ROOT="$base"
            break
        fi
        # Prefer the version the committed binaries were built with, then newest.
        if [[ -d "$base/27.2.12479018" ]]; then
            ANDROID_NDK_ROOT="$base/27.2.12479018"
            break
        fi
        candidate="$(ls -d "$base"/* 2>/dev/null | sort -V | tail -1 || true)"
        if [[ -n "$candidate" ]]; then
            ANDROID_NDK_ROOT="$candidate"
            break
        fi
    done
fi
if [[ -z "${ANDROID_NDK_ROOT:-}" || ! -f "$ANDROID_NDK_ROOT/build/cmake/android.toolchain.cmake" ]]; then
    echo "ERROR: Android NDK not found. Set ANDROID_NDK_ROOT." >&2
    exit 1
fi
echo "==> Using NDK $ANDROID_NDK_ROOT"

if [[ "$ABI" == "all" ]]; then
    ABIS=("arm64-v8a" "x86_64")
else
    ABIS=("$ABI")
fi

if [[ ! -f "$BUTTERSCOTCH_DIR/CMakeLists.txt" ]]; then
    echo "==> Initializing Butterscotch submodule"
    git -C "$REPO_ROOT" submodule update --init Butterscotch
fi

# Windows ships a drive-letter filesystem, so paths coming from the shell as
# /mnt/c or /c must be converted before a Windows CMake sees them. On a Linux
# host (CI) nothing is converted.
cmake_is_windows=0
to_cmake_path() {
    local path="$1" drive
    if [[ $cmake_is_windows -eq 1 ]] &&
       { [[ "$path" =~ ^/mnt/([a-zA-Z])/(.*)$ ]] || [[ "$path" =~ ^/([a-zA-Z])/(.*)$ ]]; }; then
        drive="${BASH_REMATCH[1]^^}"
        printf '%s:/%s\n' "$drive" "${BASH_REMATCH[2]}"
        return
    fi
    printf '%s\n' "$path"
}

CMAKE_BIN="${CMAKE_BIN:-}"
if [[ -z "$CMAKE_BIN" ]]; then
    for candidate in "$(command -v cmake.exe 2>/dev/null || true)" \
                     /c/Strawberry/c/bin/cmake.exe \
                     "/c/Program Files/CMake/bin/cmake.exe"; do
        if [[ -n "$candidate" && -x "$candidate" ]]; then
            CMAKE_BIN="$candidate"
            break
        fi
    done
fi
if [[ -z "$CMAKE_BIN" ]]; then
    CMAKE_BIN="$(command -v cmake 2>/dev/null || true)"
fi
if [[ -z "$CMAKE_BIN" ]]; then
    echo "ERROR: cmake was not found. Set CMAKE_BIN to its path." >&2
    exit 1
fi
case "$CMAKE_BIN" in
    *.exe) cmake_is_windows=1 ;;
esac
echo "==> Using cmake $CMAKE_BIN"

NINJA_BIN="${NINJA_BIN:-}"
if [[ -z "$NINJA_BIN" ]]; then
    for candidate in "$(command -v ninja.exe 2>/dev/null || true)" \
                     /c/Strawberry/c/bin/ninja.exe \
                     "$(command -v ninja 2>/dev/null || true)"; do
        if [[ -n "$candidate" && -x "$candidate" ]]; then
            NINJA_BIN="$candidate"
            break
        fi
    done
fi
if [[ -n "$NINJA_BIN" ]]; then
    echo "==> Using ninja $NINJA_BIN"
fi


NDK_TOOLCHAIN="$(to_cmake_path "$ANDROID_NDK_ROOT/build/cmake/android.toolchain.cmake")"

for abi in "${ABIS[@]}"; do
    case "$abi" in
        arm64-v8a) RID="android-arm64" ;;
        x86_64)    RID="android-x64" ;;
        *) echo "Unsupported ABI: $abi" >&2; exit 1 ;;
    esac

    BUILD_DIR="$REPO_ROOT/build/bsdbg-$RID"
    OUT_DIR="$RUNNER_DIR/native/$RID"
    if [[ $CLEAN -eq 1 && -d "$BUILD_DIR" ]]; then
        rm -rf "$BUILD_DIR"
    fi
    SRC_DIR="$(to_cmake_path "$BUTTERSCOTCH_DIR")"
    CMAKE_BUILD_DIR="$(to_cmake_path "$BUILD_DIR")"

    echo "==> [$abi] Configuring"
    make_program_arg=()
    if [[ -n "$NINJA_BIN" ]]; then
        make_program_arg=("-DCMAKE_MAKE_PROGRAM=$(to_cmake_path "$NINJA_BIN")")
    fi

    "$CMAKE_BIN" -S "$SRC_DIR" -B "$CMAKE_BUILD_DIR" -G Ninja \
        "${make_program_arg[@]}" \
        -DCMAKE_TOOLCHAIN_FILE="$NDK_TOOLCHAIN" \
        -DANDROID_ABI="$abi" \
        -DANDROID_PLATFORM=android-28 \
        -DPLATFORM=embedded \
        -DBACKEND=android_egl \
        -DCMAKE_BUILD_TYPE="$CONFIG" \
        -DVIDEO_BACKEND=none \
        -DENABLE_FMOD=OFF

    echo "==> [$abi] Building"
    "$CMAKE_BIN" --build "$CMAKE_BUILD_DIR" --parallel 8

    LIB="$BUILD_DIR/libbsdbg.so"
    if [[ ! -f "$LIB" ]]; then
        echo "ERROR: libbsdbg.so was not produced under $BUILD_DIR" >&2
        exit 1
    fi

    mkdir -p "$OUT_DIR"
    cp -f "$LIB" "$OUT_DIR/libbsdbg.so"

    # Strip debug info: the unstripped library is ~14 MB per ABI, which is too
    # much to ship inside the APK. This NDK ships Windows (or Linux) host tools,
    # so probe both layouts.
    STRIP=""
    for candidate in "$ANDROID_NDK_ROOT/toolchains/llvm/prebuilt/windows-x86_64/bin/llvm-strip.exe" \
                     "$ANDROID_NDK_ROOT/toolchains/llvm/prebuilt/linux-x86_64/bin/llvm-strip"; do
        if [[ -x "$candidate" ]]; then
            STRIP="$candidate"
            break
        fi
    done
    if [[ -n "$STRIP" ]]; then
        "$STRIP" --strip-unneeded "$(to_cmake_path "$OUT_DIR/libbsdbg.so")"
    else
        echo "==> [$abi] llvm-strip not found; shipping the unstripped library"
    fi

    echo "==> [$abi] Staged $OUT_DIR/libbsdbg.so ($(stat -c %s "$OUT_DIR/libbsdbg.so" 2>/dev/null || echo '?') bytes)"
done

echo "Build succeeded"
