#!/usr/bin/env bash
#
# Cross-build self-contained SDL2 / SDL2_image / SDL2_ttf DLLs for Windows (x64)
# from pinned upstream source, on Linux, using the MinGW-w64 toolchain. The DLLs
# are copied into Yafc/lib/windows.
#
# Why cross-compile on Linux instead of using a Windows runner:
#   * It is faster and cheaper on CI, and keeps everything except macOS on one
#     Linux runner.
#   * The Windows DLLs Yafc already ships (libfreetype-6.dll, libpng16-16.dll,
#     zlib1.dll, ...) use the MinGW/MSYS2 naming convention, so a MinGW build
#     matches the existing toolchain rather than switching to MSVC.
#
# Windows resolves DLLs by name from the application directory, so there is no
# install-name rewriting (that is a macOS-only problem, issue #659). Building
# from source is about provenance (issue #274): tagged upstream source + a known
# configuration instead of anonymous prebuilt binaries.
#
# The configuration matches build-macos.sh so behaviour is consistent:
#   * SDL2_image uses the built-in stb_image backend (PNG + JPG only).
#   * SDL2_ttf is VENDORED (static FreeType + HarfBuzz).
#   * libgcc / libstdc++ / winpthread are linked statically, so the DLLs do not
#     drag in libgcc_s / libstdc++-6 / libwinpthread-1 at runtime.
#
# Because of that, these three DLLs replace the old multi-DLL set: the previous
# libpng16-16.dll, libjpeg-9.dll, zlib1.dll and libfreetype-6.dll are folded in.
# lua52.dll is unrelated and left alone.
#
# Dependencies: cmake, ninja, curl, the mingw-w64 toolchain
# (x86_64-w64-mingw32-gcc/g++). On Ubuntu: apt-get install mingw-w64 cmake ninja-build.
#
# Usage: sdl/build-windows.sh   (override versions via the env vars below)

set -euo pipefail

# ---- Pinned upstream versions (kept in sync with build-macos.sh) -------------
SDL2_VERSION="${SDL2_VERSION:-2.32.10}"
SDL2_IMAGE_VERSION="${SDL2_IMAGE_VERSION:-2.8.12}"
SDL2_TTF_VERSION="${SDL2_TTF_VERSION:-2.24.0}"

SDL2_SHA256="${SDL2_SHA256:-5f5993c530f084535c65a6879e9b26ad441169b3e25d789d83287040a9ca5165}"
SDL2_IMAGE_SHA256="${SDL2_IMAGE_SHA256:-393f5efb50536ec13ca4f4affb69cc9966d3c3f969e6c5e701faddf9f9785381}"
SDL2_TTF_SHA256="${SDL2_TTF_SHA256:-0b2bf1e7b6568adbdbc9bb924643f79d9dedafe061fa1ed687d1d9ac4e453bfd}"

# MinGW target triple. Ubuntu ships x86_64-w64-mingw32-*.
HOST="${MINGW_HOST:-x86_64-w64-mingw32}"

# ---- Paths -------------------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
WORK="$SCRIPT_DIR/.build-windows"
PREFIX="$WORK/prefix"
WIN_DIR="$REPO_ROOT/Yafc/lib/windows"

# Heavy logging while we validate the pipeline; set SDL_BUILD_QUIET=1 to trim.
VERBOSE=1
[ -n "${SDL_BUILD_QUIET:-}" ] && VERBOSE=""

log() { echo "==> $*"; }

# Dump a PE's imported DLLs so a stray libgcc/libstdc++/libpng dependency is
# obvious in the CI log.
dump_pe() { # label file
  [ -n "${VERBOSE:-}" ] || return 0
  local label="$1" file="$2"
  echo "----- $label: $file"
  if [ ! -f "$file" ]; then echo "    (missing)"; return 0; fi
  echo "    file:    $(file -b "$file")"
  echo "    imports:"
  "${HOST}-objdump" -p "$file" 2>/dev/null | awk '/DLL Name:/ {print "      " $3}' | sort -u
}

