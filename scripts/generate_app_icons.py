#!/usr/bin/env python3
"""Generate Libzora's matte book icon and continuous L from one geometry model.

The front page and the single L outline share the same projective transform.
Its vertical and horizontal strokes have equal width in page coordinates;
rounded page corners are also projected, not rounded in screen coordinates.
The L's ink mass is centred with generous whitespace next to the page fold.

Icon Composer receives separate grey/white layers with transparent letter
cutouts. Flat exports use opaque ink, while the tray keeps a black alpha
stencil. No existing PNG is used as a template, so every export is reproducible.

Requires Pillow 11.3.0 for reproducible raster output.
Install: python3 -m pip install Pillow==11.3.0
Run: python3 scripts/generate_app_icons.py
Check: python3 scripts/generate_app_icons.py --check
Preview elsewhere: python3 scripts/generate_app_icons.py --output /tmp/icons
"""
from __future__ import annotations

import argparse
from dataclasses import dataclass
from functools import lru_cache
from io import BytesIO
import json
import math
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageOps

ROOT = Path(__file__).resolve().parents[1]
CANVAS = 1024
SUPERSAMPLE = 4
Point = tuple[float, float]
Projection = tuple[float, ...]

# Rectangular page coordinates -> the four unrounded screen-space corners,
# ordered top-left, top-right, bottom-right, bottom-left.
FRONT_SIZE = (386, 524)
FRONT_CORNERS = ((397, 367), (783, 229), (783, 705), (397, 822))
BACK_SIZE = (200, 530)
BACK_CORNERS = ((235, 294), (435, 211), (435, 808), (235, 738))
# Centre the asymmetric L's ink mass rather than its bounding box, so the
# vertical stroke does not crowd the fold. All dimensions are page-local.
LETTER_ORIGIN = (130, 94)
LETTER_SIZE = (210, 304)
STROKE = 56
LETTER_OUTLINE = (
    (0, 0), (STROKE, 0), (STROKE, LETTER_SIZE[1] - STROKE),
    (LETTER_SIZE[0], LETTER_SIZE[1] - STROKE),
    (LETTER_SIZE[0], LETTER_SIZE[1]), (0, LETTER_SIZE[1]),
)
INK = (15, 17, 19)


def homography(size: tuple[int, int], corners: tuple[Point, ...]) -> Projection:
    """Solve the eight coefficients of a rectangle-to-quadrilateral mapping."""
    width, height = size
    rows = []
    for (u, v), (x, y) in zip(((0, 0), (width, 0), (width, height), (0, height)), corners):
        rows.extend((
            [u, v, 1, 0, 0, 0, -x * u, -x * v, x],
            [0, 0, 0, u, v, 1, -y * u, -y * v, y],
        ))
    for column in range(8):
        pivot = max(range(column, 8), key=lambda row: abs(rows[row][column]))
        rows[column], rows[pivot] = rows[pivot], rows[column]
        divisor = rows[column][column]
        if abs(divisor) < 1e-12:
            raise ValueError("Degenerate page projection")
        rows[column] = [value / divisor for value in rows[column]]
        for row in range(8):
            if row != column:
                factor = rows[row][column]
                rows[row] = [a - factor * b for a, b in zip(rows[row], rows[column])]
    return tuple(row[8] for row in rows)


FRONT_PROJECTION = homography(FRONT_SIZE, FRONT_CORNERS)
BACK_PROJECTION = homography(BACK_SIZE, BACK_CORNERS)


def project(point: Point, matrix: Projection = FRONT_PROJECTION) -> Point:
    u, v = point
    a, b, c, d, e, f, g, h = matrix
    divisor = g * u + h * v + 1
    return ((a * u + b * v + c) / divisor, (d * u + e * v + f) / divisor)


def letter_points() -> list[Point]:
    left, top = LETTER_ORIGIN
    return [project((left + x, top + y)) for x, y in LETTER_OUTLINE]


