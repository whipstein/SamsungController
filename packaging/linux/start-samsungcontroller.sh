#!/usr/bin/env sh

set -u

SCRIPT_DIRECTORY=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
WEB_EXECUTABLE="$SCRIPT_DIRECTORY/SamsungController.Web"
CONTROLLER_URL="http://127.0.0.1:5050"

cd "$SCRIPT_DIRECTORY" || exit 1

if [ ! -x "$WEB_EXECUTABLE" ]; then
    echo "SamsungController.Web is missing or is not executable."
    echo "Extract the complete release archive, then try again."
    exit 1
fi

is_ready() {
    if command -v curl >/dev/null 2>&1; then
        curl --fail --silent --output /dev/null "$CONTROLLER_URL"
    elif command -v wget >/dev/null 2>&1; then
        wget --quiet --spider "$CONTROLLER_URL"
    else
        return 1
    fi
}

open_browser() {
    if command -v xdg-open >/dev/null 2>&1; then
        xdg-open "$CONTROLLER_URL" >/dev/null 2>&1
    elif command -v gio >/dev/null 2>&1; then
        gio open "$CONTROLLER_URL" >/dev/null 2>&1
    else
        echo "Open $CONTROLLER_URL in a browser."
    fi
}

open_when_ready() {
    attempt=0
    while [ "$attempt" -lt 240 ]; do
        if is_ready; then
            open_browser
            return
        fi

        sleep 0.25
        attempt=$((attempt + 1))
    done

    echo "SamsungController did not become ready at $CONTROLLER_URL."
}

open_when_ready &
BROWSER_WATCHER_PID=$!

cleanup() {
    kill "$BROWSER_WATCHER_PID" >/dev/null 2>&1 || true
}

trap cleanup EXIT INT TERM

echo "Starting SamsungController..."
echo "Keep this window open while using the web interface."
echo "Open $CONTROLLER_URL manually if the browser does not appear."
echo

"$WEB_EXECUTABLE"
EXIT_CODE=$?

if [ "$EXIT_CODE" -ne 0 ]; then
    echo
    echo "SamsungController stopped with error code $EXIT_CODE."
    echo "Another program may already be using port 5050."
fi

exit "$EXIT_CODE"
