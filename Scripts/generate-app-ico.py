#!/usr/bin/env python3
"""
Generates app.ico for Windows from the Store visual assets.

The Microsoft Store package already ships hand-sized targetsize PNGs that look
crisp in the Windows taskbar/titlebar. The portable EXE must reuse that same
asset family instead of resizing one large master PNG for every small icon.

Requires: pip install Pillow

Usage: python generate-app-ico.py
"""

import sys
import struct
from io import BytesIO
from pathlib import Path

try:
    from PIL import Image
except ImportError:
    print("ERROR: Pillow library is not installed.")
    print("")
    print("Install it with:")
    print("  pip install Pillow")
    sys.exit(1)


ICO_SIZES = [256, 128, 96, 80, 72, 64, 60, 48, 44, 40, 36, 32, 30, 24, 20, 16]


def read_png_bytes(path: Path, expected_size: int) -> bytes:
    with Image.open(path) as img:
        if img.size != (expected_size, expected_size):
            raise ValueError(f"{path} is {img.size[0]}x{img.size[1]}, expected {expected_size}x{expected_size}")

    return path.read_bytes()


def resize_png_bytes(source_path: Path, size: int) -> bytes:
    with Image.open(source_path) as img:
        img = img.convert("RGBA")
        resized = img.resize((size, size), Image.Resampling.LANCZOS)

        output = BytesIO()
        resized.save(output, format="PNG", optimize=True)
        return output.getvalue()


def load_icon_entry(visual_assets_dir: Path, size: int) -> tuple[int, bytes, str]:
    target_size_png = visual_assets_dir / f"Square44x44Logo.targetsize-{size}.png"
    if target_size_png.exists():
        return size, read_png_bytes(target_size_png, size), "targetsize"

    # Windows rarely asks for 128px directly, but keeping it in the ICO helps
    # Explorer/Alt+Tab fallback paths without forcing them to downscale 256px.
    source_png = visual_assets_dir / "Square44x44Logo.targetsize-256.png"
    if not source_png.exists():
        raise FileNotFoundError(f"Missing fallback source PNG: {source_png}")

    return size, resize_png_bytes(source_png, size), "generated"


def write_ico(output_ico: Path, entries: list[tuple[int, bytes, str]]) -> None:
    header_size = 6
    directory_entry_size = 16
    image_offset = header_size + directory_entry_size * len(entries)

    with output_ico.open("wb") as stream:
        stream.write(struct.pack("<HHH", 0, 1, len(entries)))

        offset = image_offset
        for size, png_bytes, _ in entries:
            encoded_size = 0 if size == 256 else size
            stream.write(struct.pack(
                "<BBBBHHII",
                encoded_size,
                encoded_size,
                0,
                0,
                1,
                32,
                len(png_bytes),
                offset))
            offset += len(png_bytes)

        for _, png_bytes, _ in entries:
            stream.write(png_bytes)


def main():
    script_dir = Path(__file__).parent.resolve()
    repo_root = script_dir.parent

    visual_assets_dir = repo_root / "Assets" / "AppIcon" / "Windows" / "VisualAssets"
    output_ico = repo_root / "Assets" / "AppIcon" / "Windows" / "app.ico"

    if not visual_assets_dir.exists():
        print(f"ERROR: Visual assets directory not found: {visual_assets_dir}")
        sys.exit(1)

    print("=" * 60)
    print("Generating app.ico")
    print("=" * 60)
    print(f"Source: {visual_assets_dir}")
    print(f"Output: {output_ico}")
    print()

    print("Collecting icon sizes...")
    entries = []
    for size in ICO_SIZES:
        entry = load_icon_entry(visual_assets_dir, size)
        entries.append(entry)
        print(f"  {size}x{size} ({entry[2]})")

    print("Saving ICO...")
    write_ico(output_ico, entries)

    # Verify
    print()
    print("=" * 60)
    print("SUCCESS!")
    print("=" * 60)
    print(f"File: {output_ico}")
    print(f"Size: {output_ico.stat().st_size:,} bytes")
    print()
    print("Embedded sizes:")

    with open(output_ico, 'rb') as f:
        reserved, icon_type, count = struct.unpack("<HHH", f.read(6))
        if reserved != 0 or icon_type != 1:
            raise ValueError("Generated file does not have a valid ICO header")

        for i in range(count):
            entry = f.read(16)
            w, h, _, _, _, bpp, size, _ = struct.unpack('<BBBBHHII', entry)
            w = w if w != 0 else 256
            usage = " (taskbar/titlebar DPI)" if w in [20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96] else ""
            usage = " (large fallback)" if w in [128, 256] else usage
            print(f"  {w}x{w}, {bpp}bpp, {size:,} bytes{usage}")


if __name__ == "__main__":
    main()
