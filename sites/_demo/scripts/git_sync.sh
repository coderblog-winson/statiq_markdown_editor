#!/bin/bash
# git_sync.sh — commit + push all local changes to GitHub.
#
# Called from build_deploy.sh after a successful deploy. Designed to be
# idempotent and non-fatal: if there's nothing to commit, exit 0; if push
# fails, the commit is kept locally and the deploy still counts as
# successful (the next sync will retry).
#
# Commit message: fixed format with timestamp, e.g.
#     auto: publish 2026-09-05 09:55
# — the deploy output is not parsed, this stays a simple, auditable
#   one-line message per push.
#
# Path: this script lives next to build_deploy.sh in the project root
# and cds to its own directory before running git, so it can be called
# from anywhere.

set -e
# The .git/ for this site lives at sites/<name>/ (the directory above
# scripts/). After the editor's Sep-2026 refactor each site is its own
# independent git repo with its own GitHub remote, so the script must
# cd to the site root to find .git/.
SITE_DIR="$(cd "$(dirname "$0")/.." && pwd)"
cd "${SITE_DIR}"

# --- Pre-flight: skip if not a git repo or no remote -----------------------
if [ ! -d .git ]; then
    echo "[git_sync] no .git/ here, skipping"
    exit 0
fi
remote=$(git remote get-url origin 2>/dev/null || true)
if [ -z "$remote" ]; then
    echo "[git_sync] no 'origin' remote configured, skipping"
    exit 0
fi

# --- Skip if no changes ---------------------------------------------------
# `git status --porcelain` lists everything (staged, unstaged, untracked
# that aren't gitignored). Empty = nothing to do.
if [ -z "$(git status --porcelain)" ]; then
    echo "[git_sync] no changes to commit, skipping"
    exit 0
fi

# --- Commit ---------------------------------------------------------------
# Use a fixed prefix + timestamp; the user's choice for traceability.
ts=$(date '+%Y-%m-%d %H:%M')
msg="auto: publish ${ts}"

# Show what we're about to commit, so the log records the intent.
echo "[git_sync] about to commit:"
git status --short | sed 's/^/    /'
git add -A
if git commit -m "$msg"; then
    :
else
    # Shouldn't happen given the empty-check above, but be defensive.
    echo "[git_sync] nothing to commit (race with another process?)"
    exit 0
fi

# --- Push -----------------------------------------------------------------
branch=$(git branch --show-current)
if [ -z "$branch" ]; then
    echo "[git_sync] WARNING: no current branch — commit kept locally"
    exit 0
fi

# Rebase first in case origin has commits we don't (e.g. edits on
# another machine). Silently no-op if there's nothing to pull. This
# keeps "publish from 2 laptops" working without manual `git pull`.
git pull --rebase --autostash origin "$branch" 2>/dev/null || true

echo "[git_sync] pushing origin/${branch}..."
if git push origin "$branch"; then
    echo "[git_sync] pushed ✓"
else
    # Don't fail the deploy over a push error. The commit stays local
    # and the next run (or the user manually) can retry.
    echo "[git_sync] WARNING: push failed (commit kept locally, will retry next deploy)"
fi
