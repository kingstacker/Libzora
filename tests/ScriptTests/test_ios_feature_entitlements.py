#!/usr/bin/env python3
"""Evaluate iOS signing properties without building or using a real profile."""
import itertools
from pathlib import Path
import plistlib
import shutil
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "src/Libzora.Mobile.iOS/Libzora.Mobile.iOS.csproj"
CLOUD_KEYS = {
    "com.apple.developer.icloud-container-identifiers",
    "com.apple.developer.ubiquity-container-identifiers",
    "com.apple.developer.icloud-services",
}
GROUP_KEY = "com.apple.security.application-groups"


@unittest.skipUnless(shutil.which("dotnet"), ".NET SDK is required for project evaluation")
class IOSFeatureEntitlementTests(unittest.TestCase):
    def evaluate(self, widgets, cloud, *extra):
        result = subprocess.run(
            [
                "dotnet", "msbuild", str(PROJECT), "-nologo", "-nodeReuse:false",
                "-p:RuntimeIdentifier=ios-arm64",
                f"-p:EnableWidgets={str(widgets).lower()}",
                f"-p:EnableICloud={str(cloud).lower()}",
                "-getProperty:CodesignEntitlements", *extra,
            ],
            cwd=ROOT, text=True, capture_output=True, timeout=60, check=True,
        )
        return result.stdout.strip()

    def test_default_entitlements_only_request_enabled_features(self):
        with (PROJECT.parent / "Entitlements.plist").open("rb") as stream:
            full = plistlib.load(stream)
        for widgets, cloud in itertools.product((False, True), repeat=2):
            with self.subTest(widgets=widgets, cloud=cloud):
                path = self.evaluate(widgets, cloud)
                if not widgets and not cloud:
                    self.assertEqual("", path)
                    continue
                with Path(path).open("rb") as stream:
                    entitlements = plistlib.load(stream)
                expected = {}
                if widgets:
                    expected[GROUP_KEY] = full[GROUP_KEY]
                if cloud:
                    expected.update({key: full[key] for key in CLOUD_KEYS})
                self.assertEqual(expected, entitlements)

    def test_explicit_entitlements_are_never_overridden(self):
        custom = str(ROOT / "custom-signing.entitlements")
        for widgets, cloud in itertools.product((False, True), repeat=2):
            with self.subTest(widgets=widgets, cloud=cloud):
                self.assertEqual(custom, self.evaluate(widgets, cloud, f"-p:CodesignEntitlements={custom}"))


if __name__ == "__main__":
    unittest.main()