def rounded_outline(size: tuple[int, int], radii: tuple[int, ...], matrix: Projection) -> list[Point]:
    width, height = size
    tl, tr, br, bl = radii
    arcs = (
        (width - tr, tr, tr, -90), (width - br, height - br, br, 0),
        (bl, height - bl, bl, 90), (tl, tl, tl, 180),
    )
    points = []
    for cx, cy, radius, start in arcs:
        for step in range(33):
            angle = math.radians(start + step * 90 / 32)
            points.append(project((cx + radius * math.cos(angle), cy + radius * math.sin(angle)), matrix))
    return points


def polygon_mask(points: list[Point] | tuple[Point, ...], size: int) -> Image.Image:
    mask = Image.new("L", (size, size))
    scale = size / CANVAS
    ImageDraw.Draw(mask).polygon([(x * scale, y * scale) for x, y in points], fill=255)
    return mask


def gradient(colors: tuple[tuple[int, ...], tuple[int, ...]], mask: Image.Image) -> Image.Image:
    channels = [
        mask.point(lambda value, a=a, b=b: round(a + (b - a) * value / 255))
        for a, b in zip(*colors)
    ]
    return Image.merge("RGB", channels).convert("RGBA")


def clamp(value: float) -> int:
    return max(0, min(255, round(value)))


def surface_edge(image: Image.Image, mask: Image.Image, outline: list[Point], size: int, opacity: int) -> Image.Image:
    edge = Image.new("RGBA", (size, size))
    scale = size / CANVAS
    points = [(x * scale, y * scale) for x, y in outline]
    ImageDraw.Draw(edge).line(points + points[:1], fill=(255, 255, 255, opacity), width=max(1, round(1.5 * scale)))
    edge.putalpha(ImageChops.multiply(edge.getchannel("A"), mask))
    return Image.alpha_composite(image, edge)


def shadow(mask: Image.Image, size: int, offset: Point, blur: float, opacity: float) -> Image.Image:
    alpha = mask.filter(ImageFilter.GaussianBlur(blur * size / CANVAS)).point(lambda value: round(value * opacity))
    result = Image.new("RGBA", (size, size))
    shade = Image.new("RGBA", (size, size))
    shade.putalpha(alpha)
    result.alpha_composite(shade, (round(offset[0] * size / CANVAS), round(offset[1] * size / CANVAS)))
    return result


