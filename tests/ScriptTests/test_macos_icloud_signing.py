#!/usr/bin/env python3
"""Local-only regression tests for Apple entitlement/profile preparation."""
import copy
from datetime import datetime, timedelta, timezone
import importlib.util
from pathlib import Path
import plistlib
import unittest

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("icloud_signing", ROOT / "scripts/prepare-macos-icloud-signing.py")
SIGNING = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SIGNING)


class MacOSICloudSigningTests(unittest.TestCase):
    def setUp(self):
        with (ROOT / "scripts/macos/Libzora.entitlements").open("rb") as stream:
            self.base = plistlib.load(stream)
        self.profile = {
            "ExpirationDate": datetime.now(timezone.utc) + timedelta(days=30),
            "TeamIdentifier": ["ABCDEFGHIJ"],
            "Entitlements": {
                "com.apple.application-identifier": "ABCDEFGHIJ." + SIGNING.BUNDLE_ID,
                "com.apple.developer.team-identifier": "ABCDEFGHIJ",
                "com.apple.developer.icloud-container-identifiers": [SIGNING.CONTAINER],
                "com.apple.developer.ubiquity-container-identifiers": [SIGNING.CONTAINER],
                "com.apple.developer.icloud-services": ["CloudDocuments"],
                "com.apple.developer.icloud-container-environment": "Production",
            },
        }

    def test_adhoc_and_nested_executables_keep_runtime_but_no_restricted_cloud_entitlements(self):
        runtime, app = SIGNING.prepare(self.base, None)
        self.assertEqual(runtime, app)
        self.assertTrue(app["com.apple.security.cs.allow-jit"])
        self.assertTrue(app["com.apple.security.cs.allow-unsigned-executable-memory"])
        self.assertFalse(any(key.startswith("com.apple.developer.icloud-") for key in app))
        self.assertFalse(any(key.startswith("com.apple.developer.ubiquity-") for key in app))

    def test_distribution_profile_adds_exact_app_team_and_cloud_authorization(self):
        original = copy.deepcopy(self.base)
        runtime, app = SIGNING.prepare(self.base, self.profile)
        self.assertEqual(original, self.base)
        self.assertNotIn("com.apple.application-identifier", runtime)
        self.assertEqual("ABCDEFGHIJ." + SIGNING.BUNDLE_ID, app["com.apple.application-identifier"])
        self.assertEqual("Production", app["com.apple.developer.icloud-container-environment"])
        self.assertEqual([SIGNING.CONTAINER], app["com.apple.developer.ubiquity-container-identifiers"])

    def test_wrong_app_expiry_container_service_and_environment_are_rejected(self):
        invalid = []
        profile = copy.deepcopy(self.profile); profile["ExpirationDate"] = datetime.now(timezone.utc) - timedelta(days=1); invalid.append(profile)
        for key, value in (
            ("com.apple.application-identifier", "ABCDEFGHIJ.other.app"),
            ("com.apple.developer.icloud-container-identifiers", ["iCloud.other.app"]),
            ("com.apple.developer.ubiquity-container-identifiers", []),
            ("com.apple.developer.icloud-services", ["CloudKit"]),
            ("com.apple.developer.icloud-container-environment", "Development"),
            ("get-task-allow", True),
            ("com.apple.security.get-task-allow", True),
            ("com.apple.developer.team-identifier", "OTHERTEAM"),
        ):
            profile = copy.deepcopy(self.profile); profile["Entitlements"][key] = value; invalid.append(profile)
        for profile in invalid:
            with self.subTest(profile=profile):
                with self.assertRaises(ValueError): SIGNING.prepare(self.base, profile)


if __name__ == "__main__":
    unittest.main()
