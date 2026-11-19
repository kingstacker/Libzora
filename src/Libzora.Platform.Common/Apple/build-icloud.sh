#!/bin/zsh
set -euo pipefail

# Shared Foundation bridge; no Swift runtime or separate storage implementation.
native_rid="$1"
native_output="$2"
native_configuration="$3"
native_source_dir="${0:A:h}"

case "$native_rid" in
  osx-arm64) native_sdk=macosx; native_target=arm64-apple-macos11.0; native_kind=dynamic ;;
  osx-x64) native_sdk=macosx; native_target=x86_64-apple-macos11.0; native_kind=dynamic ;;
  ios-arm64) native_sdk=iphoneos; native_target=arm64-apple-ios15.0; native_kind=static ;;
  iossimulator-arm64) native_sdk=iphonesimulator; native_target=arm64-apple-ios15.0-simulator; native_kind=static ;;
  iossimulator-x64) native_sdk=iphonesimulator; native_target=x86_64-apple-ios15.0-simulator; native_kind=static ;;
  *) print -u2 "Unsupported iCloud helper runtime: $native_rid"; exit 1 ;;
esac

mkdir -p "$native_output"
native_sdk_path="$(xcrun --sdk "$native_sdk" --show-sdk-path)"
native_flags=(-O2)
if [[ "$native_configuration" == Debug ]]; then
  native_flags=(-O0 -g)
fi

xcrun --sdk "$native_sdk" clang \
  -isysroot "$native_sdk_path" -target "$native_target" \
  -fobjc-arc -fblocks -fvisibility=hidden -Wall -Wextra \
  "${native_flags[@]}" -c "$native_source_dir/LibzoraICloud.m" \
  -o "$native_output/LibzoraICloud.o"

if [[ "$native_kind" == dynamic ]]; then
  xcrun --sdk "$native_sdk" clang \
    -isysroot "$native_sdk_path" -target "$native_target" \
    -dynamiclib -framework Foundation \
    -Wl,-install_name,@rpath/libLibzoraICloud.dylib \
    "$native_output/LibzoraICloud.o" -o "$native_output/libLibzoraICloud.dylib"
else
  xcrun --sdk "$native_sdk" libtool -static \
    -o "$native_output/libLibzoraICloud.a" "$native_output/LibzoraICloud.o"
fi