@lru_cache(maxsize=1)
def masters() -> dict[str, Image.Image]:
    """Rasterize once at 4x; resize/pad these shared masters for each platform."""
    size = CANVAS * SUPERSAMPLE
    vertical = Image.linear_gradient("L").resize((size, size))
    horizontal = vertical.transpose(Image.Transpose.TRANSPOSE)
    back_outline = rounded_outline(BACK_SIZE, (36, 26, 24, 36), BACK_PROJECTION)
    front_outline = rounded_outline(FRONT_SIZE, (40, 48, 48, 32), FRONT_PROJECTION)
    back_mask = polygon_mask(back_outline, size)
    front_mask = polygon_mask(front_outline, size)
    letter = polygon_mask(letter_points(), size)

    back_y = vertical.point(lambda value: clamp((value / 255 * CANVAS - 205) / 650 * 255))
    back_x = horizontal.point(lambda value: clamp((value / 255 * CANVAS - 235) / 200 * 255))
    back_shade = ImageChops.add(
        back_y.point(lambda value: round(value * 0.78)), back_x.point(lambda value: round(value * 0.22)),
    )
    back = gradient(((221, 223, 228), (145, 148, 153)), back_shade)
    back.putalpha(back_mask)
    back = surface_edge(back, back_mask, back_outline, size, 145)

    front_y = vertical.point(lambda value: clamp((value / 255 * CANVAS - 230) / 610 * 255))
    front_x = horizontal.point(lambda value: clamp((783 - value / 255 * CANVAS) / 386 * 255))
    front_shade = ImageChops.add(
        front_y.point(lambda value: round(value * 0.4)), front_x.point(lambda value: round(value * 0.6)),
    )
    front = gradient(((254, 254, 255), (231, 233, 237)), front_shade)
    front.putalpha(front_mask)
    front = surface_edge(front, front_mask, front_outline, size, 125)
    flat_page = front.copy()
    flat_page.paste((*INK, 255), (0, 0, size, size), letter)
    cutout_page = front.copy()
    cutout_page.putalpha(ImageChops.multiply(front_mask, ImageOps.invert(letter)))
    # Retain the page RGB even inside holes until export. Resampling RGB and
    # alpha separately avoids 8-bit premultiplication darkening faint edges.

    plate_mask = Image.new("L", (size, size))
    ImageDraw.Draw(plate_mask).rounded_rectangle(
        (38 * SUPERSAMPLE, 38 * SUPERSAMPLE, 986 * SUPERSAMPLE, 986 * SUPERSAMPLE),
        radius=232 * SUPERSAMPLE, fill=255,
    )
    plate = gradient(((20, 22, 24), (11, 13, 15)), vertical)
    plate.putalpha(plate_mask)
    # A soft edge light, not a hard outline or a glossy reflection.
    rim = Image.new("RGBA", (size, size), (76, 81, 87, 0))
    rim_alpha = ImageChops.subtract(plate_mask, plate_mask.filter(ImageFilter.GaussianBlur(10 * SUPERSAMPLE)))
    rim_alpha = ImageChops.multiply(rim_alpha, vertical.point(lambda value: round(150 - value * 0.35)))
    rim.putalpha(rim_alpha)
    plate = Image.alpha_composite(plate, rim)

    foreground = Image.alpha_composite(shadow(back_mask, size, (0, 8), 9, 0.25), back)
    foreground = Image.alpha_composite(foreground, shadow(front_mask, size, (-6, 8), 12, 0.35))
    foreground = Image.alpha_composite(foreground, flat_page)
    flat = Image.alpha_composite(plate, foreground)
    stencil = Image.new("RGBA", (size, size))
    stencil.putalpha(ImageChops.subtract(ImageChops.lighter(back_mask, front_mask), letter))
    return {"flat": flat, "foreground": foreground, "page": cutout_page, "spine": back, "stencil": stencil}


@dataclass(frozen=True)
class IconTemplate:
    path: str
    size: int
    kind: str = "flat"
    scale: float = 1
    opaque: bool = False


DESKTOP_ICON = "src/Libzora.App/Assets/Icons/libzora.png"
WEBSITE_ICON = "website/assets/libzora.png"
TOUCH_ICON = "website/assets/apple-touch-icon.png"
COMPOSER_CONFIG = "scripts/macos/Libzora.icon/icon.json"
TEMPLATES = (
    IconTemplate(DESKTOP_ICON, 256),
    IconTemplate("src/Libzora.App/Assets/Icons/libzora-dock.png", 1024, scale=0.865),
    IconTemplate("src/Libzora.App/Assets/Icons/tray.png", 128, "stencil"),
    IconTemplate("scripts/macos/Libzora.icon/Assets/Page.png", 1024, "page"),
    IconTemplate("scripts/macos/Libzora.icon/Assets/Spine.png", 1024, "spine"),
    IconTemplate("src/Libzora.Mobile.iOS/Assets.xcassets/AppIcon.appiconset/AppIcon.png", 1024, opaque=True),
    IconTemplate("src/Libzora.Mobile.Android/Resources/drawable-nodpi/app_icon.png", 256),
    IconTemplate("src/Libzora.Mobile.Android/Resources/drawable-nodpi/app_icon_foreground.png", 256, "foreground", 0.9),
    IconTemplate(WEBSITE_ICON, 256),
    IconTemplate(TOUCH_ICON, 180),
)
ICO_VARIANTS = (
    ("src/Libzora.App/Assets/Icons/Libzora.ico", DESKTOP_ICON, (16, 20, 24, 32, 40, 48, 64, 128, 256)),
    ("website/favicon.ico", WEBSITE_ICON, (16, 32, 48, 256)),
)


