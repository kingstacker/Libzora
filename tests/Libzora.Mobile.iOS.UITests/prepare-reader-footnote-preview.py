#!/usr/bin/env python3
"""Install isolated EPUB footnote fixtures, without copying or resetting user data."""
import argparse
import hashlib
import json
from pathlib import Path
import plistlib
import shutil
import sqlite3
import subprocess
import tempfile
import uuid
import zipfile

BUNDLE = "com.kingstacker.libzora.readerfootnotepreview"
FIXTURES = (
    ("旧式锚点", "<div><p><span class='math-super'><a id='z1' href='notes.xhtml#bz1'>[1]</a></span>原文正文。</p>"
     "<p>首段编号不应隐藏整个外层容器，这段后文也必须保留。</p></div>",
     "<p class='zhusi'><a id='bz1' href='body.xhtml#z1'>[1]</a>旧式锚点的完整注释。</p>"),
    ("跨文件多段", "<p>原文<a id='source' href='notes.xhtml#note-label'>[1]</a>。</p>",
     "<section epub:type='endnotes'><aside epub:type='footnote' id='note1'>"
     "<p><a id='note-label' href='body.xhtml#source' role='doc-backlink'>[1]</a>第一段注释内容。</p>"
     "<p>第二段注释内容。<br/>换行后的内容也应保留。</p></aside>"
     "<aside epub:type='footnote' id='note2'><p>不属于本条的另一条注释。</p></aside></section>"),
    ("图片数据", "<p>原文<a epub:type='noteref' href='notes.xhtml#note1'><img class='duokan-footnote' "
     "src='cover.jpg' alt='' data-footnote-text='图片数据属性中的完整注释。'/></a>。</p>",
     "<aside epub:type='footnote' id='note1'><p>图片数据属性中的完整注释。</p></aside>"),
    ("普通链接", "<p>普通<a href='notes.xhtml#chapter2'>章节链接</a>。</p>",
     "<h2 id='chapter2'>第二章</h2><p>这是普通章节，不是脚注。</p>"),
)


def run(*args):
    return subprocess.check_output(args, text=True).strip()


def covers_from(directory):
    covers = []
    for path in sorted(directory.glob("*.epub")):
        with zipfile.ZipFile(path) as book:
            image = next((item for item in book.infolist()
                          if "cover" in item.filename.lower()
                          and item.filename.lower().endswith((".jpg", ".jpeg"))
                          and item.file_size < 8_000_000), None)
            if image is not None:
                covers.append(book.read(image))
        if len(covers) == len(FIXTURES):
            return covers
    raise ValueError("Provide at least four public-domain EPUBs with JPEG book covers.")


