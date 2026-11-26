#!/usr/bin/env python3
"""Run native UI tests and capture visual checkpoints through CoreSimulator."""

import argparse
import json
import re
import struct
import subprocess
import tempfile
from pathlib import Path


def margin_colours(png: Path, collapse_repeats: bool = True,
                   start_fraction: float = 0, end_fraction: float = 1) -> list[str]:
    """Distinct colours, top to bottom, in the right page margin of a capture."""
    with tempfile.TemporaryDirectory() as directory:
        bitmap = Path(directory) / "capture.bmp"
        subprocess.run(["sips", "-s", "format", "bmp", str(png), "--out", str(bitmap)],
                       check=True, capture_output=True)
        data = bitmap.read_bytes()
    offset = struct.unpack_from("<I", data, 10)[0]
    width, height = struct.unpack_from("<ii", data, 18)
    channels = struct.unpack_from("<H", data, 28)[0] // 8
    stride = (width * channels + 3) & ~3
    x = int(width * 0.97)
    colours: list[str] = []
    for y in range(int(abs(height) * start_fraction), int(abs(height) * end_fraction)):
        row = y if height < 0 else abs(height) - 1 - y
        index = offset + row * stride + x * channels
        colour = f"#{data[index + 2]:02X}{data[index + 1]:02X}{data[index]:02X}"
        if not collapse_repeats or not colours or colours[-1] != colour:
            colours.append(colour)
    return colours


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--simulator", required=True)
    parser.add_argument("--results", type=Path, required=True, help="New output directory")
    parser.add_argument("--only", action="append", help="Optional XCTest method name; repeat to select several methods")
    parser.add_argument("--suite", default="NativeFrontendTests",
                        help="XCTest class selected by --only (default: NativeFrontendTests)")
    args = parser.parse_args()
    output = args.results.resolve()
    if output.exists():
        parser.error("The results directory must not already exist.")
    output.mkdir(parents=True)
    screenshots = output / "screenshots"
    screenshots.mkdir()
    project = Path(__file__).resolve().parent / "NativeFrontendUITests.xcodeproj"
    command = [
        "xcodebuild", "test", "-project", str(project), "-scheme", "NativeFrontendUITests",
        "-destination", f"id={args.simulator}",
        "-derivedDataPath", str(output / "DerivedData"),
        "-resultBundlePath", str(output / "NativeFrontend.xcresult"),
        "-parallel-testing-enabled", "NO", "CODE_SIGNING_ALLOWED=YES", "CODE_SIGN_IDENTITY=-",
    ]
    if args.only:
        for method in args.only:
            command.append(f"-only-testing:NativeFrontendUITests/{args.suite}/{method}")
    else:
        # Media rotation uses a separate fixture bundle and must be selected
        # explicitly after prepare-reader-media-preview.py.
        command.append("-skip-testing:NativeFrontendUITests/ReaderMediaTests")
        # Footnote compatibility also uses a separate, explicitly prepared bundle.
        command.append("-skip-testing:NativeFrontendUITests/ReaderFootnoteTests")
        # Layout compatibility uses its own isolated bundle as well.
        command.append("-skip-testing:NativeFrontendUITests/ReaderLayoutTests")
        # Review regressions need the fictional AI/dictionary/PDF-zoom fixture
        # described in the class doc comment; select them with --only.
        command.append("-skip-testing:NativeFrontendUITests/NativeReviewRegressionTests")
        # Statistics/entry captures require their own covered-book demo bundle.
        command.append("-skip-testing:NativeFrontendUITests/NativeReadingStatsTests")
        command.append("-skip-testing:NativeFrontendUITests/NativeDataSafetyUITests")
        # Home-screen widgets require explicitly installed preview widgets and
        # prepared pages, so do not run this visual suite with app-only fixtures.
        command.append("-skip-testing:NativeFrontendUITests/WidgetHomeScreenTests")
        # Upgrade setup deliberately uses another preview installation and is
        # run separately after the host has installed the legacy then native UI.
        command.append("-skip-testing:NativeFrontendUITests/NativeFrontendTests/testLegacyUpgradePreservesLibraryAndPreferences")
        # The scroll test requires a separately populated, long preview library.
        command.append("-skip-testing:NativeFrontendUITests/NativeFrontendTests/testLibraryHeaderReturnsWhenScrollingDown")
        command.append("-skip-testing:NativeFrontendUITests/NativeFrontendTests/testLibraryBottomBounceDoesNotRevealHeader")
        command.append("-skip-testing:NativeFrontendUITests/NativeFrontendTests/testColorfulBackgroundChangesPaletteWhileScrolling")
        # The account scenario is selected explicitly with --only after both
        # regional localhost servers have been started with disposable data.
        command.append("-skip-testing:NativeFrontendUITests/NativeFrontendTests/testRegionalAccountRegistrationAndFreeSyncRestriction")
        command.append("-skip-testing:NativeFrontendUITests/NativeFrontendTests/testAccountMembershipDevicesAndRightsRefresh")
        command.append("-skip-testing:NativeFrontendUITests/NativeFrontendTests/testTrialStaysUnclaimedUntilTheSwitchIsTurnedOn")
    captures = []
    with (output / "xcodebuild.log").open("w") as log:
        process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                   text=True, encoding="utf-8", errors="replace", bufsize=1)
        assert process.stdout is not None
        for line in process.stdout:
            log.write(line)
            if "LIBZORA_VISUAL_CHECKPOINT:" in line:
                name = line.split("LIBZORA_VISUAL_CHECKPOINT:", 1)[1].strip()
                safe_name = re.sub(r"[^\w.-]", "_", name)
                filename = f"{len(captures) + 1:02d}-{safe_name}.png"
                result = subprocess.run(["xcrun", "simctl", "io", args.simulator, "screenshot", str(screenshots / filename)],
                                        capture_output=True, text=True)
                captures.append({"name": name, "file": filename, "captured": result.returncode == 0})
                print(f"Visual checkpoint: {name}; captured={result.returncode == 0}", flush=True)
            elif "Test Case '" in line or "error:" in line or "** TEST" in line or "LIBZORA_NATIVE_UI_REVIEW_" in line:
                print(line.strip(), flush=True)
        status = process.wait()
    # The SwiftUI paper above and below the page must match the page drawn by
    # C#. A different colour shows up as extra bands in the page margin.
    for capture in captures:
        if capture["name"].startswith("纸张-") and capture["captured"]:
            colours = margin_colours(screenshots / capture["file"])
            capture["marginColours"] = colours
            print(f"Paper margin {capture['name']}: {' '.join(colours)}", flush=True)
            if len(colours) != 1:
                print(f"error: {capture['file']} shows paper bands around the page.", flush=True)
                status = status or 1
    for capture in captures:
        if capture["name"].startswith("纸张跟随-") and capture["captured"]:
            colours = margin_colours(screenshots / capture["file"], collapse_repeats=False)
            pixels = [tuple(int(colour[index:index + 2], 16) for index in (1, 3, 5)) for colour in colours]
            # Average neighbouring rows to remove screenshot colour dithering;
            # retain repeated rows so a solid footer band cannot be averaged away.
            means = [tuple(sum(pixel[channel] for pixel in pixels[index:index + 32])
                           / len(pixels[index:index + 32]) for channel in range(3))
                     for index in range(0, len(pixels), 32)]
            largest_jump = max((max(abs(a - b) for a, b in zip(left, right))
                                for left, right in zip(means, means[1:])), default=0)
            distinct_colours = len(set(colours))
            capture["marginColourCount"] = distinct_colours
            capture["largestMarginJump"] = largest_jump
            print(f"Following paper: {distinct_colours} margin colours; largest averaged jump={largest_jump:.2f}", flush=True)
            if distinct_colours < 2 or largest_jump > 3:
                print(f"error: {capture['file']} must follow the colourful background without a footer seam.", flush=True)
                status = status or 1
            if capture["name"] == "纸张跟随-从黑切回":
                # This scenario explicitly selects the app's light appearance;
                # a seamless but entirely dark page must not pass the regression.
                minimum_brightness = min((sum(pixel) / 3 for pixel in pixels), default=0)
                capture["minimumMarginBrightness"] = minimum_brightness
                if minimum_brightness <= 128:
                    print(f"error: {capture['file']} must return to the app's light paper after ink.", flush=True)
                    status = status or 1
    for capture in captures:
        if capture["name"].startswith("目录纸张-") and capture["captured"]:
            colours = margin_colours(screenshots / capture["file"], start_fraction=0.25, end_fraction=0.9)
            expected = {"米": "#F4F2EC", "黑": "#0E0E0E", "白": "#FFFFFF"}[capture["name"].split("-")[-1]]
            capture["marginColours"] = colours
            print(f"TOC paper: {' '.join(colours)}; expected={expected}", flush=True)
            if colours != [expected]:
                print(f"error: {capture['file']} TOC must use the reader's selected paper.", flush=True)
                status = status or 1
    (screenshots / "manifest.json").write_text(json.dumps(captures, ensure_ascii=False, indent=2))
    print(f"UI results: {output}; exit={status}")
    return status


if __name__ == "__main__":
    raise SystemExit(main())
