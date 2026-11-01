#!/usr/bin/env bash
# Cross-build A2Core.Redirect.dll for win-x64 on Linux (mingw-w64). Writes to
# $REPO/build/a2core-redirect-mingw/A2Core.Redirect.dll by default.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$ROOT/../.." && pwd)"
BUILD="${A2CORE_REDIRECT_BUILD:-$REPO/build/a2core-redirect-mingw}"
TOOLCHAIN="$ROOT/toolchain-mingw-w64.cmake"

command -v cmake >/dev/null || { echo "cmake missing" >&2; exit 1; }
command -v "${MINGW_PREFIX:-x86_64-w64-mingw32}-g++" >/dev/null || { echo "mingw-w64 g++ missing (install mingw-w64)" >&2; exit 1; }
command -v git >/dev/null || { echo "git missing (MinHook FetchContent)" >&2; exit 1; }

cmake -S "$ROOT" -B "$BUILD" -DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN" -DCMAKE_BUILD_TYPE=Release
cmake --build "$BUILD" -j"$(nproc)"

DLL="$BUILD/A2Core.Redirect.dll"
[[ -f "$DLL" ]] || { echo "Build did not produce $DLL" >&2; exit 1; }

OBJDUMP="${MINGW_PREFIX:-x86_64-w64-mingw32}-objdump"
if command -v "$OBJDUMP" >/dev/null; then
  pe_info="$("$OBJDUMP" -f "$DLL")"
  [[ "$pe_info" == *PE32+* || "$pe_info" == *pei-x86-64* ]] || { echo "Not a PE32+ x64 DLL: $DLL" >&2; exit 1; }
  imports="$("$OBJDUMP" -p "$DLL")"
  shopt -s nocasematch
  [[ "$imports" == *ws2_32.dll* ]] || { echo "ws2_32 import missing on $DLL" >&2; exit 1; }
  shopt -u nocasematch
  if command -v "${MINGW_PREFIX:-x86_64-w64-mingw32}-nm" >/dev/null; then
    symbols="$("${MINGW_PREFIX:-x86_64-w64-mingw32}-nm" "$DLL")"
    [[ "$symbols" == *' T DllMain'* ]] || { echo "DllMain missing on $DLL" >&2; exit 1; }
  fi
fi

echo "A2Core.Redirect.dll -> $DLL"
