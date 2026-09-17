---
Title: "Welcome to the Statiq Markdown Editor demo"
Description: "A quick tour of what this editor does, what the demo site shows, and how to drive the four toolbar buttons."
Date: 2026-09-15
Author: Statiq Markdown Editor
Layout: _PostLayout
Category: Documentation
Tags: [statiq, editor, markdown, dotnet]
---

# Welcome

If you're reading this, you've successfully cloned `statiq_markdown_editor`,
ran `dotnet run`, hit **Build**, and started the **Preview** server.

What you're looking at is the demo site under `sites/_demo/` rendered by the
demo theme under `themes/_demo/`. Everything you see here is in the editor's
public repo — feel free to open any of these files in your editor of choice
and edit them, then come back to the web UI and click **Build** again to see
your changes.

## What's in this demo

The demo site covers the parts of a Statiq theme you'll actually edit most
often:

- **`_Layout.cshtml`** — the master template. Header, footer, meta tags.
- **`_PostLayout.cshtml`** — single-post layout. Reads the post's frontmatter.
- **`_ListLayout.cshtml`** — list-of-posts layout. Used by the home page, tags, archives.
- **`_TagLayout.cshtml`** — one tag's posts, generated automatically by Statiq.
- **`index.cshtml`** — the home page (just `<ListLayout>` + a section).
- **`archives.cshtml`** — posts grouped by month.
- **`tags.cshtml`** — every tag, with the post count next to each.
- **`about.cshtml`** — the about page.
- **`feed.cshtml`** — RSS feed (writes `application/rss+xml`).
- **`_SitemapTemplate.cshtml`** — XML sitemap.

The **posts** in `sites/_demo/input/posts/2026-09/` cover the frontmatter
shapes you'll actually want to use:

| File | Demonstrates |
|---|---|
| `welcome-to-statiq-editor.md` | this post — full frontmatter with tags, category |
| `getting-started.md` | minimal frontmatter, no description, no image |
| `frontmatter-reference.md` | every field, plus a long body to test reading time |
| `theme-development.md` | code blocks (Statiq.Razor inside an HTML pre) |
| `deployment-workflow.md` | internal links via the `/posts/...` path |

If you delete a post and rebuild, it disappears from the index, archives,
tags, and sitemap — the demo is a useful playground for testing what the
build pipeline does to your content.

## The four buttons

The editor's top-right toolbar has:

- **🛠 Build** — runs the in-process Statiq pipeline against the active site.
- **▶ Preview** — builds, then starts a Python HTTP server on the site's
  configured port (5080 for this demo). The editor auto-kills any stale
  process on that port before starting.
- **↑ Deploy** — runs the site's `scripts/build_deploy.sh`. By default that's
  an `rsync` to a VPS, but you can replace it with anything.
- **⇆ Git Sync** — runs `scripts/git_sync.sh` on its own. Use this when you've
  changed source only (no rebuild needed) and want to push to GitHub.

Read on for the quick-start.