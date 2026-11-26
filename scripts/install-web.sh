#!/usr/bin/env bash
# Inject web/lmsubs.js into jellyfin-web's index.html.
#
# Usage:
#   sudo ./scripts/install-web.sh [WEB_DIR]
#
# WEB_DIR defaults to the first existing of:
#   /usr/share/jellyfin/web  /usr/lib/jellyfin/bin/jellyfin-web  /usr/share/jellyfin-web
#
# Docker users: mount lmsubs.js into the web root and add the script tag, e.g.
#   docker run ... \
#     -v /path/to/lmsubs.js:/jellyfin/jellyfin-web/lmsubs.js:ro \
#     jellyfin/jellyfin
#   then exec into the container once:
#     sed -i 's#</body>#<script src="lmsubs.js" defer></script></body>#' /jellyfin/jellyfin-web/index.html
#   (or use the nginx sub_filter approach in README — survives image upgrades)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC_JS="$SCRIPT_DIR/../web/lmsubs.js"
TAG='<script src="lmsubs.js" defer></script>'

WEB_DIR="${1:-}"
if [[ -z "$WEB_DIR" ]]; then
    for d in /usr/share/jellyfin/web /usr/lib/jellyfin/bin/jellyfin-web /usr/share/jellyfin-web /jellyfin/jellyfin-web; do
        if [[ -f "$d/index.html" ]]; then WEB_DIR="$d"; break; fi
    done
fi
if [[ -z "$WEB_DIR" || ! -f "$WEB_DIR/index.html" ]]; then
    echo "找不到 jellyfin-web 目录，请显式传入: $0 /path/to/jellyfin-web" >&2
    exit 1
fi

echo "web 目录: $WEB_DIR"
cp "$SRC_JS" "$WEB_DIR/lmsubs.js"

if grep -q 'lmsubs.js' "$WEB_DIR/index.html"; then
    echo "index.html 已包含 lmsubs.js，仅更新了脚本文件。"
else
    cp "$WEB_DIR/index.html" "$WEB_DIR/index.html.lmsubs.bak"
    sed -i "s#</body>#$TAG</body>#" "$WEB_DIR/index.html"
    echo "已注入 <script> 标签（备份: index.html.lmsubs.bak）。"
fi
echo "完成。刷新浏览器（Ctrl+F5）后，播放页右下角会出现「LM」按钮。"
echo "注意: Jellyfin 升级/重装后 web 目录会被覆盖，需要重新执行本脚本。"
