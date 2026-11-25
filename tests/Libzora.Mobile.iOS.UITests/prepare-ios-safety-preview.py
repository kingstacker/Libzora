#!/usr/bin/env python3
"""Prepare only the independent iossafetycheck bundle with covered books, PDF and TXT fixtures."""
import argparse
import hashlib
import json
from pathlib import Path
import plistlib
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import uuid

BUNDLE = "com.kingstacker.libzora.iossafetycheck"


def minimal_pdf():
    stream = b"BT /F1 18 Tf 60 700 Td (Native multi-format safety fixture) Tj ET"
    objects = [b"<< /Type /Catalog /Pages 2 0 R >>",
               b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
               b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 420 760] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
               b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
               b"<< /Length " + str(len(stream)).encode() + b" >>\nstream\n" + stream + b"\nendstream"]
    output = bytearray(b"%PDF-1.4\n")
    offsets = [0]
    for i, data in enumerate(objects, 1):
        offsets.append(len(output))
        output.extend(str(i).encode() + b" 0 obj\n" + data + b"\nendobj\n")
    xref = len(output)
    output.extend(b"xref\n0 6\n0000000000 65535 f \n")
    for offset in offsets[1:]:
        output.extend(f"{offset:010d} 00000 n \n".encode())
    output.extend(f"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode())
    return bytes(output)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", type=Path, required=True)
    parser.add_argument("--simulator", required=True)
    args = parser.parse_args()
    temporary = Path(tempfile.mkdtemp(prefix="libzora-safety-source-"))
    app = temporary / "Safety.app"
    shutil.copytree(args.app.resolve(strict=True), app)
    metadata = plistlib.loads((app / "Info.plist").read_bytes())
    metadata["UIFileSharingEnabled"] = True
    metadata["LSSupportsOpeningDocumentsInPlace"] = True
    (app / "Info.plist").write_bytes(plistlib.dumps(metadata))
    helper = Path(__file__).with_name("prepare-reader-footnote-preview.py")
    subprocess.run([sys.executable, "-I", str(helper), "--app", str(app), "--simulator", args.simulator,
                    "--bundle-id", BUNDLE], check=True)
    container = Path(subprocess.check_output(["xcrun", "simctl", "get_app_container", args.simulator, BUNDLE, "data"], text=True).strip())
    root = container / "Documents/Libzora"
    book_id = str(uuid.uuid5(uuid.NAMESPACE_URL, BUNDLE + "/book/旧式锚点"))
    file_id = str(uuid.uuid5(uuid.NAMESPACE_URL, BUNDLE + "/file/pdf"))
    pdf = root / "data/library" / uuid.UUID(book_id).hex / "safety.pdf"
    pdf.write_bytes(minimal_pdf())
    with sqlite3.connect(root / "data/libzora.db") as db:
        db.execute("INSERT OR REPLACE INTO BookFiles (Id,BookId,Format,RelativePath,Size,Sha256) VALUES (?,?,?,?,?,?)",
                   (file_id, book_id, "pdf", str(pdf.relative_to(root / "data")), pdf.stat().st_size,
                    hashlib.sha256(pdf.read_bytes()).hexdigest()))
    preferences = root / "mobile-preferences.json"
    values = json.loads(preferences.read_text())
    values.update({"OnboardingAccepted": False, "PreviewTxtImport": False, "ManualBookReadingStatuses": {}})
    preferences.write_text(json.dumps(values), encoding="utf-8")
    (container / "Documents/TXT规则样例.txt").write_text("第一章 开始\n这是第一章的正文。\n第二章 后续\n这是第二章的正文。\n", encoding="utf-8")
    (container / "Documents/TXT确认样例.txt").write_text("第一章 远山\n此处是导入验证的正文。\n第二章 回程\n这是最后一段。\n", encoding="utf-8")
    print("Prepared five covered books, a second readable PDF format, and two local TXT fixtures.")


if __name__ == "__main__":
    main()
