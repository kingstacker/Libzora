#!/bin/zsh
set -euo pipefail

# Build the macOS WidgetKit extension. The release packager copies the .appex
# into Contents/PlugIns and signs it with the widget entitlements. Multiple
# architectures are lipo-merged into one executable.
arches="$1"
native_output="$2"
short_version="${3:-1.1.3}"
build_version="${4:-1}"
source_dir="${0:A:h}/Shared"
font_dir="${0:A:h}/../Libzora.App/Assets/Fonts"
appex="$native_output/LibzoraWidgets.appex"
work="$native_output/.arch"
native_sdk=macosx

rm -rf "$appex" "$work"
mkdir -p "$appex" "$work"
native_sdk_path="$(xcrun --sdk "$native_sdk" --show-sdk-path)"

binaries=()
bridge_binaries=()
for arch in ${=arches}; do
  case "$arch" in
    arm64) target=arm64-apple-macos14.0 ;;
    x86_64) target=x86_64-apple-macos14.0 ;;
    *) print -u2 "Unsupported macOS widget architecture: $arch"; exit 1 ;;
  esac
  binary="$work/LibzoraWidgets-$arch"
  # App extensions enter through Foundation's NSExtensionMain. Without this the
  # Swift-generated main from @main returns on macOS 26, so chronod never
  # receives descriptors (it reports "connection to service ... invalidated").
  xcrun --sdk "$native_sdk" swiftc \
    -sdk "$native_sdk_path" -target "$target" -swift-version 5 \
    -parse-as-library -emit-executable -module-name LibzoraWidgets \
    -module-cache-path "$native_output/module-cache" \
    -O \
    -framework WidgetKit -framework SwiftUI -framework AppKit -framework Foundation \
    -Xlinker -e -Xlinker _NSExtensionMain \
    "$source_dir"/*.swift \
    -o "$binary"
  rm -rf "$binary.dSYM"
  binaries+=("$binary")

  # The containing app reloads timelines through this small dylib; WidgetKit
  # offers no cross-process reload API.
  bridge="$work/LibzoraWidgetBridge-$arch"
  xcrun --sdk "$native_sdk" swiftc \
    -sdk "$native_sdk_path" -target "$target" -swift-version 5 \
    -parse-as-library -emit-library -module-name LibzoraWidgetBridge \
    -module-cache-path "$native_output/module-cache" \
    -Xlinker -install_name -Xlinker "@loader_path/libLibzoraWidgetBridge.dylib" \
    -O \
    -framework WidgetKit -framework SwiftUI -framework AppKit -framework Foundation \
    "${0:A:h}/MacWidgetBridge.swift" \
    -o "$bridge"
  rm -rf "$bridge.dSYM"
  bridge_binaries+=("$bridge")
done

if (( ${#binaries[@]} == 1 )); then
  cp "${binaries[1]}" "$appex/LibzoraWidgets"
  cp "${bridge_binaries[1]}" "$native_output/libLibzoraWidgetBridge.dylib"
else
  lipo -create -output "$appex/LibzoraWidgets" "${binaries[@]}"
  lipo -create -output "$native_output/libLibzoraWidgetBridge.dylib" "${bridge_binaries[@]}"
fi
rm -rf "$work"
chmod 0755 "$appex/LibzoraWidgets"
chmod 0755 "$native_output/libLibzoraWidgetBridge.dylib"
mkdir -p "$appex/Fonts"
cp "$font_dir/KingHwaOldSong-v3.0.ttf" "$font_dir/KingHwaOldSong-LICENSE.txt" "$appex/Fonts/"

cat > "$appex/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key><string>zh_CN</string>
  <key>CFBundleDisplayName</key><string>Libzora</string>
  <key>CFBundleExecutable</key><string>LibzoraWidgets</string>
  <key>CFBundleIdentifier</key><string>io.github.kingstacker.libzora.widgets</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>LibzoraWidgets</string>
  <key>CFBundlePackageType</key><string>XPC!</string>
  <key>CFBundleShortVersionString</key><string>$short_version</string>
  <key>CFBundleVersion</key><string>$build_version</string>
  <key>CFBundleSupportedPlatforms</key><array><string>MacOSX</string></array>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>NSExtension</key>
  <dict>
    <key>NSExtensionPointIdentifier</key><string>com.apple.widgetkit-extension</string>
  </dict>
</dict>
</plist>
EOF
plutil -lint "$appex/Info.plist" >/dev/null
