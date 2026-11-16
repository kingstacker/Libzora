"""Build optimized website imagery.

Produces:
  * lossless PNG re-encodes and full-size WebP versions of every screenshot
  * 640w / 1024w WebP variants for responsive ``srcset`` delivery
  * a separately captured library-grid screenshot for the first feature block
  * a 1200x630 social sharing card

Shared app / Apple touch icons are managed by generate_app_icons.py, not here.
Requires Pillow 11.3.0: ``python3 -m pip install Pillow==11.3.0``.
"""

from __future__ import annotations

from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1] / "website" / "assets"

SCREENSHOTS = [
    "ai",
    "ai-en",
    "kindle",
    "kindle-en",
    "library",
    "library-en",
    "library-grid",
    "library-grid-en",
    "reader",
    "reader-en",
    "macos-library",
    "macos-library-en",
    "ios-library",
    "ios-library-en",
]
RESPONSIVE_WIDTHS = (640, 1024)
WEBP_QUALITY = 82


def mb(path: Path) -> float:
    return path.stat().st_size / 1048576


def save_webp(image: Image.Image, path: Path) -> None:
    image.save(path, "WEBP", quality=WEBP_QUALITY, method=6)


def flatten_on_white(image: Image.Image) -> Image.Image:
    """Composite transparency onto white so rounded window corners stay white."""
    if image.mode in ("RGBA", "LA") or "transparency" in image.info:
        rgba = image.convert("RGBA")
        background = Image.new("RGBA", rgba.size, (255, 255, 255, 255))
        background.alpha_composite(rgba)
        return background.convert("RGB")
    return image.convert("RGB")


def build_responsive(name: str, source: Image.Image) -> None:
    for width in RESPONSIVE_WIDTHS:
        if width >= source.width:
            continue
        height = round(source.height * width / source.width)
        variant = source.resize((width, height), Image.Resampling.LANCZOS)
        save_webp(variant, ROOT / f"{name}-{width}.webp")


def main() -> None:
    for name in SCREENSHOTS:
        png = ROOT / f"{name}.png"
        webp = ROOT / f"{name}.webp"
        before = mb(png)

        original = flatten_on_white(Image.open(png))
        save_webp(original, webp)
        original.save(png, "PNG", optimize=True, compress_level=9)
        build_responsive(name, original)

        # The PNG re-encode must stay pixel-identical.
        assert list(Image.open(png).convert("RGB").getdata()) == list(original.getdata()), name
        print(f"{name}: png {before:.2f} -> {mb(png):.2f} MB | webp {mb(webp):.2f} MB")

    # library-grid.png is its own source capture. Do not overwrite it with an
    # OS- and window-size-dependent crop of the hero screenshot.
    library = Image.open(ROOT / "library.png").convert("RGB")

    # Social sharing card (1200x630) derived from the library screenshot.
    card_width, card_height = 1200, 630
    scaled_height = round(library.height * card_width / library.width)
    card = library.resize((card_width, scaled_height), Image.Resampling.LANCZOS)
    top = max(0, (scaled_height - card_height) // 2)
    card = card.crop((0, top, card_width, top + card_height))
    card.save(ROOT / "social-card.jpg", "JPEG", quality=82, optimize=True, progressive=True)
    print(f"social-card: {card.width}x{card.height} | {mb(ROOT / 'social-card.jpg'):.2f} MB")


if __name__ == "__main__":
    main()
