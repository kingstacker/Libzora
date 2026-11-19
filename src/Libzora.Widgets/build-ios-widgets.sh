#!/bin/zsh
set -euo pipefail

# Build a self-contained WidgetKit app extension. The .NET iOS app embeds and
# signs it through AdditionalAppExtensions; this script produces the bundle
# and, for provisioned devices, the complete signing entitlements.
native_rid="$1"
native_output="$2"
native_configuration="$3"
bundle_id="${4:-com.kingstacker.libzora.widgets}"
short_version="${5:-1.1.3}"
build_version="${6:-1}"
provisioning_profile="${7:-}"
source_dir="${0:A:h}/Shared"
font_dir="${0:A:h}/../Libzora.App/Assets/Fonts"
appex="$native_output/LibzoraWidgets.appex"
# Simulator builds read entitlements from the Mach-O __TEXT,__entitlements
# section, not from the ad-hoc signature. Embed the same App Group file that
# AdditionalAppExtensions passes to codesign.
entitlements_path="${0:A:h}/LibzoraWidgets.iOS.entitlements"

case "$native_rid" in
  ios-arm64) native_sdk=iphoneos; native_target=arm64-apple-ios17.0 ;;
  iossimulator-arm64) native_sdk=iphonesimulator; native_target=arm64-apple-ios17.0-simulator ;;
  iossimulator-x64) native_sdk=iphonesimulator; native_target=x86_64-apple-ios17.0-simulator ;;
  *) print -u2 "Unsupported iOS widget runtime: $native_rid"; exit 1 ;;
esac

native_flags=(-O)
if [[ "$native_configuration" == Debug ]]; then
  native_flags=(-Onone -g)
fi
# Simulator code signing is ad-hoc and the entitlement lookup reads the
# Mach-O section; device signatures carry the entitlements instead.
entitlement_flags=()
if [[ "$native_rid" != ios-arm64 ]]; then
  entitlement_flags=(-Xlinker -sectcreate -Xlinker __TEXT -Xlinker __entitlements -Xlinker "$entitlements_path")
fi

rm -rf "$appex"
mkdir -p "$appex"
if [[ "$native_rid" == ios-arm64 && -n "$provisioning_profile" ]]; then
  # AdditionalAppExtensions signs the supplied file directly. Unlike the app
  # head, it does not inject application/team identifiers from the profile.
  python3 -I - "$provisioning_profile" "$entitlements_path" "$bundle_id" "$native_output/LibzoraWidgets.device.entitlements" <<'PY'
import fnmatch
import plistlib
import subprocess
import sys
from pathlib import Path

profile_path, source_path, bundle_id, output_path = sys.argv[1:]
profile = plistlib.loads(subprocess.check_output(
    ["security", "cms", "-D", "-i", profile_path], stderr=subprocess.DEVNULL))
allowed = profile["Entitlements"]
entitlements = plistlib.loads(Path(source_path).read_bytes())
prefix = profile["ApplicationIdentifierPrefix"][0]
application_id = prefix + "." + bundle_id
if not fnmatch.fnmatchcase(application_id, allowed["application-identifier"]):
    raise SystemExit("Widget provisioning profile does not match its bundle identifier.")
for group in entitlements.get("com.apple.security.application-groups", []):
    if not any(fnmatch.fnmatchcase(group, pattern)
               for pattern in allowed.get("com.apple.security.application-groups", [])):
        raise SystemExit("Widget provisioning profile does not authorize its App Group.")
entitlements["application-identifier"] = application_id
entitlements["com.apple.developer.team-identifier"] = allowed["com.apple.developer.team-identifier"]
entitlements["get-task-allow"] = allowed.get("get-task-allow", False)
Path(output_path).write_bytes(plistlib.dumps(entitlements))
PY
fi
native_sdk_path="$(xcrun --sdk "$native_sdk" --show-sdk-path)"

# App extensions enter through Foundation's NSExtensionMain. Without this the
# Swift-generated main from @main runs and returns on iOS 26, so chronod never
# receives descriptors (it reports "connection to service ... invalidated").
xcrun --sdk "$native_sdk" swiftc \
  -sdk "$native_sdk_path" -target "$native_target" -swift-version 5 \
  -parse-as-library -emit-executable -module-name LibzoraWidgets \
  -module-cache-path "$native_output/module-cache" \
  "${native_flags[@]}" \
  -framework WidgetKit -framework SwiftUI -framework UIKit -framework Foundation \
  -Xlinker -e -Xlinker _NSExtensionMain \
  "${entitlement_flags[@]}" \
  "$source_dir"/*.swift \
  -o "$appex/LibzoraWidgets"

rm -rf "$appex/LibzoraWidgets.dSYM"
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
  <key>CFBundleIdentifier</key><string>$bundle_id</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>LibzoraWidgets</string>
  <key>CFBundlePackageType</key><string>XPC!</string>
  <key>CFBundleShortVersionString</key><string>$short_version</string>
  <key>CFBundleVersion</key><string>$build_version</string>
  <key>MinimumOSVersion</key><string>17.0</string>
  <key>UIDeviceFamily</key><array><integer>1</integer><integer>2</integer></array>
  <key>UIAppFonts</key><array><string>Fonts/KingHwaOldSong-v3.0.ttf</string></array>
  <key>NSExtension</key>
  <dict>
    <key>NSExtensionPointIdentifier</key><string>com.apple.widgetkit-extension</string>
  </dict>
</dict>
</plist>
EOF
plutil -lint "$appex/Info.plist" >/dev/null
