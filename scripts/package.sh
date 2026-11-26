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
echo "打包完成: $OUT"
echo "MD5: $MD5"
echo "把 manifest.json 中 checksum 替换为上面的 MD5，sourceUrl 指向 zip 的下载地址，"
echo "然后在 Jellyfin 控制台 → 插件 → 存储库 添加 manifest.json 的 URL 即可在线安装。"
