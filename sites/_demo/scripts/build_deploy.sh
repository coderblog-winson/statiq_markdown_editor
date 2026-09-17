#!/bin/bash
# build_deploy.sh — Sync sites/<name>/output/ to your server, then git-sync.
#
# Adapted for the new sites/<name>/ layout (Sep 2026):
#   - No more `dotnet run` here — the editor's Build button generates
#     output/ in-process. This script only does the deploy + git-sync.
#   - The editor's Deploy button calls this via /api/sites/{name}/deploy.
#
# FORK THIS FOR YOUR SITE:
#   1. Fill in the SERVER_* variables below with your own host, user,
#     ssh key path, and remote web root.
#   2. Adjust the rsync flags if you don't need --delete (rare).
#   3. Commit and push to your own fork.

set -e
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SITE_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
OUTPUT_DIR="${SITE_DIR}/output"

# --- Server configuration (fill these in for your site) ---
# Examples below — replace with your own values:
#
#   KEY_PATH="~/.ssh/your_server_key"
#   USER="deploy"
#   IP="your.server.example.com"
#   REMOTE_DIR="/var/www/your-domain/html/"
KEY_PATH=""
USER=""
IP=""
REMOTE_DIR=""

# --- Guard: refuse to run with empty config ---
if [[ -z "${KEY_PATH}" || -z "${USER}" || -z "${IP}" || -z "${REMOTE_DIR}" ]]; then
    echo "❌ build_deploy.sh: server config not filled in yet." >&2
    echo "" >&2
    echo "   Edit this file and set:" >&2
    echo "     KEY_PATH    path to your SSH private key (~/.ssh/...)" >&2
    echo "     USER        SSH user on the server (e.g. deploy, ubuntu)" >&2
    echo "     IP          server hostname or IP" >&2
    echo "     REMOTE_DIR  absolute path on the server where nginx serves the site" >&2
    echo "" >&2
    echo "   Then re-run ./build_deploy.sh." >&2
    exit 2
fi

# --- Verify output exists ---
if [[ ! -f "${OUTPUT_DIR}/index.html" ]]; then
    echo "❌ Cannot find ${OUTPUT_DIR}/index.html" >&2
    echo "   Please click the '🛠 Build' button in the editor first." >&2
    exit 1
fi

# --- rsync to server ---
echo "📦 Syncing to server..."
echo "   Local:  ${OUTPUT_DIR}/"
echo "   Remote: ${USER}@${IP}:${REMOTE_DIR}"
rsync -avz --delete --rsync-path="sudo rsync" -e "ssh -i ${KEY_PATH}" \
    "${OUTPUT_DIR}/" "${USER}@${IP}:${REMOTE_DIR}"

echo "✅ Deploy complete!"

# --- Auto git-sync after successful deploy ---
echo "📝 Syncing to Git..."
bash "${SCRIPT_DIR}/git_sync.sh" || echo "[git_sync] warning: Git sync failed (deploy still counts as successful)"