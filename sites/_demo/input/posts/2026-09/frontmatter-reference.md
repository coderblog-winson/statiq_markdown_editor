---
Title: "Frontmatter reference: every field the editor recognises"
Description: "Title, Description, Date, Layout, Image, Category, Draft, Tags. Plus how the editor canonicalises them on save."
Date: 2026-09-17
Author: Statiq Markdown Editor
Layout: _PostLayout
Category: Documentation
Image: "/images/2026-09/frontmatter-hero.webp"
Tags: [frontmatter, yaml, reference, documentation]
---

# Frontmatter reference

A Statiq post is a markdown file with a YAML frontmatter block delimited by
`---` at the top. The editor understands this schema:

```yaml
---
Title: "The title as you want it to appear in the browser tab"
Description: "A 1–2 sentence summary used in the meta description tag and the post list cards."
Date: 2026-09-17                  # ISO date — used for sort, archives, RSS pubDate
Author: Your Name                  # optional — theme can read this if it wants
Layout: _PostLayout                # which template to render the post body with
Image: "/images/2026-09/hero.webp" # og:image, also shown above the post body
Category: Documentation             # free-text — used by archives + theme metadata
Tags: [docs, markdown, yaml]        # free-text array — drives /tag/<slug>.html pages
Draft: false                       # when true, Statiq skips the post during build
---
```

## What each field is for

### `Title`

Required (sort of — the editor falls back to the filename). Shown in:

- `<title>` (browser tab)
- `<h1>` on the post page
- Post list cards
- Open Graph tags (`og:title`)
- RSS `<title>`

Quotes are optional but recommended for any title with a colon in it.

### `Description`

1–2 sentence summary. Used in:

- `<meta name="description">`
- `<meta property="og:description">`
- The post list card excerpt
- RSS `<description>`

If you leave it blank, the editor writes `Description: ""` so the field is
always present.

### `Date`

ISO 8601 date (`YYYY-MM-DD`). Used for:

- Sorting posts (newest first)
- `<time datetime="...">` semantic markup
- RSS `<pubDate>`
- Sitemap `<lastmod>`

The editor uses this date to decide which `images/YYYY-MM/` folder to put
uploaded images into.

### `Layout`

Which `.cshtml` template under `themes/<name>/input/` renders this post.
Default is `_PostLayout`. Use:

- `_PostLayout` — normal post
- `_ListLayout` — a static page that looks like a list (e.g. about)
- `_Layout` — directly renders `_Layout.cshtml` (skip _PostLayout)

If the value doesn't exist, Statiq's Razor engine will throw
`The layout view '<name>' could not be located`.

### `Image`

Path to the post's hero image, displayed above the body and used as the
`og:image` for link previews. Should start with `/` and live under
`images/`. When you paste an image into the editor, it gets uploaded to
`input/images/YYYY-MM/<slug>-<HHmmss>.webp` and this field is filled in
automatically.

### `Category`

Free-text category. Used for:

- The post list filter dropdown
- Post card meta line (e.g. `DOCUMENTATION · SEP 17, 2026`)
- Per-category archives if your theme wants them

Doesn't drive its own URL — it's just metadata. If you want a per-category
page, you add a `category/<slug>.cshtml` template.

### `Tags`

Array of free-text tags. Each one gets its own page at `/tag/<slug>.html`
listing every post with that tag. Slug = lowercase + dashes.

```yaml
Tags: [statiq, markdown, dotnet]
```

The demo theme renders tags at the bottom of each post:

> #statiq  #markdown  #dotnet

### `Draft`

Defaults to `false`. When `true`, Statiq's draft filter (in the editor's
custom pipeline) drops the post before rendering. Useful for posts you want
to commit but not ship.

## Canonical key order

The editor re-emits frontmatter in this fixed order on every save:

```
Title, Description, Date, Layout, Image, Category, Draft, Tags
```

Unknown keys are preserved at the end of the block in their original order.
This keeps diffs clean — even when you reorder fields in the editor, the
result is always byte-stable.

## What's NOT in frontmatter

A few things you might expect are not frontmatter, they're **theme
configuration**. They live in `sites/<name>/config.json`:

- Theme path (`"Theme": "themes/blog"`)
- Host name (`"Host": "blog.example.com"`)
- Analytics IDs (`"GoogleAnalyticsId": "G-XXXXXXX"`, `"UmamiSiteId": "..."`)
- Site URL for RSS / sitemap (`"Host"` plus the protocol, e.g. `https://blog.example.com`)
- Preview port (`"PreviewPort": 5081`)

This split lets you change site-wide settings without touching every post,
and keeps sensitive keys (analytics IDs, deployment secrets) out of git.