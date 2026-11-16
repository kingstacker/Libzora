#!/usr/bin/env python3
"""Regression checks for independently sourced website screenshots.

Requires Pillow 11.3.0. Run: python3 -I scripts/test_optimize_website_assets.py
"""
import contextlib
from io import StringIO
from pathlib import Path
import runpy
import tempfile
import unittest

from PIL import Image

API = runpy.run_path(str(Path(__file__).with_name("optimize_website_assets.py")))


class WebsiteAssetTests(unittest.TestCase):
    def test_transparent_window_corners_are_composited_on_white(self):
        source = Image.new("RGBA", (4, 4), (0, 0, 0, 0))
        source.putpixel((1, 1), (0, 0, 0, 128))
        source.putpixel((2, 2), (20, 40, 60, 255))
        result = API["flatten_on_white"](source)
        self.assertEqual(result.mode, "RGB")
        self.assertEqual(result.getpixel((0, 0)), (255, 255, 255))
        self.assertEqual(result.getpixel((1, 1)), (127, 127, 127))
        self.assertEqual(result.getpixel((2, 2)), (20, 40, 60))

    def test_optimization_preserves_the_independent_grid_capture_and_icons(self):
        with tempfile.TemporaryDirectory(prefix="libzora-website-assets-") as directory:
            root = Path(directory)
            globals_ = API["main"].__globals__
            original_root = globals_["ROOT"]
            globals_["ROOT"] = root
            try:
                for index, name in enumerate(API["SCREENSHOTS"]):
                    Image.new("RGB", (64, 40), (index * 20, 50, 100)).save(root / f"{name}.png")
                grid = (root / "library-grid.png").read_bytes()
                Image.new("RGB", (180, 180), (1, 2, 3)).save(root / "apple-touch-icon.png")
                icon = (root / "apple-touch-icon.png").read_bytes()
                with contextlib.redirect_stdout(StringIO()):
                    API["main"]()
                with Image.open(root / "library-grid.png") as image:
                    self.assertEqual(image.size, (64, 40))
                    self.assertEqual(image.getpixel((0, 0)), (API["SCREENSHOTS"].index("library-grid") * 20, 50, 100))
                self.assertTrue((root / "library-grid.webp").exists())
                self.assertEqual((root / "apple-touch-icon.png").read_bytes(), icon)
                with Image.open(root / "social-card.jpg") as card:
                    self.assertEqual(card.size, (1200, 630))
                self.assertFalse((root / "library-grid-640.webp").exists())
                self.assertTrue(grid)
            finally:
                globals_["ROOT"] = original_root


if __name__ == "__main__":
    unittest.main()
