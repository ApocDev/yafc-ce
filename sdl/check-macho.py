#!/usr/bin/env python3
"""Validate that committed macOS dylibs are self-contained.

A Homebrew- or system-built dylib bakes absolute paths (e.g.
``/opt/homebrew/opt/sdl2_image/lib/...``) into its Mach-O load commands. When
such a library is bundled with Yafc and run on a Mac that does not have those
exact paths populated (no Homebrew, Nix, a clean machine, ...), dyld fails to
resolve the dependency and Yafc cannot launch. See issue #659.

This script parses the Mach-O load commands of every dylib it is given and
fails if any dependency (LC_LOAD_DYLIB / LC_LOAD_WEAK_DYLIB / LC_REEXPORT_DYLIB)
or the library's own id (LC_ID_DYLIB) points outside the set of paths that are
guaranteed to exist on every macOS install, namely:

  * ``/usr/lib/`` and ``/System/Library/`` -- OS-provided libraries/frameworks
  * ``@loader_path/`` / ``@rpath/`` / ``@executable_path/`` -- bundle-relative
  * a bare filename (no slash) -- resolved next to the loader

It needs no macOS tooling (no ``otool``); it reads the Mach-O headers directly,
so it runs anywhere, including Linux CI.

Usage:
    check-macho.py FILE_OR_DIR [FILE_OR_DIR ...]

Exit status is non-zero if any checked library has a disallowed reference.
"""

import os
import struct
import sys

FAT_MAGIC = 0xCAFEBABE
FAT_CIGAM = 0xBEBAFECA
MH_MAGIC_64 = 0xFEEDFACF
MH_CIGAM_64 = 0xCFFAEDFE

LC_ID_DYLIB = 0x0D
LC_LOAD_DYLIB = 0x0C
LC_LOAD_WEAK_DYLIB = 0x80000018
LC_REEXPORT_DYLIB = 0x8000001F

DYLIB_CMDS = {
    LC_ID_DYLIB: "id",
    LC_LOAD_DYLIB: "load",
    LC_LOAD_WEAK_DYLIB: "weak-load",
    LC_REEXPORT_DYLIB: "reexport",
}

# CPU types we care about labelling in messages.
CPU_NAMES = {0x01000007: "x86_64", 0x0100000C: "arm64"}

# Prefixes that are guaranteed present on any macOS install, or are relative to
# the bundle and therefore travel with Yafc.
#
# @rpath is intentionally NOT allowed: it only resolves if the loading image
# carries a matching LC_RPATH, so an @rpath dependency in a bundled library is a
# latent "works on my machine" failure. Yafc's libraries sit next to the
# executable and must reference each other via @loader_path.
ALLOWED_PREFIXES = (
    "/usr/lib/",
    "/System/Library/",
    "@loader_path/",
    "@executable_path/",
)


def _is_allowed(path: str) -> bool:
    if "/" not in path:
        # Bare filename: dyld resolves it next to the loader / on the search
        # path. Portable enough for a bundled sibling library.
        return True
    return path.startswith(ALLOWED_PREFIXES)


def _read_thin(data: bytes, off: int):
    """Yield (kind, path, arch) for the dylib load commands of one Mach-O slice."""
    magic = struct.unpack_from("<I", data, off)[0]
    if magic == MH_MAGIC_64:
        end = "<"
    elif magic == MH_CIGAM_64:
        end = ">"
    else:
        # 32-bit or non-Mach-O slice; Yafc ships only 64-bit dylibs.
        raise ValueError(f"unsupported Mach-O magic {magic:#x} at offset {off}")

    cputype = struct.unpack_from(end + "i", data, off + 4)[0]
    ncmds = struct.unpack_from(end + "I", data, off + 16)[0]
    arch = CPU_NAMES.get(cputype & 0xFFFFFFFF, f"cpu:{cputype}")

    p = off + 32
    for _ in range(ncmds):
        cmd, cmdsize = struct.unpack_from(end + "II", data, p)
        if cmd in DYLIB_CMDS:
            noff = struct.unpack_from(end + "I", data, p + 8)[0]
            raw = data[p + noff : p + cmdsize]
            path = raw.split(b"\x00", 1)[0].decode("utf-8", "replace")
            yield DYLIB_CMDS[cmd], path, arch
        p += cmdsize


def _slices(data: bytes):
    """Yield the file offset of each Mach-O slice (handles fat binaries)."""
    magic = struct.unpack_from(">I", data, 0)[0]
    if magic in (FAT_MAGIC, FAT_CIGAM):
        nfat = struct.unpack_from(">I", data, 4)[0]
        for i in range(nfat):
            yield struct.unpack_from(">I", data, 8 + i * 20 + 8)[0]
    else:
        yield 0


def check_file(path: str) -> list[str]:
    """Return a list of human-readable problems for one dylib (empty == ok)."""
    with open(path, "rb") as fh:
        data = fh.read()

    problems: list[str] = []
    for off in _slices(data):
        for kind, ref, arch in _read_thin(data, off):
            # A library's own id is allowed to be a bare name or @-relative;
            # what we forbid is an absolute non-system path anywhere.
            if not _is_allowed(ref):
                problems.append(f"[{arch}] {kind}: {ref}")
    return problems


def _iter_targets(args):
    for arg in args:
        if os.path.isdir(arg):
            for root, _dirs, files in os.walk(arg):
                for name in files:
                    if name.endswith(".dylib"):
                        yield os.path.join(root, name)
        else:
            yield arg


def main(argv: list[str]) -> int:
    targets = sorted(set(_iter_targets(argv[1:])))
    if not targets:
        print("check-macho: no dylibs to check", file=sys.stderr)
        return 2

    failed = False
    for path in targets:
        try:
            problems = check_file(path)
        except (ValueError, struct.error) as exc:
            print(f"FAIL {path}: cannot parse ({exc})")
            failed = True
            continue

        if problems:
            failed = True
            print(f"FAIL {path}: depends on paths that may be absent on a clean macOS:")
            for problem in problems:
                print(f"    {problem}")
        else:
            print(f"ok   {path}")

    if failed:
        print(
            "\nOne or more dylibs reference non-system, non-bundle-relative paths.\n"
            "Rebuild them with sdl/build-macos.sh so their install names use\n"
            "@loader_path and all codecs are built in (see issue #659).",
            file=sys.stderr,
        )
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
