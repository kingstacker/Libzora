#!/usr/bin/env python3
"""Regression checks for the continuous L, page perspective and icon exports.

Requires Pillow. Run: python3 scripts/test_generate_app_icons.py
"""
import contextlib
from io import StringIO
import json
import math
from pathlib import Path
import runpy
import tempfile
import unittest

from PIL import Image

API = runpy.run_path(str(Path(__file__).with_name("generate_app_icons.py")))
ROOT = API["ROOT"]
PAGE = next(template for template in API["TEMPLATES"] if template.kind == "page")
IOS = next(template for template in API["TEMPLATES"] if template.opaque)


def ink_samples():
    left, top = API["LETTER_ORIGIN"]
    width, height = API["LETTER_SIZE"]
    stroke = API["STROKE"]
    return [API["project"]((left + x, top + y)) for x, y in (
        (stroke / 2, stroke / 2), (stroke / 2, height / 2),
        (stroke / 2, height - stroke / 2), (width - stroke / 2, height - stroke / 2),
    )]


def paper_samples():
    left, top = API["LETTER_ORIGIN"]
    width, height = API["LETTER_SIZE"]
    stroke = API["STROKE"]
    return [API["project"](point) for point in (
        (left - 25, top + height / 2),  # Fold-side whitespace.
        (left + stroke + 20, top + height - stroke - 20),
        (left + width - stroke / 2, top + height / 2),
        (left + width / 2, top + height + 24),
    )]


def glyph_box(size=1024, padding=0):
    points = API["letter_points"]()
    return (math.floor(min(x for x, _ in points) * size / 1024) - padding,
            math.floor(min(y for _, y in points) * size / 1024) - padding,
            math.ceil(max(x for x, _ in points) * size / 1024) + padding,
            math.ceil(max(y for _, y in points) * size / 1024) + padding)


def pixel_at(image, point):
    return image.getpixel(tuple(round(value * image.width / 1024) for value in point))


def ink_components(image):
    image = image.crop(glyph_box(image.width))
    remaining = {(x, y) for y in range(image.height) for x in range(image.width)
                 if max(image.getpixel((x, y))[:3]) < 96}
    components = []
    while remaining:
        queue = [remaining.pop()]
        count = 0
        while queue:
            x, y = queue.pop()
            count += 1
            for neighbour in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
                if neighbour in remaining:
                    remaining.remove(neighbour)
                    queue.append(neighbour)
        components.append(count)
    return components


