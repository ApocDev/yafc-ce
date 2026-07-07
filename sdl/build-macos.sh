#!/usr/bin/env bash
#
# Build self-contained SDL2 / SDL2_image / SDL2_ttf dylibs for macOS from
# pinned upstream source and copy them into Yafc/lib/osx-arm64 and
# Yafc/lib/osx (Intel).
#
# Why from source instead of copying Homebrew's dylibs: Homebrew builds bake
# absolute /opt/homebrew/... paths into their Mach-O load commands, so the
# bundled libraries fail to load on any Mac without those exact paths (no
# Homebrew, Nix, a clean machine). See issue #659.
#
# The libraries produced here depend only on macOS system frameworks and on
# each other via @loader_path, so they are fully portable:
#   * SDL2_image uses the built-in stb_image backend (PNG + JPG, which is all
#     Yafc uses) and links no external codec libraries.
#   * SDL2_ttf is built VENDORED, statically linking its own FreeType + HarfBuzz.
#
# By default a universal (arm64 + x86_64) binary is built on one runner and
# split with lipo, so a single macOS runner covers both Yafc/lib/osx-arm64 and
# Yafc/lib/osx. The result is validated by sdl/check-macho.py before copying.
#
# Dependencies: cmake, curl, shasum, install_name_tool, lipo, codesign, a C/C++
# toolchain (Xcode command line tools). All present on GitHub's macOS runners.
#
# Usage: sdl/build-macos.sh   (override versions via the env vars below)

set -euo pipefail

# ---- Pinned upstream versions ------------------------------------------------
# Bumping SDL == change these (and the matching checksums) and re-run.
SDL2_VERSION="${SDL2_VERSION:-2.32.10}"
SDL2_IMAGE_VERSION="${SDL2_IMAGE_VERSION:-2.8.12}"
SDL2_TTF_VERSION="${SDL2_TTF_VERSION:-2.24.0}"

# sha256 of the release tarballs from github.com/libsdl-org/*/releases.
SDL2_SHA256="${SDL2_SHA256:-5f5993c530f084535c65a6879e9b26ad441169b3e25d789d83287040a9ca5165}"
SDL2_IMAGE_SHA256="${SDL2_IMAGE_SHA256:-393f5efb50536ec13ca4f4affb69cc9966d3c3f969e6c5e701faddf9f9785381}"
SDL2_TTF_SHA256="${SDL2_TTF_SHA256:-0b2bf1e7b6568adbdbc9bb924643f79d9dedafe061fa1ed687d1d9ac4e453bfd}"

# Architectures to build. Two arches -> a universal build that is lipo-split.
OSX_ARCHS="${OSX_ARCHS:-arm64;x86_64}"
# Deployment target: match the oldest macOS Yafc supports.
export MACOSX_DEPLOYMENT_TARGET="${MACOSX_DEPLOYMENT_TARGET:-11.0}"

# ---- Paths -------------------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
WORK="$SCRIPT_DIR/.build-macos"
STAGE="$WORK/stage"          # where the final universal dylibs are assembled
PREFIX="$WORK/prefix"        # cmake --install destination

ARM64_DIR="$REPO_ROOT/Yafc/lib/osx-arm64"
INTEL_DIR="$REPO_ROOT/Yafc/lib/osx"

# Final filenames Yafc loads (see Yafc/YafcLib.cs GetOsxMappedLibraryName).
declare -A OUTPUT=(
  [SDL2]="libSDL2.dylib"
  [SDL2_image]="libSDL2_image.dylib"
  [SDL2_ttf]="libSDL2_ttf.dylib"
)

# Heavy logging: while we are still validating this pipeline (issue #659) we want
# to see exactly what each stage produced. Set SDL_BUILD_QUIET=1 to trim it down.
VERBOSE="${SDL_BUILD_QUIET:+}"
[ -z "${SDL_BUILD_QUIET:-}" ] && VERBOSE=1

log() { echo "==> $*"; }

# Dump everything interesting about a Mach-O file: architectures, install id,
# and the full dependency list. Used before/after relinking so a broken load
# command is obvious in the CI log.
dump_macho() { # label file
  [ -n "${VERBOSE:-}" ] || return 0
  local label="$1" file="$2"
  echo "----- $label: $file"
  if [ ! -f "$file" ]; then
    echo "    (missing)"
    return 0
  fi
  echo "    file:  $(file -b "$file")"
  echo "    archs: $(lipo -archs "$file" 2>/dev/null || echo 'n/a')"
  echo "    id:    $(otool -D "$file" 2>/dev/null | tail -n +2)"
  echo "    deps:"
  otool -L "$file" 2>/dev/null | tail -n +2 | sed 's/^/      /'
  echo "    codesign:"
  codesign -dvv "$file" 2>&1 | sed 's/^/      /' || true
}

