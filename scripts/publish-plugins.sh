#!/usr/bin/env bash
# Packs and signs official plugins into website/plugins/ for manual download
# and local import. build_website.sh deploys the packages without an index.
#
#   scripts/publish-plugins.sh                 # every folder under plugins/
#   scripts/publish-plugins.sh book-translation
#
# Signing key: $LIBZORA_PLUGIN_SIGNING_KEY (a PEM file path), defaulting to
# ~/.config/libzora/plugin-signing/official-p256.pem. Its public half is
# PluginSignature.OfficialKeys["Libzora"]; never commit the private key.
# In CI, write the secret to a temp file and point the variable at it.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

key="${LIBZORA_PLUGIN_SIGNING_KEY:-$HOME/.config/libzora/plugin-signing/official-p256.pem}"
if [[ ! -f "$key" ]]; then
  echo "signing key not found: $key (set LIBZORA_PLUGIN_SIGNING_KEY)" >&2
  exit 1
fi

out="website/plugins"
mkdir -p "$out"
dotnet build tools/Libzora.PluginTool -c Release -nologo -v q >/dev/null
tool=(dotnet tools/Libzora.PluginTool/bin/Release/net10.0/libzora-plugin.dll)

if [[ $# -gt 0 ]]; then names=("$@"); else names=($(ls plugins)); fi
for name in "${names[@]}"; do
  [[ -f "plugins/$name/manifest.json" ]] || continue
  "${tool[@]}" pack "plugins/$name" -o "$out" --key "$key" --publisher Libzora
done