fetch() { # url sha256 outfile
  local url="$1" sha="$2" out="$3"
  if [ ! -f "$out" ]; then
    log "Downloading $(basename "$out")"
    curl -fL -o "$out" "$url"
  else
    log "Using cached $(basename "$out")"
  fi
  local expected="${sha// /}" actual
  actual="$(sha256sum "$out" | cut -d' ' -f1)"
  if [ "$expected" != "$actual" ]; then
    echo "Checksum mismatch for $out" >&2
    echo "  expected: $expected" >&2
    echo "  actual:   $actual" >&2
    exit 1
  fi
  log "Checksum OK for $(basename "$out")"
}

# Static-link the MinGW runtime so the DLLs are self-contained.
STATIC_RT_FLAGS="-static -static-libgcc -static-libstdc++"

# Common cross-compile CMake args. An array (not an echo'd string) so the
# space-containing linker-flag values stay a single argument each.
COMMON_CMAKE_ARGS=(
  -G Ninja
  -DCMAKE_BUILD_TYPE=Release
  -DCMAKE_SYSTEM_NAME=Windows
  "-DCMAKE_C_COMPILER=${HOST}-gcc"
  "-DCMAKE_CXX_COMPILER=${HOST}-g++"
  "-DCMAKE_RC_COMPILER=${HOST}-windres"
  "-DCMAKE_FIND_ROOT_PATH=/usr/${HOST};$PREFIX"
  -DCMAKE_FIND_ROOT_PATH_MODE_PROGRAM=NEVER
  -DCMAKE_FIND_ROOT_PATH_MODE_LIBRARY=BOTH
  -DCMAKE_FIND_ROOT_PATH_MODE_INCLUDE=BOTH
  "-DCMAKE_INSTALL_PREFIX=$PREFIX"
  "-DCMAKE_PREFIX_PATH=$PREFIX"
  -DBUILD_SHARED_LIBS=ON
  "-DCMAKE_SHARED_LINKER_FLAGS=$STATIC_RT_FLAGS"
  "-DCMAKE_EXE_LINKER_FLAGS=$STATIC_RT_FLAGS"
)

build_cmake() { # srcDir buildDir extra-args...
  local src="$1" bld="$2"; shift 2
  cmake -S "$src" -B "$bld" "${COMMON_CMAKE_ARGS[@]}" "$@"
  cmake --build "$bld" ${CMAKE_BUILD_VERBOSE:+--verbose}
  cmake --install "$bld"
}

# ---- Clean workspace ---------------------------------------------------------
rm -rf "$WORK"
mkdir -p "$WORK" "$PREFIX"

log "Toolchain: $(${HOST}-gcc --version | head -1)"

# ---- SDL2 --------------------------------------------------------------------
SDL2_TARBALL="$WORK/SDL2-$SDL2_VERSION.tar.gz"
fetch "https://github.com/libsdl-org/SDL/releases/download/release-$SDL2_VERSION/SDL2-$SDL2_VERSION.tar.gz" \
  "$SDL2_SHA256" "$SDL2_TARBALL"
tar -C "$WORK" -xf "$SDL2_TARBALL"
log "Building SDL2 $SDL2_VERSION"
build_cmake "$WORK/SDL2-$SDL2_VERSION" "$WORK/build-SDL2" \
  -DSDL_STATIC=OFF -DSDL_TEST=OFF

# ---- SDL2_image (stb backend, no external codecs) ----------------------------
IMG_TARBALL="$WORK/SDL2_image-$SDL2_IMAGE_VERSION.tar.gz"
fetch "https://github.com/libsdl-org/SDL_image/releases/download/release-$SDL2_IMAGE_VERSION/SDL2_image-$SDL2_IMAGE_VERSION.tar.gz" \
  "$SDL2_IMAGE_SHA256" "$IMG_TARBALL"
