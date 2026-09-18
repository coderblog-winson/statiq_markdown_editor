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
git clone https://github.com/coderblog-winson/statiq_markdown_editor.git
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

## Packaging as a desktop app

The editor ships as an **Electron shell wrapping the ASP.NET Core backend**. You can build a standalone `.app` (macOS) that bundles the .NET runtime + Razor Pages + the editor UI — users don't need .NET or Node installed.

### Prerequisites

- **.NET 9 SDK** (same one used for development)
- **Node 18+** and npm (only for the build host)
- Run `npm install` once to pull Electron 32 + electron-builder 25

### Build commands

```bash
# 1. Publish the backend as a single-file self-contained binary
npm run publish:server
# → dist/server/StatiqMarkdownEditor  (one executable, contains .NET runtime)

# 2a. Package for macOS (Apple Silicon, unpacked .app directory)
npm run package:mac

# 2b. Package for macOS as a DMG installer
npm run package:mac-dmg

# 3. Output
dist/mac/Statiq Markdown Editor.app
# or  dist/mac/Statiq Markdown Editor-1.0.0-arm64.dmg
```

The `.app` is fully self-contained: double-click launches the ASP.NET backend on `127.0.0.1:5070` and the Electron window opens the editor UI. Quitting the app tears both down.

### Running from source (dev loop)

```bash
npm start
# → publish:server + electron .
# Live editor: edit Razor pages, then Cmd-R in the Electron window
# to restart with the new code (or `dotnet build && npm start` again).
```

### Cross-platform targets

`package.json` ships with a **macOS arm64-only** target by default (the development host). To add Windows or Linux, extend the `build` block:

```jsonc
"build": {
  "appId": "com.winsonet.statiq-markdown-editor",
  "productName": "Statiq Markdown Editor",
  "mac": {
    "target": [{ "target": "dmg", "arch": ["arm64"] }],
    "hardenedRuntime": false, "gatekeeperAssess": false, "identity": null
  },
  "win": {
    "target": [{ "target": "nsis", "arch": ["x64"] }]
  },
  "linux": {
    "target": [{ "target": "AppImage", "arch": ["x64"] }]
  }
}
```

Then run with the matching electron-builder flag:

```bash
npx electron-builder --win --x64 --publish never                    # Windows NSIS installer
npx electron-builder --linux AppImage --x64 --publish never         # Linux AppImage
```

**Note**: the `publish:server` step is hard-coded to `osx-arm64` in `package.json` — change the `-r` RID (`win-x64`, `linux-x64`) before packaging for another target, or add per-platform scripts:

```jsonc
"publish:server:win":   "dotnet publish Editor.csproj -c Release -r win-x64   --self-contained -p:PublishSingleFile=true -o ./dist/server",
"publish:server:linux": "dotnet publish Editor.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o ./dist/server",
"package:win": "npm run publish:server:win && electron-builder --win --x64 --publish never"
```

### Code signing

The default config has `identity: null` / `hardenedRuntime: false` — the `.app` launches fine locally and over personal distribution, but **Gatekeeper will warn on first open** for un-signed binaries. For App Store / wide distribution, set up an Apple Developer ID and replace those two fields. Same applies to Windows (`win.certificateFile`) and Linux (signing AppImage with GPG).

---

## Authentication (optional)

The editor ships as a **local tool** — by default it binds to `127.0.0.1`
only and skips login entirely. If you want to expose it on a LAN or the
public Internet (so you can edit from your phone, share with a collaborator,
etc.), enable the built-in password gate.

### Step 1 — create the password file

```bash
dotnet run -- --init-auth
# → "New password:" → type a password → "Confirm password:" → type it again
# → wrote Auth/auth.json (mode 600)
```

This writes a PBKDF2-SHA256 hash to `Auth/auth.json` (mode 600 — owner-read
only). The file is **gitignored** and lives at the editor root, *outside*
`wwwroot/`, so the web server cannot serve it directly.

### Step 2 — turn auth on

Edit `appsettings.json` (or `appsettings.Development.json`):

```json
{
  "Auth": {
    "Enabled": true,
    "HmacKey": ""
  }
}
```

Restart the editor. Visiting any page (except `/Login`) will redirect you
to the login form; posting the correct password returns a session cookie
(`HttpOnly` + `SameSite=Strict` + `Secure` on HTTPS) and redirects to
where you were heading.

### Step 3 — bind it wherever (LAN / public Internet)

```bash
dotnet run --urls http://0.0.0.0:5070   # any host on your LAN can reach it
ASPNETCORE_URLS=http://0.0.0.0:5070 dotnet run
```

### Footgun protection

The editor refuses to start if you bind to a non-loopback address
(`0.0.0.0`, a LAN IP, a public hostname) **without** `Auth.Enabled=true`:

```
FATAL: Server is bound to a public address but Auth.Enabled is false.
       Refusing to start without authentication.

  Detected binding:
    http://0.0.0.0:5070

  Fix one of:
    - Set --urls to 127.0.0.1:5070 (local-only), OR
    - Set Auth.Enabled=true in appsettings.json AND create Auth/auth.json
      via `dotnet run --init-auth`.
```

So you can't accidentally publish the editor to the network without a
password in place.

### Rotating the password

**While signed in**: visit `/ChangePassword` (or click the 🔐 user menu
in the header → "🔑 Change password"). Fill in current + new password +
confirmation. The endpoint verifies the current password, derives a
fresh PBKDF2 hash, atomically rewrites `Auth/auth.json` (write to
`.tmp` + rename — never half-written), and clears the in-memory HMAC key.
Every existing session becomes invalid immediately; you'll be bounced
back to `/Login` to sign in with the new password.

**Without being signed in** (e.g. lost the password): re-run
`dotnet run -- --init-auth`. It will overwrite `Auth/auth.json` (keeping
the same path). All existing session cookies become invalid on the next
request because the HMAC key is derived from the password hash.

Either way, the new password must be at least 8 characters and must
differ from the current one.

### Docker / shared-secret deployments

Override the auth.json path with `STATIQ_EDITOR_AUTH_FILE=/run/secrets/auth.json`
(env var), and override the HMAC key with `Auth.HmacKey` in
`appsettings.json`. Useful when auth.json lives in a Docker secret and
you want to rotate the HMAC key independently.

### Threat model this covers

- ✅ Anyone who can reach the editor port must present a password to do
  anything (read, write, delete posts; trigger builds; deploy).
- ✅ Cross-site form submissions are blocked by CSRF token check.
- ✅ Sessions are stateless HMAC-signed tokens — no DB, no memory table,
  but the cookie is `HttpOnly` so XSS can't steal it.
- ⚠️ **Not covered**: rate limiting (someone can spam the login endpoint).
  Run behind a reverse proxy (nginx, Caddy) if that worries you.
- ⚠️ **Not covered**: multi-user / per-site permissions. Everyone with
  the password can edit every site. Add an OAuth layer if you need that.
- ⚠️ **Not covered**: HTTPS — the editor speaks plain HTTP on whatever
  port you bind it to. Use a reverse proxy for TLS termination in
  production.

If you outgrow any of those, swap the implementation — the middleware
contract is just "redirect 302 to /Login or 401 JSON for /api/*"; you can
swap `AuthService` for ASP.NET Core Identity or an OAuth handler without
touching the rest of the editor.

---

## Limitations (by design)

- **No multi-user.** The editor binds to `127.0.0.1` by default, but you
  can opt into a single-password gate (see *Authentication* below). Anyone
  with the password can edit every site.
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