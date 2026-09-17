# Statiq Markdown Editor

A web-based local editor for managing [Statiq](https://www.statiq.dev/) static-site projects. Single-user, no cloud, no telemetry, no account. Just `dotnet run` and edit your posts in the browser.

> Edit posts in markdown. Click **Build** to render. Click **Preview** to see the site. Click **Deploy** to push to your VPS. Click **Git Sync** to commit & push.

The repo ships with a complete **demo site** (`sites/_demo`) you can build immediately. Add your own sites under `sites/<name>/` and the editor picks them up automatically.

---

## Why this exists

Statiq posts are flat markdown files. Editing them in a text editor is fine — but you still need a way to:

- Preview what a post looks like *before* deploying
- Group posts by date / tag without manual SQL
- Watermark uploaded images consistently
- Auto-save drafts so a bad keystroke doesn't lose 1,000 words
- Keep multiple sites under one tool

This editor is the panel between you and `git`. It is intentionally **not** a CMS: there is no DB, no API, no auth — only your local filesystem.

---

## 5-minute quickstart

```bash
git clone https://github.com/winsonet/statiq_markdown_editor.git
cd statiq_markdown_editor
dotnet run
```

Open <http://127.0.0.1:5070>. The settings page shows the `_demo` site already discovered. Click **Activate**, then click the **Build** button in the top nav. After ~10 seconds `sites/_demo/output/` has 29 rendered HTML files.

To preview the built site on disk:

```bash
cd sites/_demo
python3 -m http.server 5080
```

Open <http://127.0.0.1:5080>.

That's it. You have a working build pipeline.

---

## What you get

- **Multi-site dashboard** — each folder under `sites/<name>/` is a site. No config file to edit; the filesystem is the source of truth.
- **In-browser markdown editor** — split-pane edit/preview, image upload, drag-drop, autosave (1 min default), spell-check, watermark on every image.
- **Live build log** — click *Build* and watch the Statiq pipeline execute step by step.
- **Per-site sh scripts** — Preview / Deploy / Git Sync are real bash scripts in `sites/<name>/scripts/`. Edit them, version-control them, share them with your team.
- **Zero external services** — no API key, no SaaS, no telemetry. Disconnect from the internet and the editor still works.

---

## Architecture

```
statiq_markdown_editor/                  ← this repo (PUBLIC)
├── Program.cs / Services/ / Pages/      ← the ASP.NET Core editor process
├── appsettings.json                     ← just { ActiveProjectName: "_demo" }
├── Statiq/                              ← shared Statiq.Web bootstrapper
├── wwwroot/                             ← Razor Pages UI (vanilla JS, no framework)
├── themes/_demo/                        ← the only PUBLIC theme — for the demo site
└── sites/
    ├── _demo/                           ← PUBLIC demo site (5 sample posts)
    │   ├── config.json
    │   ├── input/posts/2026-09/*.md
    │   ├── input/images/2026-09/*.webp
    │   ├── themes/                      ← self-contained theme for the demo
    │   └── scripts/                     ← sh scripts (Preview / Git Sync only)
    └── (your own sites — each is its own GitHub repo)
```

### What lives where

| Path | Lives in | Why |
|---|---|---|
| `Editor.csproj`, `Program.cs`, `Pages/` | this repo | shared editor shell |
| `themes/_demo/` | this repo | public demo theme |
| `sites/_demo/` | this repo | public demo content (5 posts + images) |
| `sites/<your-site>/config.json` | your own GitHub repo | site config |
| `sites/<your-site>/input/posts/` | your own GitHub repo | your markdown content |
| `sites/<your-site>/input/images/` | your own GitHub repo | your images |
| `sites/<your-site>/themes/` | your own GitHub repo | your theme (with the content) |
| `sites/<your-site>/scripts/` | your own GitHub repo | your preview/deploy/git_sync |

**One editor repo, many site repos.** Sites are independent GitHub repos so access control, secrets, and history don't get tangled.

---

## Adding a new site

1. **Create a folder under `sites/`:**

   ```bash
   mkdir -p sites/mysite/{input/posts,input/images,themes,scripts}
   ```

2. **Drop in a `sites/mysite/config.json`:**

   ```json
   {
     "Theme": "themes",
     "Host": "mysite.example.com",
     "StripMonthFromPostUrls": true,
     "UseHtmlExtensions": true,
     "PreviewScript": "scripts/preview.sh",
     "DeployScript": "scripts/build_deploy.sh",
     "GitSyncScript": "scripts/git_sync.sh",
     "PreviewPort": 5083
   }
   ```

3. **Copy the demo theme** (`themes/_demo/input/` → `sites/mysite/themes/input/`) as a starting point.

4. **Copy the scripts** (`sites/_demo/scripts/preview.sh` and `git_sync.sh`) — edit `REMOTE_DIR` inside `build_deploy.sh` to point at your VPS path.

5. **Write a post:**

   ```bash
   cat > sites/mysite/input/posts/2026-09/hello.md <<'EOF'
   ---
   Title: Hello world
   Description: First post on the new site.
   Date: 2026-09-20
   Layout: _PostLayout.cshtml
   Tags: [intro]
   ---

   # Hello

   This is my first post.
   EOF
   ```

6. **Restart `dotnet run`.** The settings page now shows `mysite`. Click *Activate*.

7. Click **Build**. Open `sites/mysite/output/index.html` to see the result.

8. Click **Preview** to spawn a `python3 -m http.server 5083` and view it in the browser.

9. Click **Deploy** to rsync `output/` to your VPS (edit `build_deploy.sh` first).

10. Click **Git Sync** to commit + push.

---

## The four buttons

| Button | What runs | Where the code lives |
|---|---|---|
| 🛠 **Build** | In-process `Statiq.Web` pipeline (~10s) | editor's `StatiqRunner.cs` |
| ▶ **Preview** | `bash sites/<name>/scripts/preview.sh` (python http.server) | per-site sh |
| ↑ **Deploy** | `bash sites/<name>/scripts/build_deploy.sh` (rsync to VPS) | per-site sh |
| ⇆ **Git Sync** | `bash sites/<name>/scripts/git_sync.sh` (commit + push) | per-site sh |

**Build** is fast (10–30 s) and runs inside the editor — no bash spawn, no ports. **Preview / Deploy / Git Sync** are slow / side-effecty so they live in plain shell scripts you can read and edit.

### The preview script — start small

```bash
#!/usr/bin/env bash
# sites/<name>/scripts/preview.sh <port>
PORT="${1:-5080}"
DIR="$(cd "$(dirname "$0")/.." && pwd)/output"

# If port is busy, kill the holder.
if lsof -ti tcp:"$PORT" >/dev/null 2>&1; then
  lsof -ti tcp:"$PORT" | xargs kill -9 2>/dev/null
  sleep 1
fi

cd "$DIR"
exec python3 -m http.server "$PORT"
```

That's all a preview script needs. The editor wraps it: kill any stale server, write a timestamped log to `scripts/logs/`, expose status via `/api/scripts/preview/status` and `/log?tail=500`.

### The build_deploy script — your VPS one-liner

```bash
#!/usr/bin/env bash
set -e
SITE_DIR="$(cd "$(dirname "$0")/.." && pwd)"
REMOTE_DIR="/opt/1panel/www/sites/www.example.com/index/"
VPS="user@myserver.example.com"

rsync -a --delete "$SITE_DIR/output/" "$VPS:$REMOTE_DIR/"
bash "$SITE_DIR/scripts/git_sync.sh"
```

You fill in `REMOTE_DIR` + `VPS`. The script just rsyncs `output/` to wherever Nginx serves from, then runs `git_sync.sh` to commit + push.

---

## Theme development

A theme is just a folder of Razor pages. Drop `_Layout.cshtml`, `_PostLayout.cshtml`, `index.cshtml` and you're rendering.

**Where the theme lives:**

```
sites/<name>/themes/input/
├── _ViewImports.cshtml      ← @using Statiq.Common, @inherits StatiqRazorPage<IDocument>
├── _Layout.cshtml           ← master layout (header / nav / footer)
├── _PostLayout.cshtml       ← single-post wrapper
├── index.cshtml             ← home page
├── about.cshtml             ← /about.html
├── archives.cshtml          ← /archives.html (groups by year-month)
├── tags.cshtml              ← /tags.html
├── feed.cshtml              ← /feed.html (RSS 2.0)
├── _SitemapTemplate.cshtml  ← custom sitemap.xml
├── page/2/index.cshtml      ← pagination
├── robots.txt
└── assets/
    ├── css/site.css
    └── favicon.svg
```

**Two ways to list documents inside a page:**

```cshtml
@* In a tag page (children set by GroupDocuments) *@
@foreach (var post in Document.GetChildren()
    .Where(c => !c.GetBool("Draft", false))
    .OrderByDescending(c => c.Get<DateTime>("Date"))) {
    <li><a href="@(post.GetLink())">@(post.GetString("Title"))</a></li>
}

@* On a listing page (no children) — pull from pipeline inputs *@
@foreach (var post in Context.Inputs
    .Where(d => d.ContainsKey("Title") && d.ContainsKey("Date"))) { ... }
```

**Don't reference `Documents` directly** — it's not available in layouts (only `Document`, the single page document, is). Use `Document.GetChildren()` for tag pages or `Context.Inputs` for everything else.

---

## Markdown frontmatter

```yaml
---
Title: My post title
Description: One-line summary for SEO and RSS.
Date: 2026-09-20
Layout: _PostLayout.cshtml      # which theme layout wraps this post
Image: /images/2026-09/hero.webp
Category: tutorials
Draft: false
Tags: [statiq, markdown, dotnet]
---
```

All eight fields are canonical. The editor writes them in this exact order on save. `Image` URLs in markdown body get rewritten to `/images/...` (the `input/` prefix is stripped).

---

## Configuration reference

### `appsettings.json`

```json
{
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "AllowedHosts": "*",
  "StatiqProject": { "ActiveProjectName": "_demo" }
}
```

`ActiveProjectName` decides which site the editor loads on startup. Override locally by editing `appsettings.Development.json` (gitignored).

### `sites/<name>/config.json`

| Field | Default | Meaning |
|---|---|---|
| `Theme` | `themes` | Theme dir, relative to `sites/<name>/` |
| `Host` | `""` | Public hostname (used for canonical URLs, RSS) |
| `StripMonthFromPostUrls` | `false` | `/posts/2026-09/foo.html` → `/posts/foo.html` |
| `UseHtmlExtensions` | `true` | Output `.html` extensions |
| `FigureStyle` | `default` | `default` / `custom-figure` / `inline-styled` |
| `GoogleAnalyticsId` | `""` | GA4 measurement ID |
| `AdSenseId` | `""` | AdSense publisher ID |
| `PreviewScript` | `scripts/preview.sh` | Bash script for ▶ Preview button |
| `DeployScript` | `scripts/build_deploy.sh` | Bash script for ↑ Deploy button |
| `GitSyncScript` | `scripts/git_sync.sh` | Bash script for ⇆ Git Sync button |
| `PreviewPort` | `5080` | Port for the Preview http.server |

---

## Programmatic endpoints

The editor exposes a small JSON API if you want to script it:

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/settings` | List all sites + active project |
| POST | `/api/projects/activate` | `{ "name": "mysite" }` switch active site |
| GET | `/api/posts` | List posts in active site |
| GET | `/api/posts/{slug}` | Read a post |
| POST | `/api/posts` | Create / update a post |
| POST | `/api/sites/{name}/build` | Run the in-process build |
| POST | `/api/sites/{name}/preview` | Spawn the preview script |
| POST | `/api/sites/{name}/deploy` | Spawn the deploy script |
| POST | `/api/sites/{name}/git-sync` | Spawn the git_sync script |
| POST | `/api/scripts/{kind}/stop` | Kill a running preview/deploy/git-sync |
| GET | `/api/scripts/{kind}/status` | `{ alive: true, exitCode: 0 }` |
| GET | `/api/scripts/{kind}/log?tail=500` | Tail the log file |

---

## Limitations (by design)

- **No multi-user.** The editor binds to `127.0.0.1`. Don't expose it on a LAN without auth.
- **No cloud sync.** Files live on your disk. If your laptop dies, so does your content — git push is your backup.
- **No live preview.** You click **Build** to render; the editor doesn't watch files.
- **No image optimization pipeline.** `webp` upload is just a re-encode; no responsive sizes, no lazy loading generation.

These are choices, not bugs. Add them yourself if you need them.

---

## License

MIT — see [LICENSE](LICENSE).

---

## Credits

- [Statiq](https://www.statiq.dev/) — the static-site generator this editor wraps.
- ASP.NET Core / Razor Pages — the editor process.
- [Marked](https://marked.js.org/) — client-side markdown preview.
- The demo theme is a deliberately small, vanilla-CSS starter. Replace it with whatever you want.