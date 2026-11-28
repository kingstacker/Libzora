#!/usr/bin/env python3
"""Prepare three covered, short multi-chapter books in an independent scroll fixture."""
import importlib.util
import json
from pathlib import Path
import subprocess
import zipfile

BUNDLE = "com.kingstacker.libzora.readerscrollcheck"
base = Path(__file__).with_name("prepare-reader-footnote-preview.py")
spec = importlib.util.spec_from_file_location("scroll_fixture_base", base)
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)
fixture.BUNDLE = BUNDLE
fixture.FIXTURES = tuple((label, "", "") for label in ("手指位移", "双向跨章", "快滑惯性"))


def make_epub(path, title, body, notes, image):
    def page(content):
        return ("<html xmlns='http://www.w3.org/1999/xhtml' xmlns:epub='http://www.idpf.org/2007/ops'>"
                "<head><title>" + title + "</title><style>p { text-indent:0; margin:0 0 .5em; }</style>"
                "</head><body>" + content + "</body></html>")
    with zipfile.ZipFile(path, "w") as book:
        book.writestr("mimetype", "application/epub+zip", compress_type=zipfile.ZIP_STORED)
        book.writestr("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'>"
                      "<rootfiles><rootfile full-path='book.opf' media-type='application/oebps-package+xml'/></rootfiles></container>")
        book.writestr("book.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'>"
                      "<metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>" + title + "</dc:identifier>"
                      "<dc:title>" + title + "</dc:title><dc:language>zh-CN</dc:language></metadata><manifest>"
                      "<item id='c1' href='first.xhtml' media-type='application/xhtml+xml'/>"
                      "<item id='c2' href='second.xhtml' media-type='application/xhtml+xml'/>"
                      "<item id='cover' href='cover.jpg' media-type='image/jpeg' properties='cover-image'/>"
                      "<item id='nav' href='nav.xhtml' media-type='application/xhtml+xml' properties='nav'/></manifest>"
                      "<spine><itemref idref='c1'/><itemref idref='c2'/></spine></package>")
        for name, heading in (("first.xhtml", "第一章"), ("second.xhtml", "第二章")):
            paragraphs = "".join(f"<p>{heading}第{i}段。手指移动多少，正文就跟随多少；短拖、长拖和快滑应当保持不同的速度与距离。</p>"
                                 for i in range(1, 10))
            book.writestr(name, page("<h1>" + heading + "</h1>" + paragraphs))
        book.writestr("nav.xhtml", page("<nav epub:type='toc'><ol><li><a href='first.xhtml'>第一章</a></li>"
                                        "<li><a href='second.xhtml'>第二章</a></li></ol></nav>"))
        book.writestr("cover.jpg", image)


fixture.make_epub = make_epub
if __name__ == "__main__":
    import sys
    fixture.main()
    simulator = sys.argv[sys.argv.index("--simulator") + 1]
    container = Path(subprocess.check_output(["xcrun", "simctl", "get_app_container", simulator, BUNDLE, "data"], text=True).strip())
    preferences = container / "Documents/Libzora/mobile-preferences.json"
    values = json.loads(preferences.read_text())
    values.update({"ReaderFlowMode": 0, "ReaderFontScale": 1.0, "ReaderPageAnimation": 0})
    preferences.write_text(json.dumps(values), encoding="utf-8")
