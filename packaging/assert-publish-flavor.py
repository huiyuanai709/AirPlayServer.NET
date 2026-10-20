#!/usr/bin/env python3
"""Fail if a publish directory has the wrong Avalonia flavor."""

import sys
from pathlib import Path


LEAN_NAME_FRAGMENTS = (
    "libSkiaSharp",
    "libHarfBuzzSharp",
    "libAvaloniaNative",
    "av_libglesv2",
    "Avalonia.Fonts",
)

LEAN_BINARY_NEEDLES = (
    b"libSkiaSharp",
    b"libHarfBuzzSharp",
    b"Avalonia.Controls",
    b"Avalonia.Fonts.Inter",
    b"av_libglesv2",
)


def main() -> None:
    if len(sys.argv) != 3:
        sys.exit("usage: assert-publish-flavor.py <publish-dir> <lean|ui>")
    directory = Path(sys.argv[1])
    flavor = sys.argv[2]
    if flavor not in ("lean", "ui"):
        sys.exit("flavor must be lean or ui")
    if not directory.is_dir():
        sys.exit(f"missing publish directory: {directory}")

    names = [path.name for path in directory.iterdir() if path.is_file()]

    def has(fragment: str) -> bool:
        return any(fragment in name for name in names)

    if flavor == "lean":
        for fragment in LEAN_NAME_FRAGMENTS:
            if has(fragment):
                sys.exit(f"lean publish still contains {fragment}")
        binaries = [
            path
            for path in directory.iterdir()
            if path.name in ("AirPlayServer.Host", "AirPlayServer.Host.exe")
        ]
        if not binaries:
            sys.exit("lean publish is missing AirPlayServer.Host")
        data = binaries[0].read_bytes()
        for needle in LEAN_BINARY_NEEDLES:
            if needle in data:
                sys.exit("lean binary contains " + needle.decode())
    else:
        if not has("libSkiaSharp") or not has("libHarfBuzzSharp"):
            sys.exit(
                "ui publish is missing Skia or HarfBuzz native library: "
                + ", ".join(sorted(names))
            )
        if has("av_libglesv2"):
            sys.exit("ui publish still contains av_libglesv2 (ANGLE)")
        if any(name.endswith(".dylib") for name in names) and not has("libAvaloniaNative"):
            sys.exit("ui publish has dylibs but not libAvaloniaNative")

    print(f"publish flavor {flavor} ok ({directory})")


if __name__ == "__main__":
    main()
