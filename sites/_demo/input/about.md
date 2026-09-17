---
Title: "About this demo"
Description: "What you're looking at, and why it exists."
Date: 2026-09-15
Layout: _Layout.cshtml
---

# About this demo

This is a working Statiq site served by the
[Statiq Markdown Editor](https://github.com/winsonet/statiq_markdown_editor).

It's the editor's "first run" experience — clone the repo, `dotnet run`,
click **Build**, click **Preview**, and this site appears at
`http://127.0.0.1:5080`.

## What it shows

The demo covers the parts of a Statiq workflow you'll actually edit:

- **Five posts** under `sites/_demo/input/posts/2026-09/` demonstrating
  different frontmatter shapes (full, minimal, with image, with code blocks,
  with internal links).
- **A complete theme** under `themes/_demo/input/` — every layout,
  every page template, an RSS feed, a sitemap, a 404 page, and a CSS file
  you can read without checking into anything else.
- **Settings injection** — the theme reads `Context.Settings.GetString(...)`
  for site title, Google Analytics ID, etc. The demo ships empty strings,
  so no external requests are made.

## The four-button publish loop

The editor's top-right toolbar has four buttons that compose into a publish
pipeline:

| Button | What it does | When to use it |
|---|---|---|
| 🛠 **Build** | Runs Statiq in-process | After every edit |
| ▶ **Preview** | Builds + starts `python -m http.server :5080` | Right before you publish |
| ↑ **Deploy** | Runs `scripts/build_deploy.sh` (rsync + commit) | When you're ready to ship |
| ⇆ **Git Sync** | Runs `scripts/git_sync.sh` alone | Source-only changes, no rebuild needed |

The demo ships with an empty `scripts/` directory (just a `README.md`) so
the **Deploy** and **Git Sync** buttons report "script not configured". Add
your own scripts there to wire up your VPS or your remote repo.

## License

MIT. See [LICENSE](https://github.com/winsonet/statiq_markdown_editor/blob/main/LICENSE).

The demo posts are CC0 — copy, edit, use them however you want.