#!/usr/bin/env python3
"""Install isolated layout fixtures for the first EPUB compatibility batch."""
import argparse
import hashlib
from pathlib import Path
import plistlib
import shutil
import sqlite3
import subprocess
import tempfile
import uuid
import zipfile

BUNDLE = "com.kingstacker.libzora.readerlayoutpreview"
BOOKS = (
    ("排版回归·列表与文字", """
      <h2>排版特性</h2>
      <p class='spaced'>字距放宽的第一批排版示例。</p>
      <p style='text-transform:uppercase'>uppercase words become capitals.</p>
      <p class='smcp'>Small Caps In A Sentence</p>
      <ol><li>有序列表第一项</li><li>第二项会换行并对齐在文字下方，而不是回到标记起点。</li><li>第三项</li></ol>
      <ul style='list-style-type:square'><li>方形列表第一项</li><li>方形列表第二项</li></ul>
      <p>下划线 <u>重点术语</u>，删除线 <s>旧价格</s>，化学式 H<sub>2</sub>O。</p>
      <p style='text-decoration:underline'>CSS 下划线也生效</p>
      <p style='display:none'>这段隐藏文字不应显示在阅读页。</p>
      <p>正文继续，确认隐藏内容没有留下空隙。</p>
    """, ".spaced { letter-spacing: 0.25em; } .smcp { font-variant: small-caps; }"),
    ("排版回归·真实目录", """
      <nav epub:type='toc'><ol><li><a href='text.xhtml'>第一章</a></li><li><a href='text.xhtml'>第二章</a></li></ol></nav>
      <h2>第一章</h2>
      <p>目录列表不应出现自动编号，正文从这里开始。</p>
    """, ""),
    ("排版回归·隐藏样式", """
      <h2>隐藏样式回归</h2>
      <p class='screen-only' style='display:block'>内联覆盖后，这段正文必须显示。</p>
      <div style='visibility:hidden'>隐藏父级文字
        <p style='visibility:visible'>子段恢复可见，这段正文也必须显示。</p>
        <p>这段仍隐藏，不应显示。</p>
      </div>
      <table border='1'>
        <tr><th>项目</th><th>结果</th></tr>
        <tr style='display:none'><td>隐藏行</td><td>不应显示</td></tr>
        <tbody style='display:none'><tr><td>隐藏分组</td><td>不应显示</td></tr></tbody>
        <tr><td rowspan='2'>跨行</td><td><ruby>排版<rt>páibǎn</rt></ruby></td></tr>
        <tr><td>保留正文</td><td>末列保留</td></tr>
      </table>
      <p>横排注音：<ruby>汉<rt>hàn</rt>字<rt>zì</rt></ruby>，<ruby>阅读<rt>yuèdú</rt></ruby>。</p>
      <p>段内不换行：甲乙丙丁戊己庚辛<span style='white-space:nowrap'>整组文字同行</span>。数字 100&#160;km 也保持同行。</p>
    """, ".screen-only { display:none; }"),
    ("排版回归·列表计数器", """
      <h2>列表编号与缩进</h2>
      <ol start='3'>
        <li style='list-style-type:none' value='5'>这一项隐藏序号，但仍占用编号五。</li>
        <li>下一项必须显示六，而不是一或三。</li>
        <li>这一项显示七，换行后应准确对齐正文，不能对齐序号或漏掉标记后的空格。</li>
      </ol>
      <ul><li>圆点与后面的空格都属于合成标记，换行正文应保持同样的悬挂缩进。</li></ul>
    """, ""),
    ("排版回归·英文断词", """
      <h2>English hyphenation</h2>
      <p style='text-align:left'>The typography of antidisestablishmentarianism and
      incomprehensibilities demands careful measurement when the column is narrow.
      Internationalization and counterrevolutionaries also belong here.</p>
      <p style='text-align:left'>Short words like table, project and present stay whole
      because the publisher's exception list marks them unbreakable.</p>
    """, "p { text-indent:0; margin:0 0 1em; }"),
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
        if len(covers) == len(BOOKS):
            return covers
    raise ValueError("Provide at least two public-domain EPUBs with JPEG covers.")


def make_epub(path, title, body, css, image):
    def page(content):
        return ("<html xmlns='http://www.w3.org/1999/xhtml' xmlns:epub='http://www.idpf.org/2007/ops'>"
                "<head><title>" + title + "</title><style>" + css + "</style>"
                "<style>p { text-indent:0; margin:0 0 .8em; } h2 { margin:1em 0 .6em; }</style></head>"
                "<body>" + content + "</body></html>")
    with zipfile.ZipFile(path, "w") as book:
        book.writestr("mimetype", "application/epub+zip", compress_type=zipfile.ZIP_STORED)
        book.writestr("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' "
                      "version='1.0'><rootfiles><rootfile full-path='book.opf' media-type='application/oebps-package+xml'/>"
                      "</rootfiles></container>")
        book.writestr("book.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'>"
                      "<metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>" + title + "</dc:identifier>"
                      "<dc:title>" + title + "</dc:title><dc:language>zh-CN</dc:language></metadata><manifest>"
                      "<item id='text' href='text.xhtml' media-type='application/xhtml+xml'/>"
                      "<item id='cover' href='cover.jpg' media-type='image/jpeg' properties='cover-image'/>"
                      "<item id='nav' href='nav.xhtml' media-type='application/xhtml+xml' properties='nav'/>"
                      "</manifest><spine><itemref idref='text'/></spine></package>")
        book.writestr("text.xhtml", page(body))
        book.writestr("nav.xhtml", page("<nav epub:type='toc'><ol><li><a href='text.xhtml'>正文</a></li></ol></nav>"))
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
    output = Path(tempfile.mkdtemp(prefix="libzora-reader-layout-"))
    preview = output / "ReaderLayoutPreview.app"
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
    rename(preview, BUNDLE, "排版回归")
    subprocess.run(["xcrun", "simctl", "terminate", args.simulator, BUNDLE], capture_output=True)
    run("xcrun", "simctl", "install", args.simulator, str(preview))
    container = Path(run("xcrun", "simctl", "get_app_container", args.simulator, BUNDLE, "data"))
    app_root = container / "Documents/Libzora"
    data = app_root / "data"
    (data / "covers").mkdir(parents=True, exist_ok=True)
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
        for index, ((title, body, css), image) in enumerate(zip(BOOKS, covers)):
            book_id = str(uuid.uuid5(uuid.NAMESPACE_URL, BUNDLE + "/book/" + title))
            file_id = str(uuid.uuid5(uuid.NAMESPACE_URL, BUNDLE + "/file/" + title))
            library = data / "library" / uuid.UUID(book_id).hex
            library.mkdir(parents=True, exist_ok=True)
            cover = data / "covers" / (str(index) + ".jpg")
            cover.write_bytes(image)
            epub = library / "layout.epub"
            make_epub(epub, title, body, css, image)
            now = "2026-11-19T00:00:00+00:00"
            db.execute("INSERT OR REPLACE INTO Books (Id,Title,Authors,CoverPath,CreatedAt,UpdatedAt) VALUES (?,?,?,?,?,?)",
                       (book_id, title, "排版回归测试", str(cover.relative_to(data)), now, now))
            db.execute("INSERT OR REPLACE INTO BookFiles (Id,BookId,Format,RelativePath,Size,Sha256) VALUES (?,?,?,?,?,?)",
                       (file_id, book_id, "epub", str(epub.relative_to(data)), epub.stat().st_size, hashlib.sha256(epub.read_bytes()).hexdigest()))
    (app_root / "mobile-preferences.json").write_text(
        '{"OnboardingAccepted":true,"UiLanguage":"zh-CN","ThemeMode":1,"ReaderTheme":0,'
        '"ReaderThemeFollowsAppearance":false,"ReaderFontScale":1.35,'
        '"ReaderBodyPadding":24,"ReaderParagraphIndent":false,'
        '"ReaderVerticalWriting":false,"ReaderPageAnimation":0}', encoding="utf-8")
    print(f"Installed isolated {BUNDLE}; layout fixtures with real book covers; artifacts: {output}")


if __name__ == "__main__":
    main()
