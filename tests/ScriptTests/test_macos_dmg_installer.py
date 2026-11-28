#!/usr/bin/env python3
"""Regression tests for the single-icon macOS installer DMG tooling."""
import importlib.util
from pathlib import Path
import plistlib
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


def load_module(name: str, relative_path: str):
    spec = importlib.util.spec_from_file_location(name, ROOT / relative_path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


BACKGROUND = load_module("dmg_background", "scripts/macos/make-dmg-background.py")
DSSTORE = load_module("dmg_dsstore", "scripts/macos/make-dmg-dsstore.py")


def parse_dsstore(data: bytes) -> dict[tuple[str, bytes], tuple[bytes, object]]:
    magic_prefix, bud_magic, root_off, root_size, root_off2 = struct.unpack(">I4sIII", data[:20])
    assert magic_prefix == 1
    assert bud_magic == b"Bud1"
    assert root_off == root_off2

    buf = data[4:]
    num_blocks = struct.unpack(">I", buf[root_off : root_off + 4])[0]
    addrs = struct.unpack(f">{num_blocks}I", buf[root_off + 8 : root_off + 8 + 4 * num_blocks])

    toc_pos = root_off + 8 + 4 * 256
    num_toc = struct.unpack(">I", buf[toc_pos : toc_pos + 4])[0]
    toc_pos += 4
    tocs = {}
    for _ in range(num_toc):
        nlen = buf[toc_pos]
        toc_pos += 1
        tname = buf[toc_pos : toc_pos + nlen].decode("ascii")
        toc_pos += nlen
        blk_id = struct.unpack(">I", buf[toc_pos : toc_pos + 4])[0]
        toc_pos += 4
        tocs[tname] = blk_id

    dsdb_off = addrs[tocs["DSDB"]] & ~0x1F
    root_node, levels, nrecs, nnodes, _ = struct.unpack(">5I", buf[dsdb_off : dsdb_off + 20])
    assert levels == 0 and nnodes == 1

    node_off = addrs[root_node] & ~0x1F
    next_id, count = struct.unpack(">II", buf[node_off : node_off + 8])
    assert next_id == 0 and count == nrecs

    pos = node_off + 8
    entries: dict[tuple[str, bytes], tuple[bytes, object]] = {}
    for _ in range(count):
        nchars = struct.unpack(">I", buf[pos : pos + 4])[0]
        pos += 4
        name = buf[pos : pos + 2 * nchars].decode("utf-16-be")
        pos += 2 * nchars
        code, dtype = struct.unpack(">4s4s", buf[pos : pos + 8])
        pos += 8
        if dtype == b"long":
            value = struct.unpack(">I", buf[pos : pos + 4])[0]
            pos += 4
        elif dtype == b"type":
            value = buf[pos : pos + 4]
            pos += 4
        elif dtype == b"blob":
            blen = struct.unpack(">I", buf[pos : pos + 4])[0]
            pos += 4
            value = buf[pos : pos + blen]
            pos += blen
        else:
            raise AssertionError(f"Unexpected type {dtype!r}")
        entries[(name, code)] = (dtype, value)
    return entries


class MacOSDmgInstallerTests(unittest.TestCase):
    def test_background_dimensions_and_single_center_geometry(self):
        self.assertEqual(BACKGROUND.WIDTH, 520)
        self.assertEqual(BACKGROUND.HEIGHT, 360)
        self.assertEqual((BACKGROUND.ICON_CENTER_X, BACKGROUND.ICON_CENTER_Y), (260.0, 154.0))

    def test_dsstore_encodes_centered_single_icon_and_background_alias(self):
        with tempfile.TemporaryDirectory() as tmp:
            vol = Path(tmp) / "Libzora"
            (vol / ".background").mkdir(parents=True)
            (vol / ".background" / "background.png").write_bytes(b"\x89PNG\r\n\x1a\n")
            (vol / " .app").mkdir()

            raw = DSSTORE.create_dmg_dsstore(vol)
            parsed = parse_dsstore(raw)

            self.assertEqual(parsed[(".", b"vSrn")], (b"long", 1))
            self.assertEqual(parsed[(".", b"vstl")], (b"type", b"icnv"))

            bwsp = plistlib.loads(parsed[(".", b"bwsp")][1])
            self.assertFalse(bwsp["ShowToolbar"])
            self.assertFalse(bwsp["ShowSidebar"])
            self.assertFalse(bwsp["ShowStatusBar"])
            self.assertEqual(bwsp["WindowBounds"], "{{100, 100}, {520, 388}}")

            icvp = plistlib.loads(parsed[(".", b"icvp")][1])
            self.assertEqual(icvp["iconSize"], 128.0)
            self.assertEqual(icvp["backgroundType"], 2)
            self.assertIn(b"background.png", icvp["backgroundImageAlias"])

            iloc_x, iloc_y = struct.unpack(">II", parsed[(" .app", b"Iloc")][1][:8])
            self.assertEqual((iloc_x, iloc_y), (260, 154))

    @unittest.skipUnless(sys.platform == "darwin", "macOS toolchain required")
    def test_native_installer_compiles_signs_and_installs_embedded_app(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            # 1. Build a minimal signed embedded Libzora.app
            embedded_app = root / "src" / "Libzora.app"
            (embedded_app / "Contents" / "MacOS").mkdir(parents=True)
            main_c = root / "app.c"
            main_c.write_text("int main(void) { return 0; }\n", encoding="utf-8")
            subprocess.run(
                ["clang", str(main_c), "-o", str(embedded_app / "Contents" / "MacOS" / "Libzora")],
                check=True,
            )
            with (embedded_app / "Contents" / "Info.plist").open("wb") as fp:
                plistlib.dump(
                    {
                        "CFBundleIdentifier": "io.github.kingstacker.libzora",
                        "CFBundleExecutable": "Libzora",
                        "CFBundleName": "Libzora",
                        "CFBundlePackageType": "APPL",
                        "CFBundleShortVersionString": "1.2.3",
                        "CFBundleVersion": "1.2.3",
                    },
                    fp,
                )
            subprocess.run(["codesign", "--force", "--sign", "-", str(embedded_app / "Contents" / "MacOS" / "Libzora")], check=True, capture_output=True)
            subprocess.run(["codesign", "--force", "--sign", "-", str(embedded_app)], check=True, capture_output=True)

            # 2. Build Install Libzora.app with LibzoraInstaller.swift
            installer_app = root / "Install Libzora.app"
            (installer_app / "Contents" / "MacOS").mkdir(parents=True)
            (installer_app / "Contents" / "Helpers").mkdir(parents=True)
            sdk = subprocess.check_output(["xcrun", "--sdk", "macosx", "--show-sdk-path"], text=True).strip()
            arch = subprocess.check_output(["uname", "-m"], text=True).strip()
            if arch == "arm64":
                target = "arm64-apple-macos12.0"
            else:
                target = "x86_64-apple-macos12.0"
            subprocess.run(
                [
                    "xcrun", "--sdk", "macosx", "swiftc",
                    "-sdk", sdk, "-target", target, "-swift-version", "5",
                    "-module-cache-path", str(root / "mc"),
                    "-framework", "AppKit", "-framework", "Foundation",
                    str(ROOT / "scripts/macos/LibzoraInstaller.swift"),
                    "-o", str(installer_app / "Contents" / "MacOS" / "LibzoraInstaller"),
                ],
                check=True,
            )
            with (installer_app / "Contents" / "Info.plist").open("wb") as fp:
                plistlib.dump(
                    {
                        "CFBundleIdentifier": "io.github.kingstacker.libzora.installer",
                        "CFBundleExecutable": "LibzoraInstaller",
                        "CFBundleName": "Install Libzora",
                        "CFBundlePackageType": "APPL",
                        "CFBundleShortVersionString": "1.2.3",
                        "CFBundleVersion": "1.2.3",
                    },
                    fp,
                )
            subprocess.run(["ditto", str(embedded_app), str(installer_app / "Contents" / "Helpers" / "Libzora.app")], check=True)
            ent = str(ROOT / "scripts/macos/LibzoraInstaller.entitlements")
            subprocess.run(["codesign", "--force", "--entitlements", ent, "--sign", "-", str(installer_app / "Contents" / "MacOS" / "LibzoraInstaller")], check=True, capture_output=True)
            subprocess.run(["codesign", "--force", "--entitlements", ent, "--sign", "-", str(installer_app)], check=True, capture_output=True)

            subprocess.run(["codesign", "--verify", "--deep", "--strict", str(installer_app)], check=True, capture_output=True)
            subprocess.run(["codesign", "--verify", "--deep", "--strict", str(installer_app / "Contents" / "Helpers" / "Libzora.app")], check=True, capture_output=True)

            # 3. Install over an existing app directory and verify result
            dest_apps = root / "Applications"
            dest_app = dest_apps / "Libzora.app"
            (dest_app / "Contents").mkdir(parents=True)
            (dest_app / "Contents" / "stale-marker.txt").write_text("old", encoding="utf-8")

            subprocess.run(
                [
                    str(installer_app / "Contents" / "MacOS" / "LibzoraInstaller"),
                    "--install-to", str(dest_app),
                    "--no-launch", "--no-eject",
                ],
                check=True,
            )
            self.assertFalse((dest_app / "Contents" / "stale-marker.txt").exists())
            self.assertFalse((dest_apps / "Libzora.app.libzora-old").exists())
            self.assertTrue((dest_app / "Contents" / "MacOS" / "Libzora").is_file())
            subprocess.run(["codesign", "--verify", "--deep", "--strict", str(dest_app)], check=True, capture_output=True)


if __name__ == "__main__":
    unittest.main()
