# Building the bundled SDL2 libraries

Yafc bundles SDL2, SDL2_image and SDL2_ttf for Windows and macOS so users don't
have to install them by hand. These scripts (re)build those libraries from
pinned upstream source, the same way [`lua/`](../lua) builds `liblua52`.

Linux is intentionally not covered: Linux users install SDL from their distro's
packages (see [`Docs/LinuxOsxInstall.md`](../Docs/LinuxOsxInstall.md)).

## Why build from source?

Previously the macOS dylibs were copied straight out of a Homebrew install. A
Homebrew build bakes absolute `/opt/homebrew/...` paths into its Mach-O load
commands, so the bundled libraries only worked on a Mac that already had
Homebrew with the exact same packages. On a clean Mac (or Nix) Yafc failed to
launch — [issue #659](https://github.com/Yafc-CE/yafc-ce/issues/659). Building
from source lets us produce libraries that are fully self-contained and
reproducible from a known configuration ([issue #274](https://github.com/Yafc-CE/yafc-ce/issues/274)).

## Configuration

Both platforms use the same configuration so behaviour is consistent:

* **SDL2_image** uses the built-in **stb_image** backend for PNG and JPG (all
  Yafc needs — see `IMG_Init(IMG_INIT_PNG | IMG_INIT_JPG)` in `Yafc.UI`). No
  external codec libraries (libpng, libjpeg, libwebp, libavif, ...) are linked.
  `IMG_SavePNG` still works via SDL_image's built-in miniz encoder.
* **SDL2_ttf** is built **vendored**, statically linking its own FreeType and
  HarfBuzz.

The result is three libraries that depend only on the operating system and on
`libSDL2` — nothing else has to be shipped.

## Rebuilding

The easy way is the **Build native libraries** GitHub Actions workflow
(`.github/workflows/build-native-libs.yml`): run it from the Actions tab,
optionally overriding the versions, and it builds and verifies every platform.
Enable *Open a pull request* to have it commit the results back.

To build locally instead:

### macOS (`build-macos.sh`)

Runs on macOS. Builds a universal (arm64 + x86_64) binary and splits it into
`Yafc/lib/osx-arm64` and `Yafc/lib/osx`, rewriting install names to
`@loader_path` and ad-hoc code-signing so the libraries are relocatable.

```
sdl/build-macos.sh
```

Requires the Xcode command line tools plus `cmake` and `ninja`
(`brew install cmake ninja`).

### Windows (`build-windows.sh`)

Cross-compiles on Linux with MinGW-w64 into `Yafc/lib/windows`. MinGW matches
the toolchain the existing Windows DLLs were built with, and the C/C++ runtime
is linked statically so no `libgcc`/`libstdc++`/`libwinpthread` DLLs are needed.

```
sdl/build-windows.sh
```

Requires `mingw-w64`, `cmake` and `ninja`
(`sudo apt-get install mingw-w64 cmake ninja-build`).

These three DLLs replace the old dependency set — `libpng16-16.dll`,
`libjpeg-9.dll`, `zlib1.dll` and `libfreetype-6.dll` are now built in and can be
deleted. `lua52.dll` is unrelated and left alone.

## Bumping a version

1. Edit the default `*_VERSION` and `*_SHA256` values at the top of both build
   scripts (and the workflow input defaults). The checksums are the sha256 of
   the release tarballs from `github.com/libsdl-org/*/releases`.
2. Re-run the builds (workflow or local scripts).
3. Commit the updated libraries.

## Verifying (`check-macho.py`)

`check-macho.py` parses the Mach-O load commands of the committed macOS dylibs
and fails if any of them references a path that may be absent on a clean macOS
(anything outside `/usr/lib`, `/System/Library`, or a `@loader_path`/`@rpath`
relative path). It needs no macOS tooling, so it runs in CI on Linux via the
**Check native libraries** workflow on every pull request that touches
`Yafc/lib/`.

```
python3 sdl/check-macho.py Yafc/lib/osx-arm64 Yafc/lib/osx
```
