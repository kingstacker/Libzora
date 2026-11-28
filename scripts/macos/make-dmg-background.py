#!/usr/bin/env python3
"""Generate the Libzora DMG background (no third-party imaging modules).

The image is rendered at 2x and box-filtered down so Finder's rounded
corner strokes and center glow stay smooth on Retina displays, then
optionally annotated with crisp CoreText instructions on macOS.
"""
from __future__ import annotations

import argparse
import os
import shutil
import struct
import subprocess
import tempfile
import zlib
from pathlib import Path

WIDTH = 520
HEIGHT = 360
ICON_CENTER_X = 260.0
ICON_CENTER_Y = 154.0
SCALE = 2


def lerp(a: float, b: float, t: float) -> float:
    return a + (b - a) * t


def blend(dst: list[int], color: tuple[int, int, int], alpha: float) -> None:
    if alpha <= 0:
        return
    if alpha > 1:
        alpha = 1.0
    for index in range(3):
        dst[index] = round(lerp(dst[index], color[index], alpha))


def rounded_rect_sdf(x: float, y: float, cx: float, cy: float, hw: float, hh: float, r: float) -> float:
    qx = abs(x - cx) - (hw - r)
    qy = abs(y - cy) - (hh - r)
    outside = (max(qx, 0.0) ** 2 + max(qy, 0.0) ** 2) ** 0.5
    return outside + min(max(qx, qy), 0.0) - r


def render() -> bytes:
    width, height = WIDTH * SCALE, HEIGHT * SCALE
    pixels: list[list[int]] = []
    for py in range(height):
        y = (py + 0.5) / SCALE
        row: list[list[int]] = []
        for px in range(width):
            x = (px + 0.5) / SCALE
            t = (x / WIDTH) * 0.55 + (y / HEIGHT) * 0.45
            color = [
                round(lerp(247, 229, t)),
                round(lerp(243, 236, t)),
                round(lerp(234, 246, t)),
            ]
            # Soft glow behind the single centered installer icon.
            distance = ((x - ICON_CENTER_X) ** 2 + (y - ICON_CENTER_Y) ** 2) ** 0.5
            if distance < 116:
                blend(color, (255, 255, 255), (1 - distance / 116) * 0.44)

            # Subtle pill backdrop for the double-click install hint below the icon.
            pill = rounded_rect_sdf(x, y, WIDTH / 2, 286.0, 178.0, 27.0, 14.0)
            if pill <= 0.0:
                blend(color, (255, 255, 255), 0.52)
            if abs(pill) <= 0.8:
                blend(color, (186, 176, 158), 0.35)

            # Rounded outer frame.
            frame = rounded_rect_sdf(x, y, WIDTH / 2, HEIGHT / 2, WIDTH / 2 - 22, HEIGHT / 2 - 22, 24)
            if abs(frame) <= 1.1:
                blend(color, (186, 176, 158), 0.55)

            row.append(color)
        pixels.append(row)

    # Box-filter the 2x render down to the final size.
    final: list[list[int]] = []
    for y in range(HEIGHT):
        row: list[list[int]] = []
        for x in range(WIDTH):
            block = [
                pixels[y * SCALE + dy][x * SCALE + dx]
                for dy in range(SCALE)
                for dx in range(SCALE)
            ]
            row.append([
                round(sum(pixel[channel] for pixel in block) / len(block))
                for channel in range(3)
            ])
        final.append(row)

    raw = bytearray()
    for row in final:
        raw.append(0)
        for pixel in row:
            raw.extend(pixel)
    return bytes(raw)


