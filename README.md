# Statiq Markdown Editor

A web-based editor for [Statiq](https://www.statiq.dev/) markdown posts. Built so
you can edit your posts in the browser and let [Grammarly](https://www.grammarly.com/)
check your English while you write.

Single-user local tool — no auth, no cloud, no telemetry. Reads and writes files
directly from the Statiq project on disk.

## Why

VS Code is great for markdown, but Grammarly lives in the browser. A web editor
sits in the same place as Grammarly's content script, so suggestions just show up
next to whatever you type. Monaco editor (the same engine VS Code uses) is
contenteditable under the hood, which Grammarly already targets.

## What it does

- **List view** — every `.md` under the project's `input/posts` (including date
  subfolders like `input/posts/202609/`), with category filter and search by
  title or filename.
- **Edit view** — full Monaco editor with markdown syntax highlighting, line
  numbers, word wrap at column 100, live word/char/line count.
- **Paste from the web** — paste any HTML (e.g. a Medium article) and the editor
  converts it to clean markdown via [Turndown](https://github.com/mixmark-io/turndown).
  The first line of the URL prefix (e.g. `https://medium.com/...`) is the
  trigger; tiny snippets still go through as plain text. Headings, lists,
  fenced code, links, images all converted.
- **Paste images** — paste an image from the clipboard (screenshot, copied
  image, etc.) and the editor uploads it to the project, converts to WebP
  (quality 85, same as the existing og:image convention), saves it under
  `input/images/{YYYY-MM}/`, and inserts the `![alt](/images/...)` markdown
  in place. The target month comes from the post's frontmatter `Date` if set,
  otherwise "now". You can override per-upload.
- **New post** — full-page form for the 7 frontmatter fields (Title, Slug, Date,
  Layout, Image, Category, Tags, Description) with a live YAML preview alongside.
  Creates a clean Statiq-style frontmatter block in the canonical order.
- **Settings** — point the editor at any Statiq project on disk. Change Root /
  ContentSubdir / ImagesSubdir through a form; saved to `appsettings.json` and
  takes effect immediately (no restart needed).
- **Rename** — change the file slug without touching frontmatter.
- **Delete** — with confirmation.
- **Cmd/Ctrl+S** to save; browser warns on close with unsaved changes.

The frontmatter is preserved byte-for-byte on save. Statiq's
`<?# Figure ... ?>` shortcodes pass through untouched.

## Requirements

- macOS / Linux / Windows
- .NET SDK 9.0 (the project targets `net9.0`; tested on 9.0.200 — same SDK
  pinned by the Statiq project itself)

Check:
```bash
dotnet --list-sdks
```

## Run

From this directory:
```bash
dotnet run
```

Then open <http://localhost:5070/>.

The default port is 5070 (see `Properties/launchSettings.json`). The first run
opens the browser automatically.

## Configure

Open <http://localhost:5070/Settings> in the browser and edit the form.
The values are written to `appsettings.json` (preserving the Logging and
AllowedHosts sections) and the running app reloads them on the next request —
no restart needed.

You can also edit `appsettings.json` by hand; the next request picks it up:

```json
{
  "StatiqProject": {
    "Root": "/path/to/your.statiq",
    "ContentSubdir": "input/posts",
    "ImagesSubdir": "input/images"
  }
}
```

## Grammarly setup

1. Install the [Grammarly browser extension](https://www.grammarly.com/browser)
   in Chrome / Edge / Firefox / Arc.
2. Open the editor at <http://localhost:5070/editor?path=your-post.md>.
3. Start typing. Grammarly's underlines and suggestions should appear inline.

If Grammarly is blocked on `localhost`, click the extension icon in the toolbar
and toggle "Check grammar and spelling" on for the current site.

## Project structure

```
statiq_markdown_editor/
├── Editor.csproj              # net9.0 web app, depends on YamlDotNet + ImageSharp
├── Program.cs                 # minimal API + Razor Pages wiring
├── appsettings.json           # StatiqProject:Root / ContentSubdir / ImagesSubdir
├── Properties/launchSettings.json
├── Models/Models.cs           # PostSummary, PostContent, FrontmatterData, NewPostRequest, SettingsUpdateRequest, ImageUploadResponse
├── Services/
│   ├── FrontmatterService.cs  # YAML split + re-emit with stable key order
│   ├── MarkdownFileService.cs # list / read / write / delete / create / rename
│   ├── ImageService.cs        # paste upload → WebP → input/images/YYYY-MM/
│   └── SettingsService.cs     # read/write appsettings.json + trigger reload
├── Pages/
│   ├── _ViewImports.cshtml
│   ├── _ViewStart.cshtml
│   ├── Index.cshtml + .cs     # list page (open / rename / delete)
│   ├── Editor.cshtml + .cs    # editor page (Monaco + Grammarly + paste)
│   ├── New.cshtml + .cs       # new post form (7 frontmatter fields)
│   ├── Settings.cshtml + .cs  # settings form (Statiq project path)
│   ├── Error.cshtml + .cs
│   └── Shared/_Layout.cshtml
└── wwwroot/
    ├── css/site.css           # base + tokens
    ├── css/list.css
    ├── css/editor.css
    ├── css/new.css            # new post form + YAML preview
    ├── css/settings.css
    ├── js/list.js
    ├── js/editor.js           # Monaco + paste (image → /api/images, HTML → Turndown)
    ├── js/new.js              # new post form + live preview
    └── js/settings.js         # settings load + save
```

## Image upload rules

- **Input formats**: anything ImageSharp can decode (PNG, JPEG, GIF, BMP, WebP,
  TIFF, TGA, PBM). The decoder is auto-detected from bytes.
- **Output**: WebP quality 85 (matches the existing `svg2webp.py` convention).
- **Filename**: `{slug}-{HHmmss}.webp`, with `-2`, `-3` suffixes if the slot is
  taken. The slug is derived from the original filename when available.
- **Destination**: `{Root}/{ImagesSubdir}/{YYYY-MM}/`. The YYYY-MM comes from
  the post's frontmatter `Date` when known, otherwise the upload time. You can
  override per upload.
- **URL written to markdown**: `/{ImagesSubdir without "input/"}/{YYYY-MM}/file.webp`
  — matches the convention used in the coderblog frontmatter `Image:` field.
- **Max size**: 25 MB per upload (hard cap; the request is rejected before any
  decode work is done).

## API

All endpoints under `/api/`:

| Method | Path                              | Notes                                |
| ------ | --------------------------------- | ------------------------------------ |
| GET    | `/api/posts`                      | `?q=` search, `?category=` filter    |
| GET    | `/api/posts/{*path}`              | returns frontmatter + body + rawText |
| PUT    | `/api/posts/{*path}`              | body `{relativePath, rawText}`       |
| DELETE | `/api/posts/{*path}`              |                                      |
| POST   | `/api/posts`                      | body `NewPostRequest` (7 fields)     |
| POST   | `/api/posts/rename?path=…`        | body `{newSlug}`                     |
| GET    | `/api/categories`                 |                                      |
| PUT    | `/api/images`                     | multipart: `file`, `postPath?`, `targetDate?` |
| GET    | `/api/settings`                   | resolved paths + existence flag      |
| PUT    | `/api/settings`                   | body `SettingsUpdateRequest`         |

## Safety

- All file paths are validated against the content root — `..` and absolute
  paths return 404.
- The save path writes `rawText` verbatim, so the frontmatter you see in the
  editor is exactly what ends up on disk.
- The `Create` path uses a stable field order (Title, Description, Date, Layout,
  Image, Category, Tags) so diffs against older posts stay clean.

## Known limitations (v1)

- No image browser — you see only images you've pasted; no thumbnails of the
  existing `input/images/` tree.
- No live markdown preview pane (Monaco highlights syntax; render in VS Code or
  run the Statiq preview script when you want a visual check).
- No concurrent-write protection (single-user local tool).
- Filename rename only — does not touch the frontmatter `Title` or URL slug.
- HTML→markdown conversion uses Turndown defaults. Complex Medium-specific
  embeds (tweets, code blocks with custom containers) may not convert perfectly.
