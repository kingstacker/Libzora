#!/usr/bin/env python3
"""Generate a minimal, valid macOS Finder .DS_Store for a single-icon DMG.

This avoids automating Finder over AppleScript so both CI and local builds
produce a centered single-installer DMG window without toolbars or sidebars.
"""
from __future__ import annotations

import argparse
import os
import plistlib
import struct
from pathlib import Path

HFS_EPOCH_OFFSET = 2082844800  # Seconds between 1904-01-01 and 1970-01-01.


def to_hfs_timestamp(unix_seconds: float) -> int:
    value = int(unix_seconds) + HFS_EPOCH_OFFSET
    return max(0, min(0xFFFFFFFF, value))


def encode_alias_v2(volume_root: Path, relative_file: Path) -> bytes:
    """Build a Finder-compatible HFS+ Alias v2 record for the background image."""
    target = volume_root / relative_file
    vol_stat = os.stat(volume_root)
    parent_stat = os.stat(target.parent)
    file_stat = os.stat(target)

    volume_name = volume_root.name
    file_name = target.name
    vol_bytes = volume_name.encode("utf-8")[:27]
    file_bytes = file_name.encode("utf-8")[:63]

    vol_birthtime = getattr(vol_stat, "st_birthtime", vol_stat.st_mtime)
    file_birthtime = getattr(file_stat, "st_birthtime", file_stat.st_mtime)

    extras = bytearray()

    def add_tag(tag: int, payload: bytes) -> None:
        extras.extend(struct.pack(">hH", tag, len(payload)))
        extras.extend(payload)
        if len(payload) % 2 == 1:
            extras.append(0)

    parent_name_bytes = target.parent.name.encode("utf-8")
    add_tag(0, parent_name_bytes)
    add_tag(1, struct.pack(">II", parent_stat.st_ino & 0xFFFFFFFF, file_stat.st_ino & 0xFFFFFFFF))
    file_u16 = file_name.encode("utf-16-be")
    add_tag(14, struct.pack(">H", len(file_name)) + file_u16)
    vol_u16 = volume_name.encode("utf-16-be")
    add_tag(15, struct.pack(">H", len(volume_name)) + vol_u16)
    posix_rel = ("/" + relative_file.as_posix()).encode("utf-8")
    add_tag(18, posix_rel)
    add_tag(19, str(volume_root).encode("utf-8"))
    extras.extend(struct.pack(">hH", -1, 0))

    total_size = 150 + len(extras)
    header = struct.pack(
        ">4sHHH B27s I2sH I B63s II4s4shhIH10s",
        b"\x00\x00\x00\x00",
        total_size,
        2,
        0,
        len(vol_bytes),
        vol_bytes.ljust(27, b"\x00"),
        to_hfs_timestamp(vol_birthtime),
        b"H+",
        5,
        parent_stat.st_ino & 0xFFFFFFFF,
        len(file_bytes),
        file_bytes.ljust(63, b"\x00"),
        file_stat.st_ino & 0xFFFFFFFF,
        to_hfs_timestamp(file_birthtime),
        b"\x00\x00\x00\x00",
        b"\x00\x00\x00\x00",
        -1,
        -1,
        0x00000D02,
        0,
        b"\x00" * 10,
    )
    assert len(header) == 150
    return header + bytes(extras)


def encode_record(name: str, code: bytes, dtype: bytes, value: object) -> bytes:
    assert len(code) == 4 and len(dtype) == 4
    name_u16 = name.encode("utf-16-be")
    buf = bytearray()
    buf.extend(struct.pack(">I", len(name)))
    buf.extend(name_u16)
    buf.extend(code)
    buf.extend(dtype)
    if dtype == b"long":
        buf.extend(struct.pack(">I", int(value)))
    elif dtype == b"type":
        raw = bytes(value)
        assert len(raw) == 4
        buf.extend(raw)
    elif dtype == b"blob":
        raw = bytes(value)
        buf.extend(struct.pack(">I", len(raw)))
        buf.extend(raw)
    else:
        raise ValueError(f"Unsupported DS_Store record type: {dtype!r}")
    return bytes(buf)


