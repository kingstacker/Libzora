#!/usr/bin/env python3
"""Check iOS UI translation coverage and positional formatting contracts."""
from __future__ import annotations

import argparse
from collections import Counter
from dataclasses import dataclass
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET

HAN = re.compile(r"[㐀-鿿]")
PLACEHOLDER = re.compile(r"(?<!\{)\{([0-9]+)(?:,[+-]?[0-9]+)?(?::[^{}]+)?\}(?!\})")
# These are content/data identifiers, not interface labels. Language options
# deliberately use their own names so they can be found in either language.
VERBATIM = {"京华老宋体", "简体中文"}


@dataclass
class SwiftString:
    start: int
    end: int
    source: str
    arguments: list[str]
    localized: bool


def swift_strings(text: str) -> list[SwiftString]:
    """Read strings without mistaking comments or nested interpolations for UI."""
    result: list[SwiftString] = []

    def comment(index: int) -> int:
        if text.startswith("//", index):
            end = text.find("\n", index)
            return len(text) if end < 0 else end
        if text.startswith("/*", index):
            depth = 1
            index += 2
            while index < len(text) and depth:
                if text.startswith("/*", index):
                    depth += 1
                    index += 2
                elif text.startswith("*/", index):
                    depth -= 1
                    index += 2
                else:
                    index += 1
            return index
        return index

    def expression(index: int) -> tuple[str, int]:
        start, depth = index, 1
        while index < len(text):
            skipped = comment(index)
            if skipped != index:
                index = skipped
                continue
            if text[index] == '"':
                _, index = string(index)
                continue
            if text[index] == "(":
                depth += 1
            elif text[index] == ")":
                depth -= 1
                if depth == 0:
                    return text[start:index], index + 1
            index += 1
        raise ValueError("Unterminated Swift interpolation")

    def string(start: int) -> tuple[SwiftString, int]:
        delimiter = '"""' if text.startswith('"""', start) else '"'
        index = start + len(delimiter)
        parts: list[str] = []
        arguments: list[str] = []
        while index < len(text):
            if text.startswith(delimiter, index):
                end = index + len(delimiter)
                raw = "".join(parts)
                # Keep source spelling for keys; decode Swift's ordinary escapes.
                source = re.sub(r"\\([\\\"nrt])", lambda match: {
                    "\\": "\\", '"': '"', "n": "\n", "r": "\r", "t": "\t"
                }[match[1]], raw)
                localized = re.search(r"\bL\(\s*$", text[max(0, start - 20):start]) is not None
                return SwiftString(start, end, source, arguments, localized), end
            if text.startswith("\\(", index):
                value, index = expression(index + 2)
                parts.append("{" + str(len(arguments)) + "}")
                arguments.append(value)
            elif text[index] == "\\" and index + 1 < len(text):
                parts.append(text[index:index + 2])
                index += 2
            else:
                parts.append(text[index])
                index += 1
        raise ValueError("Unterminated Swift string")

    index = 0
    while index < len(text):
        skipped = comment(index)
        if skipped != index:
            index = skipped
        elif text[index] == '"':
            value, index = string(index)
            result.append(value)
        else:
            index += 1
    return result


def catalog(root: Path) -> dict[str, str]:
    namespace = "{http://schemas.microsoft.com/winfx/2006/xaml}"
    def shared(language: str) -> dict[str, str]:
        path = root / "src/Libzora.App/Resources" / ("Strings." + language + ".axaml")
        return {element.attrib[namespace + "Key"]: element.text or ""
                for element in ET.parse(path).getroot().findall(namespace + "String")}
    chinese, english = shared("zh-CN"), shared("en-US")
    values: dict[str, str] = {}
    for key in sorted(chinese):
        if key in english:
            values.setdefault(chinese[key], english[key])
    for path in sorted((root / "src/Libzora.Mobile/Localization").glob("*.en-US.json")):
        values.update(json.loads(path.read_text(encoding="utf-8")))
    return values


def check(root: Path) -> int:
    values = catalog(root)
    failures: list[str] = []
    keys: set[str] = set()
    count = 0
    for path in sorted((root / "src/Libzora.Mobile.iOS/NativeUI").glob("*.swift")):
        # Markdown's CJK regular expression parses content; it is not UI text.
        if path.name == "NativeMarkdown.swift":
            continue
        text = path.read_text(encoding="utf-8")
        for value in swift_strings(text):
            if (not HAN.search(value.source) or value.source in VERBATIM
                    # Existing reader automation IDs are stable data keys,
                    # even when their historical suffix is Chinese.
                    or value.source.startswith("reader.")):
                continue
            line = text.count("\n", 0, value.start) + 1
            location = f"{path.relative_to(root)}:{line}"
            if not value.localized:
                failures.append(f"{location}: UI string is not localized: {value.source!r}")
                continue
            keys.add(value.source)
            count += 1
            if value.source not in values:
                failures.append(f"{location}: missing English translation: {value.source!r}")
    backend_count = 0
    backend_paths = list((root / "src/Libzora.Mobile.iOS").glob("IOSNativeFrontend*.cs"))
    backend_paths += list((root / "src/Libzora.Mobile/Views").glob("ReaderPage*.cs"))
    for path in sorted(backend_paths):
        text = path.read_text(encoding="utf-8")
        for match in re.finditer(r'\bUiText\.Get\(\s*("(?:[^"\\]|\\.)*")', text):
            source = json.loads(match[1])
            if not HAN.search(source):
                continue
            keys.add(source)
            backend_count += 1
            if source not in values:
                line = text.count("\n", 0, match.start()) + 1
                failures.append(f"{path.relative_to(root)}:{line}: missing backend translation: {source!r}")
    for source in keys:
        if source not in values:
            continue
        translated = values[source]
        if HAN.search(translated) and source not in VERBATIM:
            failures.append(f"English UI translation still contains Chinese: {source!r}: {translated!r}")
        if Counter(PLACEHOLDER.findall(source)) != Counter(PLACEHOLDER.findall(translated)):
            failures.append(f"Formatting arguments differ: {source!r}: {translated!r}")
    for failure in failures:
        print(failure)
    print(f"iOS UI localization: {count} native and {backend_count} backend occurrences, {len(keys)} source keys; {len(failures)} errors")
    return int(bool(failures))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    return check(parser.parse_args().root.resolve())


if __name__ == "__main__":
    raise SystemExit(main())
