#!/usr/bin/env python3
"""Install a separate reader-media fixture without touching the populated preview."""
import argparse
import hashlib
from pathlib import Path
import plistlib
import shutil
import sqlite3
import struct
import subprocess
import tempfile
import uuid
import zipfile
import zlib

BUNDLE = "com.kingstacker.libzora.readermediapreview"
BOOK_ID = "6e15ab09-8381-45dd-8d5f-2bbd4e9b56da"
FILE_ID = "bd08e5e1-3980-4873-a45f-71b4871231de"
TITLE = "Reader Media Fixture"


def run(*args):
    return subprocess.check_output(args, text=True).strip()


def fixture_image():
    width, height = 600, 900
    raw = bytearray()
    for y in range(height):
        raw.append(0)
        for x in range(width):
            border = x < 16 or x >= width - 16 or y < 16 or y >= height - 16
            centre = width // 3 <= x < width * 2 // 3 and height // 3 <= y < height * 2 // 3
            colour = (18, 48, 68) if border else (244, 179, 79) if centre else (92, 168 + y // 24, 208)
            raw.extend(colour)
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xffffffff)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b""))


def make_epub(path, image):
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("mimetype", "application/epub+zip", compress_type=zipfile.ZIP_STORED)
        archive.writestr("META-INF/container.xml", '<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>')
        archive.writestr("book.opf", f'''<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">{BOOK_ID}</dc:identifier><dc:title>{TITLE}</dc:title><dc:language>zh-CN</dc:language></metadata><manifest><item id="figure" href="figure.xhtml" media-type="application/xhtml+xml"/><item id="text" href="text.xhtml" media-type="application/xhtml+xml"/><item id="image" href="figure.png" media-type="image/png" properties="cover-image"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest><spine><itemref idref="figure"/><itemref idref="text"/></spine></package>''')
        archive.writestr("figure.xhtml", '<html xmlns="http://www.w3.org/1999/xhtml"><head><title>测试插图</title></head><body><p style="margin:0;text-align:center"><img src="figure.png" width="600" height="900" alt="测试插图"/></p></body></html>')
        archive.writestr("text.xhtml", '<html xmlns="http://www.w3.org/1999/xhtml"><head><title>翻页后的正文</title></head><body><p>这里是图片之后的正文，用于确认滑动翻页不会误开图片。</p></body></html>')
        archive.writestr("nav.xhtml", '<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>目录</title></head><body><nav epub:type="toc"><ol><li><a href="figure.xhtml">测试插图</a></li><li><a href="text.xhtml">翻页后的正文</a></li></ol></nav></body></html>')
        archive.writestr("figure.png", image)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", type=Path, required=True)
    parser.add_argument("--simulator", required=True)
    args = parser.parse_args()
    source = args.app.resolve(strict=True)
    source_info = plistlib.loads((source / "Info.plist").read_bytes())
    if not (source / source_info["CFBundleExecutable"]).is_file():
        parser.error("The source app is incomplete: its bundle executable is missing.")
    output = Path(tempfile.mkdtemp(prefix="libzora-reader-media-"))
    app = output / "ReaderMediaPreview.app"
    shutil.copytree(source, app)
    def rename(bundle, identifier, display=None):
        path = bundle / "Info.plist"
        info = plistlib.loads(path.read_bytes())
        info["CFBundleIdentifier"] = identifier
        if display:
            info["CFBundleDisplayName"] = display
        path.write_bytes(plistlib.dumps(info, fmt=plistlib.FMT_BINARY))
        run("codesign", "--force", "--preserve-metadata=entitlements", "--identifier", identifier, "--sign", "-", str(bundle))
    widget = app / "PlugIns/LibzoraWidgets.appex"
    if widget.exists():
        rename(widget, BUNDLE + ".widgets")
    rename(app, BUNDLE, "Reader Media Preview")
    subprocess.run(["xcrun", "simctl", "terminate", args.simulator, BUNDLE], capture_output=True)
    run("xcrun", "simctl", "install", args.simulator, str(app))
    container = Path(run("xcrun", "simctl", "get_app_container", args.simulator, BUNDLE, "data"))
    data = container / "Documents/Libzora/data"
    library = data / "library" / uuid.UUID(BOOK_ID).hex
    covers = data / "covers"
    library.mkdir(parents=True, exist_ok=True)
    covers.mkdir(parents=True, exist_ok=True)
    image = fixture_image()
    cover = covers / "reader-media.png"
    cover.write_bytes(image)
    epub = library / "reader-media.epub"
    make_epub(epub, image)
    with sqlite3.connect(data / "libzora.db") as db:
        if db.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='ReaderProgress'").fetchone():
            db.execute("DELETE FROM ReaderProgress WHERE BookFileId = ?", (FILE_ID,))
        db.executescript('''CREATE TABLE IF NOT EXISTS Books (
            Id TEXT PRIMARY KEY, Title TEXT NOT NULL, Authors TEXT NOT NULL,
            Series TEXT NULL, SeriesIndex REAL NULL, Description TEXT NULL,
            Publisher TEXT NULL, PublishDate TEXT NULL, Isbn TEXT NULL,
            PageCount TEXT NULL, Binding TEXT NULL, OnlineRating REAL NULL,
            OnlineRatingCount INTEGER NULL, Tags TEXT NOT NULL DEFAULT '',
            Category TEXT NOT NULL DEFAULT '', IsFavorite INTEGER NOT NULL DEFAULT 0,
            ReadingStatus INTEGER NOT NULL DEFAULT 0, CoverPath TEXT NULL,
            CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS BookFiles (
            Id TEXT PRIMARY KEY, BookId TEXT NOT NULL REFERENCES Books(Id) ON DELETE CASCADE,
            Format TEXT NOT NULL, RelativePath TEXT NOT NULL, Size INTEGER NOT NULL,
            Sha256 TEXT NOT NULL UNIQUE, SyncUpdatedAt TEXT NULL);''')
        now = "2026-11-15T00:00:00+00:00"
        db.execute("INSERT OR REPLACE INTO Books (Id,Title,Authors,CoverPath,CreatedAt,UpdatedAt) VALUES (?,?,?,?,?,?)",
                   (BOOK_ID, TITLE, "UI Test", str(cover.relative_to(data)), now, now))
        db.execute("INSERT OR REPLACE INTO BookFiles (Id,BookId,Format,RelativePath,Size,Sha256) VALUES (?,?,?,?,?,?)",
                   (FILE_ID, BOOK_ID, "epub", str(epub.relative_to(data)), epub.stat().st_size, hashlib.sha256(epub.read_bytes()).hexdigest()))
    print(f"Installed isolated {BUNDLE}; fixture: {TITLE}; artifacts: {output}")


if __name__ == "__main__":
    main()
