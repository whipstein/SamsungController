#!/usr/bin/env sh
# Optional per-user shortcut. No root access, login service, or automatic launch.
set -eu
PACKAGE_DIRECTORY=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
APPLICATION_DIRECTORY="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
mkdir -p "$APPLICATION_DIRECTORY"
# Desktop-entry quoted arguments escape backslash, quote, backtick and dollar.
ESCAPED_EXECUTABLE=$(printf '%s' "$PACKAGE_DIRECTORY/SamsungController.App" | sed 's/[\\"`$]/\\&/g; s/%/%%/g')
printf '%s\n' '[Desktop Entry]' 'Type=Application' 'Name=SamsungController' \
  "Exec=\"$ESCAPED_EXECUTABLE\"" 'Terminal=false' 'Categories=Utility;' \
  'Comment=Local Samsung display control' > "$APPLICATION_DIRECTORY/samsungcontroller.desktop"
chmod 755 "$APPLICATION_DIRECTORY/samsungcontroller.desktop"
printf '%s\n' 'SamsungController is now available in your applications menu. Keep its extracted folder in this location.'
