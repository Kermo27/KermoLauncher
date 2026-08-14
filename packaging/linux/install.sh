#!/usr/bin/env bash
# Installs the linux-x64 single-file binary for the current user.
# Usage: ./install.sh /path/to/KermoLauncher-*-linux-x64
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <KermoLauncher-*-linux-x64>" >&2
  exit 1
fi

SRC=$(readlink -f "$1")
BIN_DIR="${XDG_BIN_HOME:-$HOME/.local/bin}"
APP_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICON_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"
HERE="$(cd "$(dirname "$0")" && pwd)"
DESKTOP_SRC="$HERE/kermolauncher.desktop"
ICON_SRC="$HERE/kermolauncher.png"

mkdir -p "$BIN_DIR" "$APP_DIR" "$ICON_DIR"
install -m 755 "$SRC" "$BIN_DIR/kermolauncher"
if [[ -f "$ICON_SRC" ]]; then
  install -m 644 "$ICON_SRC" "$ICON_DIR/kermolauncher.png"
fi

# Point Exec at the installed binary so the desktop entry works without PATH tricks.
sed "s|^Exec=.*|Exec=$BIN_DIR/kermolauncher|" "$DESKTOP_SRC" > "$APP_DIR/kermolauncher.desktop"
chmod 644 "$APP_DIR/kermolauncher.desktop"

echo "Installed to $BIN_DIR/kermolauncher"
echo "Desktop entry: $APP_DIR/kermolauncher.desktop"
echo "The binary lives in a user-writable directory so in-app updates can replace it."
