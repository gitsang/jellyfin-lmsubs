#!/usr/bin/env bash
# Build the plugin DLL and produce a repository-installable zip + updated manifest checksum.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

DOTNET="${DOTNET:-dotnet}"
command -v "$DOTNET" >/dev/null || DOTNET="$HOME/.dotnet/dotnet"

VERSION=$(grep -oPm1 '(?<=<Version>)[^<]+' Jellyfin.Plugin.LmSubs/Jellyfin.Plugin.LmSubs.csproj)
OUT="dist/lmsubs_${VERSION}.zip"

"$DOTNET" publish Jellyfin.Plugin.LmSubs -c Release -o dist/publish
mkdir -p dist
(cd dist/publish && zip -q "../lmsubs_${VERSION}.zip" Jellyfin.Plugin.LmSubs.dll)

MD5=$(md5sum "$OUT" | cut -d' ' -f1)
echo "Package built: $OUT"
echo "MD5: $MD5"
echo "Put this MD5 into manifest.json (checksum), point sourceUrl at the zip's download URL,"
echo "then add the manifest URL under Jellyfin Dashboard -> Plugins -> Repositories."