def make_epub(path, title, body, notes, image):
    def page(content):
        return ("<html xmlns='http://www.w3.org/1999/xhtml' xmlns:epub='http://www.idpf.org/2007/ops'>"
                "<head><title>" + title + "</title><style>.math-super { vertical-align:super; font-size:.75em; }"
                "p { text-indent:0; margin:0 0 1em; }</style></head><body>" + content + "</body></html>")
    with zipfile.ZipFile(path, "w") as book:
        book.writestr("mimetype", "application/epub+zip", compress_type=zipfile.ZIP_STORED)
        book.writestr("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' "
                      "version='1.0'><rootfiles><rootfile full-path='book.opf' media-type='application/oebps-package+xml'/>"
                      "</rootfiles></container>")
        book.writestr("book.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'>"
                      "<metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>" + title + "</dc:identifier>"
                      "<dc:title>" + title + "</dc:title><dc:language>zh-CN</dc:language></metadata><manifest>"
                      "<item id='body' href='body.xhtml' media-type='application/xhtml+xml'/>"
                      "<item id='notes' href='notes.xhtml' media-type='application/xhtml+xml'/>"
                      "<item id='cover' href='cover.jpg' media-type='image/jpeg' properties='cover-image'/>"
                      "<item id='nav' href='nav.xhtml' media-type='application/xhtml+xml' properties='nav'/>"
                      "</manifest><spine><itemref idref='body'/><itemref idref='notes'/></spine></package>")
        book.writestr("body.xhtml", page(body))
        book.writestr("notes.xhtml", page(notes))
        book.writestr("nav.xhtml", page("<nav epub:type='toc'><ol><li><a href='body.xhtml'>正文</a></li>"
                                        "<li><a href='notes.xhtml'>原注</a></li></ol></nav>"))
        book.writestr("cover.jpg", image)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", required=True, type=Path)
    parser.add_argument("--simulator", required=True)
    parser.add_argument("--covers-from", type=Path, default=Path(__file__).resolve().parents[2] / "公版书")
    args = parser.parse_args()
    source = args.app.resolve(strict=True)
    info = plistlib.loads((source / "Info.plist").read_bytes())
    if not (source / info["CFBundleExecutable"]).is_file():
        parser.error("The source app is missing its executable.")
    covers = covers_from(args.covers_from.resolve(strict=True))
    output = Path(tempfile.mkdtemp(prefix="libzora-reader-footnotes-"))
    preview = output / "ReaderFootnotePreview.app"
    shutil.copytree(source, preview)

    def rename(bundle, identifier, display=None):
        path = bundle / "Info.plist"
        metadata = plistlib.loads(path.read_bytes())
        metadata["CFBundleIdentifier"] = identifier
        if display:
            metadata["CFBundleDisplayName"] = display
        path.write_bytes(plistlib.dumps(metadata, fmt=plistlib.FMT_BINARY))
        run("codesign", "--force", "--preserve-metadata=entitlements", "--identifier", identifier, "--sign", "-", str(bundle))

    widget = preview / "PlugIns/LibzoraWidgets.appex"
    if widget.exists():
        rename(widget, BUNDLE + ".widgets")
    rename(preview, BUNDLE, "脚注回归")
    subprocess.run(["xcrun", "simctl", "terminate", args.simulator, BUNDLE], capture_output=True)
    run("xcrun", "simctl", "install", args.simulator, str(preview))
    container = Path(run("xcrun", "simctl", "get_app_container", args.simulator, BUNDLE, "data"))
    app_root = container / "Documents/Libzora"
    data = app_root / "data"
    data.mkdir(parents=True, exist_ok=True)
    (data / "covers").mkdir(exist_ok=True)
    with sqlite3.connect(data / "libzora.db") as db:
        db.executescript("""CREATE TABLE IF NOT EXISTS Books (
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
            Sha256 TEXT NOT NULL UNIQUE, SyncUpdatedAt TEXT NULL);""")
        has_progress = db.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='ReaderProgress'").fetchone()
        for index, ((label, body, notes), image) in enumerate(zip(FIXTURES, covers)):
            title = "脚注回归·" + label
            book_id = str(uuid.uuid5(uuid.NAMESPACE_URL, BUNDLE + "/book/" + label))
            file_id = str(uuid.uuid5(uuid.NAMESPACE_URL, BUNDLE + "/file/" + label))
            library = data / "library" / uuid.UUID(book_id).hex
            library.mkdir(parents=True, exist_ok=True)
            cover = data / "covers" / (str(index) + ".jpg")
            cover.write_bytes(image)
            epub = library / "footnotes.epub"
            make_epub(epub, title, body, notes, image)
            if has_progress:
                db.execute("DELETE FROM ReaderProgress WHERE BookFileId = ?", (file_id,))
            now = "2026-11-19T00:00:00+00:00"
            db.execute("INSERT OR REPLACE INTO Books (Id,Title,Authors,CoverPath,CreatedAt,UpdatedAt) VALUES (?,?,?,?,?,?)",
                       (book_id, title, "脚注回归测试", str(cover.relative_to(data)), now, now))
            db.execute("INSERT OR REPLACE INTO BookFiles (Id,BookId,Format,RelativePath,Size,Sha256) VALUES (?,?,?,?,?,?)",
                       (file_id, book_id, "epub", str(epub.relative_to(data)), epub.stat().st_size, hashlib.sha256(epub.read_bytes()).hexdigest()))
    # Preferences belong exclusively to this synthetic fixture bundle.
    preferences = app_root / "mobile-preferences.json"
    previous = json.loads(preferences.read_text()) if preferences.exists() else {}
    previous.update({"UiLanguage": "zh-CN", "ThemeMode": 1, "ReaderTheme": 0,
                     "ReaderThemeFollowsAppearance": False, "ReaderFontScale": 1.35,
                     "ReaderBodyPadding": 24, "ReaderParagraphIndent": False,
                     "ReaderVerticalWriting": False, "ReaderPageAnimation": 0})
    preferences.write_text(json.dumps(previous), encoding="utf-8")
    print(f"Installed isolated {BUNDLE}; four fixtures with real book covers; artifacts: {output}")


if __name__ == "__main__":
    main()