tar -C "$WORK" -xf "$IMG_TARBALL"
log "Building SDL2_image $SDL2_IMAGE_VERSION (stb backend)"
build_cmake "$WORK/SDL2_image-$SDL2_IMAGE_VERSION" "$WORK/build-SDL2_image" \
  -DSDL2IMAGE_SAMPLES=OFF -DSDL2IMAGE_DEPS_SHARED=OFF -DSDL2IMAGE_VENDORED=OFF \
  -DSDL2IMAGE_BACKEND_STB=ON -DSDL2IMAGE_BACKEND_WIC=OFF \
  -DSDL2IMAGE_PNG=ON -DSDL2IMAGE_JPG=ON -DSDL2IMAGE_PNG_SAVE=ON \
  -DSDL2IMAGE_AVIF=OFF -DSDL2IMAGE_JXL=OFF -DSDL2IMAGE_TIF=OFF -DSDL2IMAGE_WEBP=OFF

# ---- SDL2_ttf (vendored: static FreeType + HarfBuzz) -------------------------
TTF_TARBALL="$WORK/SDL2_ttf-$SDL2_TTF_VERSION.tar.gz"
fetch "https://github.com/libsdl-org/SDL_ttf/releases/download/release-$SDL2_TTF_VERSION/SDL2_ttf-$SDL2_TTF_VERSION.tar.gz" \
  "$SDL2_TTF_SHA256" "$TTF_TARBALL"
tar -C "$WORK" -xf "$TTF_TARBALL"
log "Building SDL2_ttf $SDL2_TTF_VERSION (vendored FreeType + HarfBuzz)"
build_cmake "$WORK/SDL2_ttf-$SDL2_TTF_VERSION" "$WORK/build-SDL2_ttf" \
  -DSDL2TTF_SAMPLES=OFF -DSDL2TTF_VENDORED=ON -DSDL2TTF_HARFBUZZ=ON

# ---- Copy DLLs into Yafc/lib/windows and verify self-containment -------------
mkdir -p "$WIN_DIR"
# Allowed import prefixes: OS DLLs plus our own sibling SDL DLLs. Anything else
# (libgcc_s, libstdc++-6, libwinpthread-1, libpng16-16, ...) means the static
# link did not take and the DLL is not self-contained.
ALLOWED='^(KERNEL32|USER32|GDI32|WINMM|IMM32|OLE32|OLEAUT32|ADVAPI32|SHELL32|SETUPAPI|VERSION|SHLWAPI|msvcrt|api-ms-win|RPCRT4|WS2_32|hid|CFGMGR32|ntdll|dwmapi|d3d9|dxgi|dwrite|SDL2|SDL2_image|SDL2_ttf)\.dll$'

fail=0
for dll in SDL2.dll SDL2_image.dll SDL2_ttf.dll; do
  src="$PREFIX/bin/$dll"
  [ -f "$src" ] || { echo "Expected $src was not produced by the build" >&2; exit 1; }
  cp -f "$src" "$WIN_DIR/$dll"
  dump_pe "built $dll" "$WIN_DIR/$dll"
  while IFS= read -r imp; do
    if ! echo "$imp" | grep -qiE "$ALLOWED"; then
      echo "  UNEXPECTED import in $dll: $imp" >&2
      fail=1
    fi
  done < <("${HOST}-objdump" -p "$WIN_DIR/$dll" | awk '/DLL Name:/ {print $3}' | sort -u)
  log "Wrote $dll"
done

if [ "$fail" -ne 0 ]; then
  echo "One or more Windows DLLs import a non-system library; they are not self-contained." >&2
  exit 1
fi

# The old dependency DLLs are now built in and no longer needed.
for old in libpng16-16.dll libjpeg-9.dll zlib1.dll libfreetype-6.dll; do
  [ -f "$WIN_DIR/$old" ] && log "Now built in, can be removed from Yafc/lib/windows: $old"
done

ls -la "$WIN_DIR"
log "Done. Built SDL2 $SDL2_VERSION, SDL2_image $SDL2_IMAGE_VERSION, SDL2_ttf $SDL2_TTF_VERSION."
