#!/bin/bash
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
output="$(mktemp -d "${TMPDIR:-/tmp}/libzora-ios-logic.XXXXXX")"
trap 'rm -rf "$output"' EXIT

xcrun --sdk macosx swiftc -swift-version 5 -parse-as-library \
  "$root/src/Libzora.Mobile.iOS/NativeUI/NativeLibraryModels.swift" \
  "$root/src/Libzora.Mobile.iOS/NativeUI/NativeBookSeries.swift" \
  "$root/src/Libzora.Mobile.iOS/NativeUI/NativeLibraryBatchPolicy.swift" \
  "$root/src/Libzora.Mobile.iOS/NativeUI/NativeLibraryScrollChromePolicy.swift" \
  "$root/src/Libzora.Mobile.iOS/NativeUI/NativeSyncKeyConfirmation.swift" \
  "$root/src/Libzora.Mobile.iOS/NativeUI/NativeAdaptiveLayout.swift" \
  "$root/tests/Libzora.Mobile.iOS.LogicTests/NativeLibraryLogicTests.swift" \
  -o "$output/native-library-tests"
"$output/native-library-tests"

xcrun --sdk macosx swiftc -swift-version 5 -parse-as-library \
  "$root/src/Libzora.Mobile.iOS/NativeUI/NativePreferenceEdits.swift" \
  "$root/src/Libzora.Mobile.iOS/NativeUI/NativeReaderSearchCursor.swift" \
  "$root/src/Libzora.Mobile.iOS/NativeUI/NativeBookOpenTransitionPolicy.swift" \
  "$root/src/Libzora.Mobile.iOS/NativeUI/NativeSwipeDeletePolicy.swift" \
  "$root/src/Libzora.Mobile.iOS/NativeUI/NativeBookReflectionPolicy.swift" \
  "$root/src/Libzora.Mobile.iOS/NativeUI/NativeSelectionToolbarLayout.swift" \
  "$root/tests/Libzora.Mobile.iOS.LogicTests/NativeStateLogicTests.swift" \
  -o "$output/native-state-tests"
"$output/native-state-tests"

xcrun --sdk macosx swiftc -swift-version 5 -parse-as-library \
  "$root/src/Libzora.Mobile.iOS/NativeUI/NativeReaderOpenPolicy.swift" \
  "$root/tests/Libzora.Mobile.iOS.LogicTests/NativeReaderOpenLogicTests.swift" \
  -o "$output/native-reader-open-tests"
"$output/native-reader-open-tests"

xcrun --sdk macosx swiftc -swift-version 5 -parse-as-library \
  "$root/src/Libzora.Mobile.iOS/NativeUI/NativeReaderPresentationPolicy.swift" \
  "$root/src/Libzora.Mobile.iOS/NativeUI/NativeWidgetNavigationPolicy.swift" \
  "$root/src/Libzora.Mobile.iOS/NativeUI/NativeBookOpenTransitionPolicy.swift" \
  "$root/tests/Libzora.Mobile.iOS.LogicTests/NativeReaderPresentationLogicTests.swift" \
  -o "$output/native-reader-presentation-tests"
"$output/native-reader-presentation-tests"

xcrun --sdk macosx swiftc -swift-version 5 -parse-as-library \
  "$root/src/Libzora.Widgets/Shared/WidgetModels.swift" \
  "$root/src/Libzora.Widgets/Shared/WidgetContinueBooksLayout.swift" \
  "$root/src/Libzora.Widgets/Shared/WidgetReadingTreeModel.swift" \
  "$root/tests/Libzora.Mobile.iOS.LogicTests/WidgetReadingTreeLogicTests.swift" \
  -o "$output/widget-tree-tests"
"$output/widget-tree-tests"

xcrun --sdk macosx swiftc -swift-version 5 -parse-as-library \
  "$root/src/Libzora.Mobile.iOS/NativeUI/NativeMarkdownParser.swift" \
  "$root/tests/Libzora.Mobile.iOS.LogicTests/NativeMarkdownLogicTests.swift" \
  -o "$output/native-markdown-tests"
"$output/native-markdown-tests"

xcrun --sdk macosx swiftc -swift-version 5 -parse-as-library \
  "$root/src/Libzora.Mobile.iOS/NativeUI/NativeDataSafetyPolicy.swift" \
  "$root/tests/Libzora.Mobile.iOS.LogicTests/NativeDataSafetyLogicTests.swift" \
  -o "$output/native-data-safety-tests"
"$output/native-data-safety-tests"