def build_dsstore_bytes(records: list[tuple[str, bytes, bytes, object]]) -> bytes:
    # Sort strictly by (lowercase utf-16be filename, 4-char code) as Finder expects.
    ordered = sorted(records, key=lambda r: (r[0].lower(), r[1]))
    node_payload = bytearray(struct.pack(">II", 0, len(ordered)))
    for name, code, dtype, value in ordered:
        node_payload.extend(encode_record(name, code, dtype, value))

    page_size = 4096
    if len(node_payload) > page_size:
        raise ValueError("DS_Store B-tree leaf exceeded single 4096-byte page")

    # Buddy-allocator offsets (relative to byte 4 after the 0x00000001 alignment prefix):
    #   0x0000 .. 0x0020 : Bud1 header (32 bytes)
    #   0x0020 .. 0x0040 : DSDB superblock (32 bytes, block 1 -> 0x0020 | 5)
    #   0x1000 .. 0x2000 : B-tree leaf node (4096 bytes, block 2 -> 0x1000 | 12)
    #   0x2000 .. 0x2800 : Allocator directory (2048 bytes, block 0 -> 0x2000 | 11)
    #   0x2800 .. 0x4000 : Free buddy space (0x2800/11, 0x3000/12)
    body = bytearray(0x4000)

    root_offset = 0x2000
    root_size = 0x800
    body[0:32] = struct.pack(">4sIII16s", b"Bud1", root_offset, root_size, root_offset, b"\x00" * 16)

    dsdb_offset = 0x0020
    body[dsdb_offset : dsdb_offset + 20] = struct.pack(">5I", 2, 0, len(ordered), 1, page_size)

    node_offset = 0x1000
    body[node_offset : node_offset + len(node_payload)] = node_payload

    alloc = bytearray()
    alloc.extend(struct.pack(">II", 3, 0))
    block_addrs = [0x2000 | 11, 0x0020 | 5, 0x1000 | 12]
    alloc.extend(struct.pack(">3I", *block_addrs))
    alloc.extend(b"\x00" * ((256 - len(block_addrs)) * 4))

    # TOC: 1 entry ("DSDB" -> block 1)
    alloc.extend(struct.pack(">IB4sI", 1, 4, b"DSDB", 1))

    # 32 buddy free-lists covering the remaining unallocated regions in 0x0000..0x4000
    free_lists: dict[int, list[int]] = {
        6: [0x0040],
        7: [0x0080],
        8: [0x0100],
        9: [0x0200],
        10: [0x0400],
        11: [0x0800, 0x2800],
        12: [0x3000],
    }
    for power in range(32):
        entries = free_lists.get(power, [])
        alloc.extend(struct.pack(">I", len(entries)))
        for entry in entries:
            alloc.extend(struct.pack(">I", entry))

    if len(alloc) > root_size:
        raise ValueError("Allocator directory exceeded 2048 bytes")
    body[root_offset : root_offset + len(alloc)] = alloc

    return struct.pack(">I", 1) + bytes(body)


def create_dmg_dsstore(
    volume_root: Path,
    item_name: str = " .app",
    background_relative: Path | None = Path(".background/background.png"),
    window_bounds: str = "{{100, 100}, {520, 388}}",
    icon_x: int = 260,
    icon_y: int = 154,
    icon_size: float = 128.0,
) -> bytes:
    bwsp = plistlib.dumps(
        {
            "ContainerShowSidebar": False,
            "ShowPathbar": False,
            "ShowSidebar": False,
            "ShowStatusBar": False,
            "ShowTabView": False,
            "ShowToolbar": False,
            "SidebarWidth": 0,
            "WindowBounds": window_bounds,
        },
        fmt=plistlib.FMT_BINARY,
    )

    icvp_dict: dict[str, object] = {
        "arrangeBy": "none",
        "backgroundColorBlue": 1.0,
        "backgroundColorGreen": 1.0,
        "backgroundColorRed": 1.0,
        "backgroundType": 0,
        "gridOffsetX": 0.0,
        "gridOffsetY": 0.0,
        "gridSpacing": 100.0,
        "iconSize": float(icon_size),
        "labelOnBottom": True,
        "showIconPreview": True,
        "showItemInfo": False,
        "textSize": 13.0,
        "viewOptionsVersion": 1,
    }
    if background_relative is not None and (volume_root / background_relative).is_file():
        icvp_dict["backgroundType"] = 2
        icvp_dict["backgroundImageAlias"] = encode_alias_v2(volume_root, background_relative)

    icvp = plistlib.dumps(icvp_dict, fmt=plistlib.FMT_BINARY)
    iloc = struct.pack(">IIHHHH", icon_x, icon_y, 0xFFFF, 0xFFFF, 0xFFFF, 0x0000)

    records = [
        (".", b"bwsp", b"blob", bwsp),
        (".", b"icvp", b"blob", icvp),
        (".", b"vSrn", b"long", 1),
        (".", b"vstl", b"type", b"icnv"),
        (item_name, b"Iloc", b"blob", iloc),
    ]
    return build_dsstore_bytes(records)


def main() -> None:
    parser = argparse.ArgumentParser(description="Create a single-icon Finder .DS_Store for a DMG volume.")
    parser.add_argument("volume_root", help="Mounted DMG volume path (e.g. /Volumes/Libzora)")
    parser.add_argument("--item", default=" .app")
    parser.add_argument("--background", default=".background/background.png")
    parser.add_argument("--bounds", default="{{100, 100}, {520, 388}}")
    parser.add_argument("--icon-x", type=int, default=260)
    parser.add_argument("--icon-y", type=int, default=154)
    parser.add_argument("--icon-size", type=float, default=128.0)
    args = parser.parse_args()

    volume = Path(args.volume_root).resolve()
    bg = Path(args.background) if args.background else None
    payload = create_dmg_dsstore(
        volume_root=volume,
        item_name=args.item,
        background_relative=bg,
        window_bounds=args.bounds,
        icon_x=args.icon_x,
        icon_y=args.icon_y,
        icon_size=args.icon_size,
    )
    out_file = volume / ".DS_Store"
    out_file.write_bytes(payload)
    print(f"wrote {out_file} ({len(payload)} bytes)")


if __name__ == "__main__":
    main()