def render_icon(template: IconTemplate) -> Image.Image:
    original = masters()[template.kind]
    length = round(template.size * template.scale)
    if template.kind == "page":
        image = original.convert("RGB").resize((length, length), Image.Resampling.LANCZOS).convert("RGBA")
        alpha = original.getchannel("A").resize((length, length), Image.Resampling.LANCZOS)
        image.putalpha(alpha)
        image.paste((0, 0, 0, 0), (0, 0, length, length), alpha.point(lambda value: 255 if value == 0 else 0))
    elif template.kind == "stencil":
        image = Image.new("RGBA", (length, length))
        alpha = original.getchannel("A").resize((length, length), Image.Resampling.LANCZOS)
        # Remove very faint (up to 3/255) ringing in empty/full template areas.
        image.putalpha(alpha.point(lambda value: 0 if value <= 3 else 255 if value >= 252 else value))
    else:
        image = original.resize((length, length), Image.Resampling.LANCZOS)
    if template.scale != 1:
        padded = Image.new("RGBA", (template.size, template.size))
        padded.alpha_composite(image, ((template.size - length) // 2, (template.size - length) // 2))
        image = padded
    if template.opaque:
        base = Image.new("RGBA", image.size, (*INK, 255))
        base.alpha_composite(image)
        image = base.convert("RGB")
    return image


def same_pixels(a: Image.Image, b: Image.Image) -> bool:
    return a.size == b.size and a.mode == b.mode and a.tobytes() == b.tobytes()


def encode_ico(image: Image.Image, sizes: tuple[int, ...]) -> bytes:
    data = BytesIO()
    image.save(data, "ICO", sizes=[(size, size) for size in sizes])
    return data.getvalue()


def composer_config() -> dict:
    """Keep Apple's native masks, but share the flat icon's colour and geometry."""
    colour = ",".join(f"{value / 255:.6f}" for value in INK)
    return {
        "fill": {"solid": f"srgb:{colour},1.0"},
        "groups": [{
            "layers": [{
                "image-name": f"{name}.png",
                "name": name,
                "position": {"scale": 1, "translation-in-points": [0, 0]},
            } for name in ("Spine", "Page")],
            "translucency": {"enabled": False, "value": 0.0},
        }],
        "supported-platforms": {"squares": "shared"},
    }


def generate(root: Path, check: bool = False) -> list[str]:
    images = {template.path: render_icon(template) for template in TEMPLATES}
    images[TOUCH_ICON] = images[WEBSITE_ICON].resize((180, 180), Image.Resampling.LANCZOS)
    stale = []
    for path, image in images.items():
        target = root / path
        if check:
            if not target.exists():
                stale.append(path)
                continue
            with Image.open(target) as current:
                if not same_pixels(current, image):
                    stale.append(path)
        else:
            target.parent.mkdir(parents=True, exist_ok=True)
            image.save(target, "PNG", optimize=True)
            print(f"Updated {path}")
    for path, source_path, sizes in ICO_VARIANTS:
        data = encode_ico(images[source_path], sizes)
        target = root / path
        if check:
            if not target.exists():
                stale.append(path)
                continue
            with Image.open(target) as current, Image.open(BytesIO(data)) as expected:
                if current.ico.sizes() != expected.ico.sizes() or any(
                    not same_pixels(current.ico.getimage(size), expected.ico.getimage(size))
                    for size in expected.ico.sizes()
                ):
                    stale.append(path)
        else:
            target.write_bytes(data)
            print(f"Updated {path}")
    target = root / COMPOSER_CONFIG
    config = composer_config()
    if check:
        try:
            current = json.loads(target.read_text(encoding="utf-8"))
        except (FileNotFoundError, json.JSONDecodeError):
            current = None
        if current != config:
            stale.append(COMPOSER_CONFIG)
    else:
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")
        print(f"Updated {COMPOSER_CONFIG}")
    return stale


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT, help="Export into a separate root for previews")
    parser.add_argument("--check", action="store_true", help="Verify all exports without changing files")
    args = parser.parse_args()
    stale = generate(args.output, check=args.check)
    if stale:
        for path in stale:
            print(f"Out-of-date icon: {path}")
        raise SystemExit(1)
    if args.check:
        print("All icon exports match the shared page-space L.")


if __name__ == "__main__":
    main()