fetch() { # url sha256 outfile
  local url="$1" sha="$2" out="$3"
  if [ ! -f "$out" ]; then
    log "Downloading $(basename "$out")"
    curl -fL -o "$out" "$url"
  else
    log "Using cached $(basename "$out")"
  fi
  # Checksums may carry stray whitespace when edited by hand; strip it.
  local expected="${sha// /}"
  local actual
  actual="$(shasum -a 256 "$out" | cut -d' ' -f1)"
  if [ "$expected" != "$actual" ]; then
    echo "Checksum mismatch for $out" >&2
    echo "  expected: $expected" >&2
    echo "  actual:   $actual" >&2
    exit 1
  fi
  log "Checksum OK for $(basename "$out")"
}

# ---- Clean workspace ---------------------------------------------------------
rm -rf "$WORK"
mkdir -p "$WORK" "$STAGE" "$PREFIX"

# ---- SDL2 --------------------------------------------------------------------
SDL2_TARBALL="$WORK/SDL2-$SDL2_VERSION.tar.gz"
fetch "https://github.com/libsdl-org/SDL/releases/download/release-$SDL2_VERSION/SDL2-$SDL2_VERSION.tar.gz" \
  "$SDL2_SHA256" "$SDL2_TARBALL"
tar -C "$WORK" -xf "$SDL2_TARBALL"

log "Building SDL2 $SDL2_VERSION ($OSX_ARCHS)"
cmake -S "$WORK/SDL2-$SDL2_VERSION" -B "$WORK/build-SDL2" -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_OSX_ARCHITECTURES="$OSX_ARCHS" \
  -DCMAKE_INSTALL_PREFIX="$PREFIX" \
  -DBUILD_SHARED_LIBS=ON \
  -DSDL_STATIC=OFF -DSDL_TEST=OFF
cmake --build "$WORK/build-SDL2" ${CMAKE_BUILD_VERBOSE:+--verbose}
cmake --install "$WORK/build-SDL2"

# ---- SDL2_image (stb backend, no external codecs) ----------------------------
SDL2_IMAGE_TARBALL="$WORK/SDL2_image-$SDL2_IMAGE_VERSION.tar.gz"
fetch "https://github.com/libsdl-org/SDL_image/releases/download/release-$SDL2_IMAGE_VERSION/SDL2_image-$SDL2_IMAGE_VERSION.tar.gz" \
  "$SDL2_IMAGE_SHA256" "$SDL2_IMAGE_TARBALL"
tar -C "$WORK" -xf "$SDL2_IMAGE_TARBALL"

log "Building SDL2_image $SDL2_IMAGE_VERSION (stb backend, no external codecs)"
cmake -S "$WORK/SDL2_image-$SDL2_IMAGE_VERSION" -B "$WORK/build-SDL2_image" -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_OSX_ARCHITECTURES="$OSX_ARCHS" \
  -DCMAKE_INSTALL_PREFIX="$PREFIX" \
  -DCMAKE_PREFIX_PATH="$PREFIX" \
  -DBUILD_SHARED_LIBS=ON \
  -DSDL2IMAGE_SAMPLES=OFF \
  -DSDL2IMAGE_DEPS_SHARED=OFF \
  -DSDL2IMAGE_VENDORED=OFF \
  -DSDL2IMAGE_BACKEND_STB=ON \
  -DSDL2IMAGE_BACKEND_IMAGEIO=OFF \
  -DSDL2IMAGE_PNG=ON -DSDL2IMAGE_JPG=ON -DSDL2IMAGE_PNG_SAVE=ON \
  -DSDL2IMAGE_AVIF=OFF -DSDL2IMAGE_JXL=OFF -DSDL2IMAGE_TIF=OFF -DSDL2IMAGE_WEBP=OFF
cmake --build "$WORK/build-SDL2_image" ${CMAKE_BUILD_VERBOSE:+--verbose}
cmake --install "$WORK/build-SDL2_image"

# ---- SDL2_ttf (vendored: static FreeType + HarfBuzz) -------------------------
SDL2_TTF_TARBALL="$WORK/SDL2_ttf-$SDL2_TTF_VERSION.tar.gz"
fetch "https://github.com/libsdl-org/SDL_ttf/releases/download/release-$SDL2_TTF_VERSION/SDL2_ttf-$SDL2_TTF_VERSION.tar.gz" \
  "$SDL2_TTF_SHA256" "$SDL2_TTF_TARBALL"
tar -C "$WORK" -xf "$SDL2_TTF_TARBALL"

