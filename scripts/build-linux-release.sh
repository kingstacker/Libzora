#!/usr/bin/env bash
set -euo pipefail

version="${1:?usage: build-linux-release.sh VERSION [OUTPUT_DIR] [RID]}"
output_root="${2:-artifacts/linux}"
rid="${3:-linux-x64}"

case "$rid" in
  linux-x64) deb_arch="amd64" ;;
  linux-arm64) deb_arch="arm64" ;;
  *) echo "Unsupported Linux RID: $rid" >&2; exit 2 ;;
esac

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$repo_root/scripts/dotnet-sdk.sh"
require_repo_dotnet_sdk "$repo_root"
output_root="$(mkdir -p "$output_root" && cd "$output_root" && pwd)"
work_root="$(mktemp -d)"
trap 'rm -rf "$work_root"' EXIT
publish_root="$work_root/publish"
package_root="$work_root/deb"

cd "$repo_root"
dotnet publish "src/Libzora.Desktop.Linux/Libzora.Desktop.Linux.csproj" \
  -c Release -r "$rid" --self-contained true \
  -p:Version="$version" -p:PublishSingleFile=false \
  -o "$publish_root"

cp "$repo_root/LICENSE" "$publish_root/LICENSE"
cp "$repo_root/docs/cross-platform.md" "$publish_root/LINUX-README.md"
tar -C "$publish_root" -czf "$output_root/Libzora-$version-$rid.tar.gz" .

install -d "$package_root/DEBIAN" "$package_root/opt/libzora" \
  "$package_root/usr/bin" "$package_root/usr/share/applications" \
  "$package_root/usr/share/icons/hicolor/256x256/apps" "$package_root/usr/share/pixmaps"
cp -a "$publish_root/." "$package_root/opt/libzora/"
install -m 0644 "$repo_root/src/Libzora.App/Assets/Icons/libzora.png" \
  "$package_root/usr/share/icons/hicolor/256x256/apps/libzora.png"
install -m 0644 "$repo_root/src/Libzora.App/Assets/Icons/libzora.png" \
  "$package_root/usr/share/pixmaps/libzora.png"

cat > "$package_root/DEBIAN/control" <<EOF
Package: libzora
Version: $version
Section: utils
Priority: optional
Architecture: $deb_arch
Maintainer: kingstacker
Depends: libfontconfig1, libice6, libsm6, libsecret-tools, libwebkit2gtk-4.1-0 | libwpewebkit-2.0-1
Recommends: udisks2
Suggests: calibre, libwpewebkit-2.0-1
Description: Personal ebook library, reader and Kindle manager
 Libzora combines a local ebook library, reading, conversion and
 mounted USB Kindle management in an Avalonia desktop application.
EOF

cat > "$package_root/usr/bin/libzora" <<'EOF'
#!/usr/bin/env sh
export GDK_BACKEND="${GDK_BACKEND:-x11}"
exec /opt/libzora/Libzora "$@"
EOF
chmod 0755 "$package_root/usr/bin/libzora" "$package_root/opt/libzora/Libzora"

cat > "$package_root/usr/share/applications/libzora.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Libzora
Comment=Ebook library, reader and Kindle manager
Exec=libzora
Icon=libzora
Terminal=false
Categories=Office;Viewer;
StartupWMClass=libzora
EOF

dpkg-deb --build --root-owner-group "$package_root" \
  "$output_root/libzora_${version}_${deb_arch}.deb"
