#!/usr/bin/env python3
"""Seed an isolated reading-statistics visual fixture; never touch the user's preview library."""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import sqlite3
import subprocess
import xml.etree.ElementTree as ET
import zipfile

BUNDLE = "com.kingstacker.libzora.readingstatscheck"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", type=Path, required=True)
    parser.add_argument("--simulator", required=True)
    args = parser.parse_args()
    here = Path(__file__).resolve().parent
    covers = here.parents[1] / "公版书"
    subprocess.run(["python3", "-I", str(here / "prepare-reader-footnote-preview.py"),
                    "--app", str(args.app), "--simulator", args.simulator,
                    "--covers-from", str(covers), "--page-animation", "3", "--bundle-id", BUNDLE], check=True)
    container = Path(subprocess.check_output(["xcrun", "simctl", "get_app_container", args.simulator, BUNDLE, "data"], text=True).strip())
    root = container / "Documents/Libzora"
    titles = []
    for path in sorted(covers.glob("*.epub")):
        with zipfile.ZipFile(path) as epub:
            if not any("cover" in entry.filename.lower() and entry.filename.lower().endswith((".jpg", ".jpeg"))
                       and entry.file_size < 8_000_000 for entry in epub.infolist()):
                continue
            opf = next(name for name in epub.namelist() if name.endswith(".opf"))
            title = ET.fromstring(epub.read(opf)).find(".//{http://purl.org/dc/elements/1.1/}title")
            titles.append(title.text if title is not None and title.text else path.stem)
    now = datetime.now(timezone.utc).isoformat()
    day = now[:10]
    with sqlite3.connect(root / "data/libzora.db") as db:
        db.executescript("""
            CREATE TABLE IF NOT EXISTS ReaderReadingStats (
                BookFileId TEXT PRIMARY KEY,BookId TEXT NOT NULL,CumulativeSeconds INTEGER NOT NULL DEFAULT 0,
                ListeningSeconds INTEGER NOT NULL DEFAULT 0,HasFinished INTEGER NOT NULL DEFAULT 0,
                ProgressPercent REAL NOT NULL DEFAULT 0,CompletedChapters INTEGER NOT NULL DEFAULT 0,
                TotalChapters INTEGER NOT NULL DEFAULT 0,UpdatedAt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS S3SyncReadingDayCounters (
                BookFileId TEXT NOT NULL,ReadingDate TEXT NOT NULL,DeviceId TEXT NOT NULL,
                Seconds INTEGER NOT NULL,ListeningSeconds INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(BookFileId,ReadingDate,DeviceId));
        """)
        rows = db.execute("SELECT b.Id,f.Id FROM Books b JOIN BookFiles f ON f.BookId=b.Id ORDER BY b.CoverPath").fetchall()
        for index, (book, file) in enumerate(rows):
            total = [32400, 25200, 19800, 14400, 10800][index]
            listening = [7200, 9000, 0, 7200, 10800][index]
            progress = [100, 35, 65, 25, 10][index]
            db.execute("UPDATE Books SET Title=?,Authors=?,ReadingStatus=?,UpdatedAt=? WHERE Id=?",
                       (titles[index], "公版阅读样例", 2 if index < 2 else 1, now, book))
            db.execute("INSERT OR REPLACE INTO ReaderReadingStats VALUES (?,?,?,?,?,?,?,?,?)",
                       (file, book, total, listening, int(index < 2), progress, 0, 2, now))
            db.execute("INSERT OR REPLACE INTO S3SyncReadingDayCounters VALUES (?,?,?,?,?)",
                       (file, day, "legacy", total, listening))
    preferences = root / "mobile-preferences.json"
    values = json.loads(preferences.read_text())
    values.update({"ThemeMode": 1, "ColorfulMode": False, "ReaderThemeFollowsAppearance": True})
    preferences.write_text(json.dumps(values), encoding="utf-8")
    print(f"Prepared five covered books and synthetic reading/listening statistics in {BUNDLE}")


if __name__ == "__main__":
    main()
