#!/bin/zsh
# Fetches the optional reader body fonts into the local (git-ignored) catalog.
# Debug iOS builds bundle this directory so the "正文字体" download action works
# before the production font server exists. Release builds use the catalog's
# remote base URL instead.
#
# GitHub is often slow or unreachable from mainland networks. Each fetch tries
# public GitHub mirrors first and falls back to the origin, resuming partial
# downloads after a dropped connection.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEST="$ROOT/src/Libzora.App/Assets/Fonts/catalog"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
mkdir -p "$DEST/licenses"

MIRRORS=(
  "https://ghfast.top/"
  "https://ghproxy.net/"
  ""
)

remote_size() {
  local url="$1" mirror size
  for mirror in $MIRRORS; do
    size=$(/usr/bin/curl -fsIL --http1.1 --connect-timeout 15 --max-time 30 \
      "$mirror$url" 2>/dev/null |
      /usr/bin/tr -d '\r' |
      /usr/bin/awk 'tolower($1) == "content-length:" { n = $2 } END { print n + 0 }')
    if (( size > 0 )); then
      echo "$size"
      return 0
    fi
  done
  echo 0
}

fetch() {
  local url="$1" output="$2"
  local mirror attempt rc expected
  echo "→ $output"
  expected=$(remote_size "$url")
  if [[ -f "$output" ]] && (( expected > 0 )) \
      && [[ "$(/usr/bin/stat -f '%z' "$output")" -eq "$expected" ]]; then
    echo "  cached ($expected bytes)"
    return 0
  fi
  for mirror in $MIRRORS; do
    for attempt in {1..8}; do
      rc=0
      /usr/bin/curl -fL --http1.1 -C - --connect-timeout 20 \
        --speed-limit 4096 --speed-time 30 \
        -o "$output" "$mirror$url" || rc=$?
      # 33 = the server rejected the resume range because the file is
      # already complete.
      if (( rc == 0 || rc == 33 )); then
        return 0
      fi
      sleep 2
    done
    echo "  mirror failed: ${mirror:-direct}" >&2
  done
  echo "cannot fetch $url" >&2
  return 1
}

fetch "https://raw.githubusercontent.com/notofonts/noto-cjk/main/Serif/OTF/SimplifiedChinese/NotoSerifCJKsc-Regular.otf" \
  "$DEST/NotoSerifCJKsc-Regular.otf"
fetch "https://raw.githubusercontent.com/notofonts/noto-cjk/main/Sans/OTF/SimplifiedChinese/NotoSansCJKsc-Regular.otf" \
  "$DEST/NotoSansCJKsc-Regular.otf"
fetch "https://github.com/lxgw/LxgwWenKai-Lite/releases/download/v1.530/LXGWWenKaiLite-Regular.ttf" \
  "$DEST/LXGWWenKaiLite-Regular.ttf"

fetch "https://github.com/TrionesType/zhuque/releases/download/v0.212/ZhuqueFangsong-v0.212.zip" \
  "$TMP/zhuque.zip"
/usr/bin/unzip -q -o "$TMP/zhuque.zip" -d "$TMP/zhuque"
cp "$TMP/zhuque/ZhuqueFangsong-Regular.ttf" "$DEST/ZhuqueFangsong-Regular.ttf"

fetch "https://raw.githubusercontent.com/notofonts/noto-cjk/main/Serif/LICENSE" \
  "$DEST/licenses/OFL-Noto.txt"
fetch "https://raw.githubusercontent.com/lxgw/LxgwWenKai-Lite/main/OFL.txt" \
  "$DEST/licenses/OFL-LXGWWenKai.txt"
fetch "https://raw.githubusercontent.com/TrionesType/zhuque/main/LICENSE.txt" \
  "$DEST/licenses/OFL-Zhuque.txt"

echo
echo "Sizes:"
/usr/bin/stat -f '%z  %N' "$DEST"/*.otf "$DEST"/*.ttf
echo
echo "SHA-256:"
/usr/bin/shasum -a 256 "$DEST"/*.otf "$DEST"/*.ttf
