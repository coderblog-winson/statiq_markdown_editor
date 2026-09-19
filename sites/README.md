# `sites/` — one directory per site

The editor's in-process Statiq.Web engine reads each site's content from here.
Every subdirectory is a **complete site** (its own `input/posts/`, `input/images/`,
`output/`, `cache/`, and `config.json`).

## Directory layout

```
sites/
├── README.md            ← this file
├── _demo/               ← example site, used to verify build/preview end-to-end
│   ├── config.json      ← site configuration (see below)
│   ├── input/           ← all source content for this site
│   │   ├── posts/       ← markdown posts (can be grouped into YYYY-MM/ subdirs)
│   │   ├── images/      ← images (can be grouped into YYYY-MM/ subdirs)
│   │   ├── about.md     ← top-level pages
│   │   └── ads.txt      ← top-level static files pass through verbatim
│   ├── output/          ← Statiq build output (auto-written, safe to .gitignore)
│   └── cache/           ← Statiq cache (auto-written, safe to .gitignore)
├── coderblog/
│   ├── config.json
│   ├── input/
│   └── ...
└── winsoninvest/
    ├── config.json
    ├── input/
    └── ...
```

## `config.json` fields

| Field | Required | Description | Example |
|---|---|---|---|
| `Theme` | ✅ | Theme path, relative to editor root (points at a subdirectory under `themes/`) | `"themes/coderblog"` |
| `Host` | ✅ | Public hostname (no scheme) — used for absolute URLs in sitemap/feed | `"coderblog.in"` |
| `StripMonthFromPostUrls` |  | Whether to strip the `YYYY-MM/` month prefix from post URLs (keeps them flat) | `true` |
| `FigureStyle` |  | CSS style for the `<?# Figure ?>` shortcode: `custom-figure` (class) or `inline-styled` | `"custom-figure"` |
| `GoogleAnalyticsId` |  | GA measurement ID | `"G-XXX"` |
| `AdSenseId` |  | AdSense publisher ID | `"ca-pub-XXX"` |
| `UmamiSiteId` / `UmamiUrl` |  | Umami analytics | |
| `UseHtmlExtensions` |  | Whether URLs carry `.html` extensions | `true` |
| `LinkHideExtensions` |  | Hide `.html` on internal links | `false` |

## Migrating from an existing Statiq project

Move an existing `Coderblog.in/coderblog.statiq/` into `sites/coderblog/`:

| Source | Destination |
|---|---|
| `Coderblog.in/coderblog.statiq/input/posts/` | `sites/coderblog/input/posts/` |
| `Coderblog.in/coderblog.statiq/input/images/` | `sites/coderblog/input/images/` |
| `Coderblog.in/coderblog.statiq/themes/coderblog/` | `themes/coderblog/` (shared) |
| `Coderblog.in/coderblog.statiq/appsettings.json` | `sites/coderblog/config.json` (renamed fields) |

Field mapping (`appsettings.json` → `config.json`):

```jsonc
// appsettings.json
{
  "Theme": "themes/coderblog",   // → same name in config.json
  "Host": "coderblog.in",        // → same name in config.json
  "GoogleAnalyticsId": "G-...",  // → same name in config.json
  "GoogleAdSenseId": "",         // → same name (AdSenseId) in config.json
  "UseExtensions": true,         // → same name (UseHtmlExtensions) in config.json
  "LinkHideExtensions": false    // → same name
}
```

**`StripMonthFromPostUrls` and `FigureStyle` are new fields.** Fill them in
based on each site's original setup:

- CoderBlog / WinsonInvest: `StripMonthFromPostUrls=true`, `FigureStyle="custom-figure"`
- Tableware: `StripMonthFromPostUrls=false` (keep month subdirectories), `FigureStyle="inline-styled"`

## Status

- ✅ Directory skeleton in place
- ✅ `config.json` schema settled
- ⏳ Three existing sites not yet migrated (waiting on manual migration + smoke test)