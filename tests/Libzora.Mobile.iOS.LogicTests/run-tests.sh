#!/bin/bash
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
output="$(mktemp -d "${TMPDIR:-/tmp}/libzora-ios-logic.XXXXXX")"
trap 'rm -rf "$output"' EXIT

xcrun --sdk macosx swiftc -swift-version 5 -parse-as-library \
  "$root/src/Libzora.Mobile.iOS/NativeUI/NativeLibraryModels.swift" \
  "$root/src/Libzora.Mobile.iOS/NativeUI/NativeBookSeries.swift" \
  "$root/src/Libzora.Mobile.iOS/NativeUI/NativeSyncKeyConfirmation.swift" \
  "$root/src/Libzora.Mobile.iOS/NativeUI/NativeAdaptiveLayout.swift" \
  "$root/tests/Libzora.Mobile.iOS.LogicTests/NativeLibraryLogicTests.swift" \
  -o "$output/native-library-tests"
"$output/native-library-tests"
