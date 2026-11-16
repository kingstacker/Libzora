#!/usr/bin/env python3
"""Offline regressions for the license inventory and byte-preserving checkout."""

import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("license_inventory", ROOT / "scripts/update-third-party-notices.py")
tool = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tool)


class LicenseInventoryTests(unittest.TestCase):
    def test_legacy_license_url_preserves_publisher_terms(self):
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory)
            (package / "sdk.nuspec").write_text(
                '<package><metadata><id>Microsoft.Windows.SDK.NET.Ref</id>'
                '<version>10.0.19041.57</version><authors>Microsoft</authors>'
                '<licenseUrl>https://aka.ms/WinSDKLicenseURL</licenseUrl>'
                '</metadata></package>')
            metadata = tool.read_package(package)
            self.assertEqual(metadata["licenseType"], "url")
            self.assertEqual(metadata["license"], "https://aka.ms/WinSDKLicenseURL")
            original = b"{\\rtf1 Original SDK license terms}"

            class CachedDocuments:
                def fetch(self, url):
                    self.url = url
                    return original

            documents = CachedDocuments()
            data, name, url = tool.resolve_upstream_document(metadata, documents)
            self.assertEqual((data, name, url), (original, "LICENSE.rtf", metadata["license"]))
            self.assertEqual(documents.url, url)
            metadata["license"] = "http://example.invalid/license"
            with self.assertRaisesRegex(RuntimeError, "HTTPS"):
                tool.resolve_upstream_document(metadata, documents)

    def test_implicit_windows_packs_are_discovered_without_assets(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            cache = root / "cache"
            for package_id, version in (("Microsoft.WindowsDesktop.App.Runtime.win-x64", "10.0.11"),
                                        ("Microsoft.Windows.SDK.NET.Ref", "10.0.19041.57")):
                package = cache / package_id.lower() / version
                package.mkdir(parents=True)
                (package / "package.nuspec").write_text(
                    f'<package><metadata><id>{package_id}</id><version>{version}</version>'
                    '<license type="expression">MIT</license></metadata></package>')
            previous = tool.ROOT
            try:
                tool.ROOT = root
                found = tool.restored_packages([cache])
            finally:
                tool.ROOT = previous
            self.assertEqual({p["id"] for p in found}, {
                "Microsoft.WindowsDesktop.App.Runtime.win-x64", "Microsoft.Windows.SDK.NET.Ref"})

    def test_refresh_preserves_separately_maintained_attributions(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            target = root / "THIRD-PARTY-NOTICES.md"
            marker = "## NuGet package licenses and copyright notices"
            prefix = "# Notices\n\n## Downloadable fonts\nOriginal font notice\n\n## Models\nOriginal model notice\n\n"
            target.write_text(prefix + marker + "\n\nOld inventory\n")
            previous = tool.ROOT
            try:
                tool.ROOT = root
                tool.write_package_notices(["# Generated header", marker, "", "New inventory", ""])
            finally:
                tool.ROOT = previous
            self.assertEqual(target.read_text(), prefix + marker + "\n\nNew inventory\n")

    def test_windows_checkout_preserves_lf_and_crlf_documents(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            subprocess.run(["git", "-C", str(root), "init", "--quiet"], check=True, capture_output=True)
            (root / ".gitattributes").write_bytes((ROOT / ".gitattributes").read_bytes())
            files = {}
            for name, content in (("LF.txt", b"License\nOriginal notice\n"),
                                  ("CRLF.txt", b"License\r\nOriginal notice\r\n")):
                relative = "src/Libzora.Infrastructure/ThirdParty/Notices/licenses/" + name
                path = root / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(content)
                files[relative] = content
            subprocess.run(["git", "-C", str(root), "-c", "core.autocrlf=true", "add", ".gitattributes", *files],
                           check=True, capture_output=True)
            for relative, original in files.items():
                checkout = subprocess.check_output([
                    "git", "-C", str(root), "-c", "core.autocrlf=true", "cat-file", "--filters", ":" + relative])
                self.assertEqual(checkout, original)


if __name__ == "__main__":
    unittest.main()
