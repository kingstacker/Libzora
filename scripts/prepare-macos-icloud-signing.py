#!/usr/bin/env python3
"""Prepare runtime/app entitlements from a decoded Apple provisioning profile."""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
from pathlib import Path
import plistlib

CONTAINER = "iCloud.com.kingstacker.libzora"
BUNDLE_ID = "io.github.kingstacker.libzora"
CLOUD_KEYS = (
    "com.apple.developer.icloud-container-identifiers",
    "com.apple.developer.ubiquity-container-identifiers",
    "com.apple.developer.icloud-services",
)


def prepare(base: dict, profile: dict | None) -> tuple[dict, dict]:
    runtime = {key: value for key, value in base.items()
               if not key.startswith(("com.apple.developer.icloud-", "com.apple.developer.ubiquity-"))}
    if profile is None:
        # Restricted iCloud entitlements without an Apple profile can prevent
        # an ad-hoc bundle from launching. Diagnostic builds remain runnable.
        return runtime, dict(runtime)

    expiry = profile.get("ExpirationDate")
    if not isinstance(expiry, datetime) or expiry.replace(tzinfo=timezone.utc) <= datetime.now(timezone.utc):
        raise ValueError("The iCloud provisioning profile is missing an expiration date or has expired.")
    allowed = profile.get("Entitlements")
    if not isinstance(allowed, dict):
        raise ValueError("The iCloud provisioning profile has no entitlements.")
    if allowed.get("get-task-allow") or allowed.get("com.apple.security.get-task-allow"):
        raise ValueError("Use a Developer ID distribution profile, not a development profile.")
    app_id = allowed.get("com.apple.application-identifier") or allowed.get("application-identifier")
    if not isinstance(app_id, str) or not app_id.endswith("." + BUNDLE_ID):
        raise ValueError(f"The iCloud profile must authorize the macOS App ID {BUNDLE_ID}.")
    teams = profile.get("TeamIdentifier")
    team_id = allowed.get("com.apple.developer.team-identifier")
    if not team_id and isinstance(teams, list) and len(teams) == 1:
        team_id = teams[0]
    if not isinstance(team_id, str) or not team_id or not isinstance(teams, list) or team_id not in teams:
        raise ValueError("The iCloud profile is missing a valid Apple team identifier.")
    for key in CLOUD_KEYS[:2]:
        if not isinstance(allowed.get(key), list) or CONTAINER not in allowed[key]:
            raise ValueError(f"The iCloud profile must authorize {CONTAINER} in {key}.")
    if not isinstance(allowed.get(CLOUD_KEYS[2]), list) or "CloudDocuments" not in allowed[CLOUD_KEYS[2]]:
        raise ValueError("The iCloud profile must authorize the CloudDocuments service.")

    app = dict(base)
    app["com.apple.application-identifier"] = app_id
    app["com.apple.developer.team-identifier"] = team_id
    # Production/Development is chosen by Apple's profile, not guessed by the
    # client. This is deliberately absent from profile-less diagnostic builds.
    environment = allowed.get("com.apple.developer.icloud-container-environment")
    if environment is not None:
        if environment != "Production":
            raise ValueError("The Developer ID iCloud profile must use the Production environment.")
        app["com.apple.developer.icloud-container-environment"] = environment
    return runtime, app


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", type=Path, required=True)
    parser.add_argument("--profile", type=Path, help="Decoded plist from security cms -D")
    parser.add_argument("--runtime-output", type=Path, required=True)
    parser.add_argument("--app-output", type=Path, required=True)
    args = parser.parse_args()
    try:
        with args.base.open("rb") as stream:
            base = plistlib.load(stream)
        profile = None
        if args.profile is not None:
            with args.profile.open("rb") as stream:
                profile = plistlib.load(stream)
        runtime, app = prepare(base, profile)
        for path, value in ((args.runtime_output, runtime), (args.app_output, app)):
            with path.open("wb") as stream:
                plistlib.dump(value, stream, sort_keys=False)
    except (OSError, ValueError, TypeError, plistlib.InvalidFileException) as error:
        parser.exit(2, f"iCloud signing configuration: {error}\n")


if __name__ == "__main__":
    main()
