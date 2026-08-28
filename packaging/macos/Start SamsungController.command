#!/bin/zsh

set -u

SCRIPT_DIRECTORY="$(cd "$(dirname "$0")" && pwd)"
WEB_EXECUTABLE="$SCRIPT_DIRECTORY/SamsungController.Web"
CONTROLLER_URL="http://127.0.0.1:5050"

cd "$SCRIPT_DIRECTORY" || exit 1

if [[ ! -x "$WEB_EXECUTABLE" ]]; then
    echo "SamsungController.Web is missing or is not executable."
    echo "Extract the complete release archive, then try again."
    read -r "?Press Return to close..."
    exit 1
fi

open_when_ready() {
    local attempt=0
    while (( attempt < 240 )); do
        if /usr/bin/curl --fail --silent --output /dev/null "$CONTROLLER_URL"; then
            /usr/bin/open "$CONTROLLER_URL"
            return
        fi

        /bin/sleep 0.25
        (( attempt++ ))
    done

    echo "SamsungController did not become ready at $CONTROLLER_URL."
}

open_when_ready &
BROWSER_WATCHER_PID=$!

cleanup() {
    /bin/kill "$BROWSER_WATCHER_PID" >/dev/null 2>&1 || true
}

trap cleanup EXIT INT TERM

echo "Starting SamsungController..."
echo "Keep this window open while using the web interface."
echo "Open $CONTROLLER_URL manually if the browser does not appear."
echo

"$WEB_EXECUTABLE"
EXIT_CODE=$?

if (( EXIT_CODE != 0 )); then
    echo
    echo "SamsungController stopped with error code $EXIT_CODE."
    echo "Another program may already be using port 5050."
    read -r "?Press Return to close..."
fi

exit "$EXIT_CODE"