log "Building SDL2_ttf $SDL2_TTF_VERSION (vendored FreeType + HarfBuzz)"
cmake -S "$WORK/SDL2_ttf-$SDL2_TTF_VERSION" -B "$WORK/build-SDL2_ttf" -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_OSX_ARCHITECTURES="$OSX_ARCHS" \
  -DCMAKE_INSTALL_PREFIX="$PREFIX" \
  -DCMAKE_PREFIX_PATH="$PREFIX" \
  -DBUILD_SHARED_LIBS=ON \
  -DSDL2TTF_SAMPLES=OFF \
  -DSDL2TTF_VENDORED=ON \
  -DSDL2TTF_HARFBUZZ=ON
cmake --build "$WORK/build-SDL2_ttf" ${CMAKE_BUILD_VERBOSE:+--verbose}
cmake --install "$WORK/build-SDL2_ttf"

# ---- Stage: resolve symlinks, rename, rewrite install names to @loader_path --
# `cmake --install` writes versioned dylibs (libSDL2-2.0.0.dylib) plus unversioned
# symlinks. Copy the real file under the exact name Yafc loads, then rewrite every
# non-system reference to @loader_path so the libraries find each other next to
# the executable regardless of where Yafc is installed.
log "Staging freshly built dylibs"
for key in SDL2 SDL2_image SDL2_ttf; do
  src="$(readlink -f "$PREFIX/lib/${OUTPUT[$key]}")"
  cp "$src" "$STAGE/${OUTPUT[$key]}"
  dump_macho "as-built $key" "$STAGE/${OUTPUT[$key]}"
done

log "Rewriting install names to @loader_path"
for key in SDL2 SDL2_image SDL2_ttf; do
  file="$STAGE/${OUTPUT[$key]}"
  install_name_tool -id "@loader_path/${OUTPUT[$key]}" "$file"
  # Rewrite any dependency that isn't an OS path to its bundled @loader_path name.
  while IFS= read -r dep; do
    case "$dep" in
      /usr/lib/*|/System/*|@*) continue ;;
    esac
    base="$(basename "$dep")"
    newname=""
    case "$base" in
      libSDL2-*.dylib|libSDL2.dylib)             newname="${OUTPUT[SDL2]}" ;;
      libSDL2_image-*.dylib|libSDL2_image.dylib) newname="${OUTPUT[SDL2_image]}" ;;
      libSDL2_ttf-*.dylib|libSDL2_ttf.dylib)     newname="${OUTPUT[SDL2_ttf]}" ;;
      *)
        echo "Unexpected external dependency in $file: $dep" >&2
        echo "The build is supposed to be self-contained; refusing to continue." >&2
        exit 1
        ;;
    esac
    echo "    $key: $dep -> @loader_path/$newname"
    install_name_tool -change "$dep" "@loader_path/$newname" "$file"
  done < <(otool -L "$file" | tail -n +2 | awk '{print $1}')
done

# Editing a Mach-O invalidates its (ad-hoc) code signature; re-sign so dyld on
# Apple Silicon will load it.
log "Re-signing (ad-hoc)"
for key in SDL2 SDL2_image SDL2_ttf; do
  codesign --force --sign - "$STAGE/${OUTPUT[$key]}"
  dump_macho "relinked+signed $key" "$STAGE/${OUTPUT[$key]}"
done

# ---- Verify before copying anything into the repo ----------------------------
log "Verifying self-containment (universal staged dylibs)"
python3 "$SCRIPT_DIR/check-macho.py" "$STAGE"

# ---- Split universal -> per-arch and copy into Yafc/lib ----------------------
copy_arch() { # lipo-arch  dest-dir
  local arch="$1" dest="$2"
  mkdir -p "$dest"
  for key in SDL2 SDL2_image SDL2_ttf; do
    local name="${OUTPUT[$key]}"
    if lipo "$STAGE/$name" -verify_arch "$arch" 2>/dev/null; then
      lipo "$STAGE/$name" -thin "$arch" -output "$dest/$name"
    else
      cp "$STAGE/$name" "$dest/$name"   # already single-arch
    fi
    codesign --force --sign - "$dest/$name"
    dump_macho "$arch $key" "$dest/$name"
  done
  log "Wrote $arch dylibs to $dest"
  ls -la "$dest"
}

copy_arch arm64 "$ARM64_DIR"
copy_arch x86_64 "$INTEL_DIR"

# Final gate: re-run the verifier against exactly what landed in the repo.
log "Verifying self-containment (per-arch dylibs in Yafc/lib)"
python3 "$SCRIPT_DIR/check-macho.py" "$ARM64_DIR" "$INTEL_DIR"

log "Done. Built SDL2 $SDL2_VERSION, SDL2_image $SDL2_IMAGE_VERSION, SDL2_ttf $SDL2_TTF_VERSION."
