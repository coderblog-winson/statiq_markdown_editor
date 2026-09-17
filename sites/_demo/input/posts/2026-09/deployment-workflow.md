---
Title: "Deployment workflow: from local edit to live site"
Description: "How the four toolbar buttons (Build, Preview, Deploy, Git Sync) compose into a publish loop."
Date: 2026-09-19
Author: Statiq Markdown Editor
Layout: _PostLayout
Category: Tutorial
Tags: [deployment, rsync, git, workflow, tutorial]
---

# Deployment workflow

The four toolbar buttons aren't independent — they're a small publish
pipeline. Used in order they compose into: **edit → build → preview →
commit → ship**.

## The loop

```text
edit (Monaco, in browser)
  │
  ▼
save (PUT /api/posts/{path})
  │
  ▼
build (🛠 Build → in-process Bootstrapper)
  │
  ▼
preview (▶ Preview → python http.server :5080)
  │
  ▼
deploy (↑ Deploy → scripts/build_deploy.sh)
  │            rsync output/ VPS:/path/to/index/
  │            bash scripts/git_sync.sh
  ▼
Git Sync (⇆ Git Sync → scripts/git_sync.sh)
            git add/commit/push origin
```

In practice you don't use every button every time:

| Scenario | Buttons |
|---|---|
| Editing and want to see the result | **Build** + **Preview** |
| You're done with a post, want it live | **Build** + **Deploy** |
| Fixed a typo in an old post (no rebuild needed) | **Git Sync** |
| Want to commit but defer deploy | **Build** + **Git Sync** (commit + push source) |

The **Deploy** button is the only one that touches a VPS. The **Git Sync**
button only commits source code. **Build** + **Preview** never leave your
machine.

## What `build_deploy.sh` actually does

The editor ships an empty `scripts/` for the demo site, but a real
deployment script looks like:

```bash
#!/bin/bash
set -e
SITE_DIR="$(cd "$(dirname "$0")/.." && pwd)"
OUTPUT_DIR="${SITE_DIR}/output"

KEY_PATH="~/.ssh/oracle.key"
USER="ubuntu"
IP="152.70.142.214"
REMOTE_DIR="/opt/1panel/www/sites/www.example.com/index/"

if [[ ! -f "${OUTPUT_DIR}/index.html" ]]; then
    echo "❌ Run the editor's Build button first." >&2
    exit 1
fi

echo "📦 rsync → ${USER}@${IP}:${REMOTE_DIR}/"
rsync -avz --delete --rsync-path="sudo rsync" \
    -e "ssh -i ${KEY_PATH}" \
    "${OUTPUT_DIR}/" "${USER}@${IP}:${REMOTE_DIR}"

bash "${SITE_DIR}/scripts/git_sync.sh"
```

The pattern:

1. **Verify output exists** — if the editor's Build didn't run, rsync'ing
   an empty directory would wipe the live site.
2. **rsync with `--delete`** — mirrors the local output to the VPS exactly.
   Old files in `REMOTE_DIR` that aren't in local output get deleted.
3. **`git_sync.sh`** — commit the source change that triggered this
   deploy (the post you just added or edited).

## What `git_sync.sh` does

This one's small. It's just:

```bash
#!/bin/bash
set -e
SITE_DIR="$(cd "$(dirname "$0")/../.." && pwd)"  # → sites/<name>/
cd "${SITE_DIR}"

[ ! -d .git ] && { echo "no .git/ here, skipping"; exit 0; }
[ -z "$(git status --porcelain)" ] && { echo "no changes"; exit 0; }

ts=$(date '+%Y-%m-%d %H:%M')
git add -A
git commit -m "auto: publish ${ts}"
git pull --rebase --autostash origin main
git push origin main
```

Two exit paths for safety:

- **No `.git/` directory** — you're running the script outside a repo
  (e.g. you moved it to a fresh clone and forgot to `git init`). Skip
  gracefully instead of crashing.
- **No changes** — someone else already pushed, or the post wasn't saved.
  Skip.

The `pull --rebase --autostash` line keeps "publish from two laptops"
working: if you forgot you already pushed this morning from another
machine, the rebase silently folds your local commits on top of theirs.

## The demo's `scripts/` is intentionally empty

The demo site doesn't ship a real `build_deploy.sh` — the user of the
demo presumably runs the editor locally and only cares about **Build** +
**Preview**. The four-button UI all works with empty scripts; the
**Deploy** and **Git Sync** buttons just say "script not configured for
site '_demo'" and return an error.

When you copy the demo to make your own site:

```bash
cp -R sites/_demo sites/blog
# Edit sites/blog/config.json with your own Host, theme, etc.
# Drop your own theme into sites/blog/themes/

# Copy starter scripts from one of your existing sites, or write your own:
cp ../coderblog.statiq/scripts/preview.sh sites/blog/scripts/preview.sh
cp ../coderblog.statiq/scripts/build_deploy.sh sites/blog/scripts/build_deploy.sh
cp ../coderblog.statiq/scripts/git_sync.sh sites/blog/scripts/git_sync.sh
chmod +x sites/blog/scripts/*.sh

# Edit the VPS paths in build_deploy.sh
$EDITOR sites/blog/scripts/build_deploy.sh
```

The editor will pick up the new site automatically — open Settings,
click **Activate** on `blog`, and you're publishing.

## What gets committed where

A typical publish leaves a trail like this:

| Repo | What lands there | When |
|---|---|---|
| **`statiq_markdown_editor`** (public) | editor code, demo site, demo theme | when you change editor features |
| **`statiq-sites`** (private, your fork) | `sites/blog/{input,themes,scripts}` | every **Git Sync** or **Deploy** |
| **VPS** | rendered HTML in `output/` | every **Deploy** |

The demo's `sites/_demo/` lives in the public `statiq_markdown_editor`
repo because it's meant to be cloned. Your real sites live in a
separate private repo (e.g. `statiq-sites`) so the markdown + theme
configs never leak.

That's the whole publish story. Build locally, preview locally, deploy
to a VPS with rsync, push the source to a private GitHub repo. Three
independent moves; one button each.