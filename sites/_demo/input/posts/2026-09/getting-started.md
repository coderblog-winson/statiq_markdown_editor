---
Title: "Getting started: clone, run, build, preview"
Description: "From zero to a rendered demo site in about five minutes. No sign-up, no API key, no cloud account."
Date: 2026-09-16
Author: Statiq Markdown Editor
Layout: _PostLayout
Category: Tutorial
Tags: [tutorial, quickstart, dotnet]
---

# Getting started

This is the shortest path to a working site on your machine.

## What you need

- **.NET 9 SDK** (or .NET 10 — the editor compiles on either). Install from
  [dot.net](https://dotnet.microsoft.com/download).
- **Python 3** (only for `python3 -m http.server` during Preview). On macOS
  Python 3 ships with Xcode command-line tools. On Linux, install via your
  package manager.
- About 200 MB of disk space for the editor's own dependencies.

No `git` credentials, no API keys, no Docker.

## Clone & run

```bash
git clone https://github.com/winsonet/statiq_markdown_editor.git
cd statiq_markdown_editor
dotnet run
```

The first `dotnet run` does a restore + build, which takes 30–60 seconds.
After that, it listens on `http://127.0.0.1:5070`.

Open that URL in a browser. You'll land on the **Posts** list — empty, until
you build the demo.

## Build & preview the demo

1. Look at the top-right of the editor — there's a site dropdown showing
   `_demo`. That's the active site.
2. Click **🛠 Build**. The button shows a modal that streams Statiq's
   build log line-by-line. A 30-post site takes ~10 seconds.
3. Click **▶ Preview**. A second modal starts a Python HTTP server on port
   `5080` and tells you the URL. Open it in a new tab to see the rendered
   demo site.

That's the whole loop: edit → build → preview → ship.

## What the buttons actually do

The four toolbar buttons are wired to per-site shell scripts under
`sites/<name>/scripts/`:

| Button | Runs | Long-lived? |
|---|---|---|
| **Build** | the in-process Statiq pipeline (no shell) | no |
| **Preview** | `scripts/preview.sh` | yes — keeps a `python3 -m http.server` alive |
| **Deploy** | `scripts/build_deploy.sh` | no |
| **Git Sync** | `scripts/git_sync.sh` | no |

The demo's `scripts/` directory ships empty (just a `README.md`) — that's
fine, the editor only invokes those paths that exist. If you want a real
preview server, copy a starter `preview.sh` from one of your existing sites.

## Next steps

- **Add a new site**: copy `sites/_demo` to `sites/blog`, edit the
  `config.json`, then point Settings → Activate at it. See the project
  README for the full walkthrough.
- **Customise the theme**: open `themes/_demo/input/_Layout.cshtml` and the
  other templates in your text editor of choice. Rebuild to see changes.
- **Replace the editor's placeholder ads/analytics IDs**: open the demo's
  `config.json` and fill in your own `GoogleAnalyticsId` / `AdSenseId` /
  `UmamiSiteId`. The theme reads them via `Context.Settings.GetString(...)`.