def overlay_prompt_macos(png_path: Path) -> None:
    """Draw crisp Retina bilingual double-click prompt onto the PNG using AppKit."""
    if os.uname().sysname != "Darwin" or shutil.which("swiftc") is None:
        return

    swift_source = r'''
import AppKit
import Foundation

let path = CommandLine.arguments[1]
guard let sourceImage = NSImage(contentsOfFile: path),
      let tiff = sourceImage.tiffRepresentation,
      let rep = NSBitmapImageRep(data: tiff) else {
    exit(1)
}
let width = rep.pixelsWide
let height = rep.pixelsHigh
let scale: CGFloat = 2.0
guard let canvas = NSBitmapImageRep(
    bitmapDataPlanes: nil,
    pixelsWide: width * Int(scale),
    pixelsHigh: height * Int(scale),
    bitsPerSample: 8,
    samplesPerPixel: 4,
    hasAlpha: true,
    isPlanar: false,
    colorSpaceName: .deviceRGB,
    bytesPerRow: 0,
    bitsPerPixel: 0
) else {
    exit(1)
}
canvas.size = NSSize(width: width, height: height)

NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: canvas)
NSGraphicsContext.current?.imageInterpolation = .high
sourceImage.draw(in: NSRect(x: 0, y: 0, width: width, height: height))

let paragraph = NSMutableParagraphStyle()
paragraph.alignment = .center

let primaryAttrs: [NSAttributedString.Key: Any] = [
    .font: NSFont.systemFont(ofSize: 13.5, weight: .medium),
    .foregroundColor: NSColor(calibratedRed: 0.27, green: 0.24, blue: 0.20, alpha: 0.96),
    .paragraphStyle: paragraph,
]
let secondaryAttrs: [NSAttributedString.Key: Any] = [
    .font: NSFont.systemFont(ofSize: 11.0, weight: .regular),
    .foregroundColor: NSColor(calibratedRed: 0.45, green: 0.41, blue: 0.36, alpha: 0.90),
    .paragraphStyle: paragraph,
]

// AppKit coordinates have origin at bottom-left; image height is 360, pill center is y=286 from top -> y=74 from bottom.
let primaryText = "双击上方图标，自动安装到“应用程序”" as NSString
let secondaryText = "Double-click the icon above to install" as NSString
primaryText.draw(in: NSRect(x: 40, y: 74, width: CGFloat(width) - 80, height: 22), withAttributes: primaryAttrs)
secondaryText.draw(in: NSRect(x: 40, y: 54, width: CGFloat(width) - 80, height: 18), withAttributes: secondaryAttrs)

NSGraphicsContext.restoreGraphicsState()

guard let outRep = NSBitmapImageRep(
    bitmapDataPlanes: nil,
    pixelsWide: width,
    pixelsHigh: height,
    bitsPerSample: 8,
    samplesPerPixel: 4,
    hasAlpha: true,
    isPlanar: false,
    colorSpaceName: .deviceRGB,
    bytesPerRow: 0,
    bitsPerPixel: 0
) else {
    exit(1)
}
outRep.size = NSSize(width: width, height: height)
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: outRep)
NSGraphicsContext.current?.imageInterpolation = .high
let hiRes = NSImage(size: NSSize(width: width, height: height))
hiRes.addRepresentation(canvas)
hiRes.draw(in: NSRect(x: 0, y: 0, width: width, height: height))
NSGraphicsContext.restoreGraphicsState()

guard let pngData = outRep.representation(using: .png, properties: [:]) else {
    exit(1)
}
try pngData.write(to: URL(fileURLWithPath: path))
'''
    with tempfile.TemporaryDirectory() as tmp:
        tmp_dir = Path(tmp)
        swift_file = tmp_dir / "draw_prompt.swift"
        bin_file = tmp_dir / "draw_prompt"
        swift_file.write_text(swift_source, encoding="utf-8")
        sdk = subprocess.check_output(["xcrun", "--sdk", "macosx", "--show-sdk-path"], text=True).strip()
        subprocess.run(
            [
                "xcrun", "--sdk", "macosx", "swiftc",
                "-sdk", sdk, "-swift-version", "5",
                "-module-cache-path", str(tmp_dir / "mc"),
                "-framework", "AppKit", "-framework", "Foundation",
                str(swift_file), "-o", str(bin_file),
            ],
            check=True,
        )
        subprocess.run([str(bin_file), str(png_path)], check=True)


def png_chunk(kind: bytes, payload: bytes) -> bytes:
    return (
        struct.pack(">I", len(payload))
        + kind
        + payload
        + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF)
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("output", nargs="?", default="scripts/macos/dmg-background.png")
    args = parser.parse_args()
    output = Path(args.output)
    raw = render()
    png = (
        b"\x89PNG\r\n\x1a\n"
        + png_chunk(b"IHDR", struct.pack(">IIBBBBB", WIDTH, HEIGHT, 8, 2, 0, 0, 0))
        + png_chunk(b"IDAT", zlib.compress(raw, 9))
        + png_chunk(b"IEND", b"")
    )
    output.write_bytes(png)
    overlay_prompt_macos(output)
    print(f"wrote {output} ({output.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
