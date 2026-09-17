#!/bin/bash
# preview.sh — Start a local Python HTTP server to preview the rendered site.
#
# Adapted for the new sites/<name>/ layout (Sep 2026):
#   - No more `dotnet run` here — build is handled in-process by the
#     editor's Build button. This script only serves output/.
#   - Layout:
#       sites/<name>/
#       ├── config.json
#       ├── input/{posts,images}/
#       ├── scripts/preview.sh     ← this file
#       └── output/                ← editor generates this via Build button
#
# Usage:
#   ./preview.sh                  # default port from config.json (5080)
#   ./preview.sh 9090             # override port
#
# The editor's ScriptRunnerService pre-emptively kills any stale process
# on the port before calling this script. The port-recovery dance below
# is a defensive backup in case the script is run outside the editor
# (e.g. from a shell).

set -euo pipefail

DEFAULT_PORT="${PREVIEW_PORT:-5080}"
HOST="127.0.0.1"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SITE_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
OUTPUT_DIR="${SITE_DIR}/output"
PID_FILE="${SCRIPT_DIR}/.preview_server.pid"
LOG_FILE="${SCRIPT_DIR}/.preview_server.log"

# === Argument parsing ===
PORT="${DEFAULT_PORT}"
for arg in "$@"; do
    case "${arg}" in
        --help|-h)
            awk 'NR>1 && /^[^#]/ {exit} NR>1 {sub(/^# ?/,""); print}' "$0"
            exit 0
            ;;
        --skip-port-check) ;;
        -*)
            echo "Unknown argument: ${arg}" >&2
            exit 1
            ;;
        *)
            PORT="${arg}"
            ;;
    esac
done

# === Environment ===
export PATH="/usr/local/share/dotnet:/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:${PATH}"

# === Enter site directory ===
cd "${SITE_DIR}"

# === Cleanup: kill any previous background server (if present) ===
cleanup() {
    if [[ -f "${PID_FILE}" ]]; then
        local old_pid
        old_pid="$(cat "${PID_FILE}" 2>/dev/null || true)"
        if [[ -n "${old_pid}" ]] && kill -0 "${old_pid}" 2>/dev/null; then
            echo ""
            echo "→ Stopping preview server (PID ${old_pid})"
            kill "${old_pid}" 2>/dev/null || true
        fi
        rm -f "${PID_FILE}"
    fi
}
trap cleanup EXIT INT TERM
cleanup

# === Step 1: check output ===
if [[ ! -f "${OUTPUT_DIR}/index.html" ]]; then
    echo "❌ Cannot find ${OUTPUT_DIR}/index.html" >&2
    echo "   Please click the '🛠 Build' button in the editor first." >&2
    exit 1
fi

# === Step 2: port check / auto-recovery ===
if lsof -ti tcp:"${PORT}" >/dev/null 2>&1; then
    echo "⚠️  Port ${PORT} is busy — killing the occupant..."
    lsof -i tcp:"${PORT}" | tail -n +2 | awk '{printf "   %-12s PID %-7s user %s\n", $1, $2, $3}'
    OCCUPANT_PIDS=($(lsof -ti tcp:"${PORT}"))
    for pid in "${OCCUPANT_PIDS[@]}"; do
        kill "${pid}" 2>/dev/null || true
    done
    for _ in 1 2 3 4 5 6; do
        if ! lsof -ti tcp:"${PORT}" >/dev/null 2>&1; then
            break
        fi
        sleep 0.5
    done
    if lsof -ti tcp:"${PORT}" >/dev/null 2>&1; then
        echo "   Process didn't respond to SIGTERM, escalating to SIGKILL..."
        for pid in $(lsof -ti tcp:"${PORT}"); do
            kill -9 "${pid}" 2>/dev/null || true
        done
        sleep 0.5
    fi
    if lsof -ti tcp:"${PORT}" >/dev/null 2>&1; then
        echo "❌ Cannot free port ${PORT} (system process or insufficient permissions?)" >&2
        lsof -i tcp:"${PORT}" | tail -n +2 >&2
        exit 1
    fi
    echo "✅ Port ${PORT} is now free"
fi

# === Step 3: launch Python HTTP server (background) ===
echo ""
echo "🚀 Launching preview server"
echo "   URL:        http://${HOST}:${PORT}/"
echo "   Document root: ${OUTPUT_DIR}/"
echo "   Log:        ${LOG_FILE}"
echo "   Stop:       Ctrl+C (or click Stop in the editor)"
echo ""

cd "${OUTPUT_DIR}"
python3 -m http.server "${PORT}" --bind "${HOST}" >"${LOG_FILE}" 2>&1 &
SERVER_PID=$!
echo "${SERVER_PID}" > "${PID_FILE}"

sleep 1

if ! kill -0 "${SERVER_PID}" 2>/dev/null; then
    echo "❌ Server failed to start, log:" >&2
    cat "${LOG_FILE}" >&2
    exit 1
fi

echo "✅ Server is running in the background (PID ${SERVER_PID})"
echo ""
echo "👉 Open in browser: http://${HOST}:${PORT}/"
echo ""

wait "${SERVER_PID}"