class AppIconGenerationTests(unittest.TestCase):
    def test_projection_maps_all_page_corners(self):
        for name in ("FRONT", "BACK"):
            width, height = API[f"{name}_SIZE"]
            for point, expected in zip(((0, 0), (width, 0), (width, height), (0, height)), API[f"{name}_CORNERS"]):
                actual = API["project"](point, API[f"{name}_PROJECTION"])
                for a, b in zip(actual, expected):
                    self.assertAlmostEqual(a, b, places=7)

    def test_l_is_one_outline_with_equal_page_space_strokes(self):
        width, height = API["LETTER_SIZE"]
        stroke = API["STROKE"]
        expected = ((0, 0), (stroke, 0), (stroke, height - stroke),
                    (width, height - stroke), (width, height), (0, height))
        self.assertEqual(API["LETTER_OUTLINE"], expected)
        left, top = API["LETTER_ORIGIN"]
        self.assertEqual(API["letter_points"](), [API["project"]((left + x, top + y)) for x, y in expected])
        self.assertGreater(stroke, 0)
        self.assertLess(stroke, min(width, height))

    def test_ink_mass_is_centred_with_generous_fold_side_whitespace(self):
        left, top = API["LETTER_ORIGIN"]
        width, height = API["LETTER_SIZE"]
        stroke = API["STROKE"]
        page_width, page_height = API["FRONT_SIZE"]
        area = stroke * height + stroke * width - stroke * stroke
        centroid_x = (stroke * height * stroke / 2 + stroke * width * width / 2
                      - stroke * stroke * stroke / 2) / area
        self.assertLess(abs(left + centroid_x - page_width / 2), 1)
        self.assertGreaterEqual(left / page_width, 0.30)
        self.assertGreaterEqual((page_width - left - width) / page_width, 0.10)
        self.assertGreater(top, 0)
        self.assertLess(top + height, page_height)

    def test_foot_and_stem_follow_the_front_page_rulings(self):
        points = API["letter_points"]()
        for a, b in ((0, 5), (1, 2), (3, 4)):
            self.assertAlmostEqual(points[a][0], points[b][0], places=7)
        left, top = API["LETTER_ORIGIN"]
        _, height = API["LETTER_SIZE"]
        for indices, y in (((2, 3), top + height - API["STROKE"]), ((5, 4), top + height)):
            a = API["project"]((0, y))
            b = API["project"]((API["FRONT_SIZE"][0], y))
            for index in indices:
                point = points[index]
                cross = (b[0] - a[0]) * (point[1] - a[1]) - (b[1] - a[1]) * (point[0] - a[0])
                self.assertAlmostEqual(cross, 0, places=6)
            self.assertLess(b[1], a[1])

    def test_composer_letter_is_transparent_and_surrounding_paper_opaque(self):
        result = API["render_icon"](PAGE)
        self.assertEqual(result.mode, "RGBA")
        for point in ink_samples():
            self.assertEqual(pixel_at(result, point), (0, 0, 0, 0), "Glass icons need a hole, not opaque ink.")
        for point in paper_samples():
            self.assertEqual(pixel_at(result, point)[3], 255)
            self.assertGreaterEqual(min(pixel_at(result, point)[:3]), 220)

    def test_cutout_edges_do_not_have_dark_rgb_halos(self):
        result = API["render_icon"](PAGE)
        edges = [pixel for pixel in result.crop(glyph_box(padding=2)).getdata() if 0 < pixel[3] < 255]
        self.assertTrue(edges)
        self.assertTrue(all(min(pixel[:3]) >= 220 for pixel in edges))

    def test_flat_ios_icon_is_rgb_with_opaque_ink(self):
        result = API["render_icon"](IOS)
        self.assertEqual((result.size, result.mode), ((1024, 1024), "RGB"))
        for point in ink_samples():
            self.assertEqual(pixel_at(result, point), API["INK"])
        for point in paper_samples():
            self.assertGreaterEqual(min(pixel_at(result, point)), 220)

    def test_tray_is_a_black_template_with_a_transparent_l(self):
        tray = next(template for template in API["TEMPLATES"] if template.kind == "stencil")
        result = API["render_icon"](tray)
        self.assertEqual((result.size, result.mode), ((128, 128), "RGBA"))
        self.assertTrue(all(pixel[:3] == (0, 0, 0) for pixel in result.getdata()))
        for point in ink_samples():
            self.assertEqual(pixel_at(result, point)[3], 0)
        for point in paper_samples():
            self.assertGreater(pixel_at(result, point)[3], 240)

    def test_letter_stays_connected_at_small_launcher_sizes(self):
        for size in (32, 64, 128, 256):
            with self.subTest(size=size):
                result = API["render_icon"](API["IconTemplate"]("test", size))
                components = ink_components(result)
                self.assertEqual(len(components), 1)
                self.assertGreater(components[0], 3)

    def test_platform_padding_and_sizes_are_preserved(self):
        for template in API["TEMPLATES"]:
            with self.subTest(path=template.path):
                result = API["render_icon"](template)
                self.assertEqual(result.size, (template.size, template.size))
                self.assertEqual(result.mode, "RGB" if template.opaque else "RGBA")
                if template.scale != 1:
                    bounds = result.getchannel("A").getbbox()
                    self.assertGreater(bounds[0], 0)
                    self.assertLess(bounds[2], template.size)

    def test_plate_and_book_pages_have_rounded_outer_corners(self):
        desktop = API["render_icon"](API["IconTemplate"]("test", 1024))
        for point in ((38, 38), (986, 38), (986, 986), (38, 986)):
            self.assertEqual(desktop.getpixel(point)[3], 0)
        self.assertEqual(desktop.getpixel((512, 50))[3], 255)
        for kind, corners in (("page", API["FRONT_CORNERS"]), ("spine", API["BACK_CORNERS"])):
            page = API["render_icon"](API["IconTemplate"]("test", 1024, kind))
            for point in corners:
                self.assertEqual(pixel_at(page, point)[3], 0, "A page corner must not become a square vertex.")

    def test_composer_layers_use_the_new_canvas_without_double_scaling(self):
        config = json.loads((ROOT / "scripts/macos/Libzora.icon/icon.json").read_text())
        layers = config["groups"][0]["layers"]
        self.assertEqual([layer["image-name"] for layer in layers], ["Spine.png", "Page.png"])
        for layer in layers:
            self.assertEqual(layer["position"]["scale"], 1)
            self.assertEqual(layer["position"]["translation-in-points"], [0, 0])
        rgba = [float(value) for value in config["fill"]["solid"].removeprefix("srgb:").split(",")]
        for actual, expected in zip(rgba[:3], API["INK"]):
            self.assertLess(abs(actual * 255 - expected), 1)
        self.assertEqual(rgba[3], 1)

    def test_ico_exports_keep_every_existing_frame(self):
        for path, _, sizes in API["ICO_VARIANTS"]:
            with self.subTest(path=path), Image.open(ROOT / path) as icon:
                self.assertEqual(icon.ico.sizes(), {(size, size) for size in sizes})

    def test_generation_is_template_free_and_byte_deterministic(self):
        with tempfile.TemporaryDirectory(prefix="libzora-icon-test-") as directory, contextlib.redirect_stdout(StringIO()):
            root = Path(directory)
            API["generate"](root)
            before = {str(path.relative_to(root)): path.read_bytes() for path in root.rglob("*") if path.is_file()}
            API["generate"](root)
            after = {str(path.relative_to(root)): path.read_bytes() for path in root.rglob("*") if path.is_file()}
            self.assertEqual(before, after)
            self.assertEqual(API["generate"](root, check=True), [])
            target = root / API["DESKTOP_ICON"]
            with Image.open(target) as icon:
                changed = icon.copy()
            changed.putpixel((128, 128), (255, 0, 0, 255))
            changed.save(target)
            self.assertEqual(API["generate"](root, check=True), [API["DESKTOP_ICON"]])

    def test_checker_reports_missing_exports(self):
        with tempfile.TemporaryDirectory(prefix="libzora-icon-test-") as directory:
            missing = API["generate"](Path(directory), check=True)
            self.assertEqual(set(missing), {template.path for template in API["TEMPLATES"]}
                             | {path for path, _, _ in API["ICO_VARIANTS"]} | {API["COMPOSER_CONFIG"]})

    def test_checker_reports_composer_settings_drift(self):
        with tempfile.TemporaryDirectory(prefix="libzora-icon-config-test-") as directory, contextlib.redirect_stdout(StringIO()):
            root = Path(directory)
            API["generate"](root)
            path = root / API["COMPOSER_CONFIG"]
            config = json.loads(path.read_text())
            config["groups"][0]["layers"][0]["position"]["scale"] = 1.2
            path.write_text(json.dumps(config))
            self.assertEqual(API["generate"](root, check=True), [API["COMPOSER_CONFIG"]])
            path.write_text("invalid json")
            self.assertEqual(API["generate"](root, check=True), [API["COMPOSER_CONFIG"]])

    def test_all_checked_in_exports_match_the_generator(self):
        self.assertEqual(API["generate"](ROOT, check=True), [])


if __name__ == "__main__":
    unittest.main()
