---
Title: "Theme development: the templates, the inputs, the build"
Description: "How a Statiq theme actually fits into the build pipeline. What you need to know to write your own."
Date: 2026-09-18
Author: Statiq Markdown Editor
Layout: _PostLayout
Category: Tutorial
Tags: [theme, razor, statiq, tutorial, templates]
---

# Theme development

A Statiq theme is just a folder of `.cshtml` files that the build pipeline
loads as InputPaths and resolves against `IDocument` instances. No magic,
no plugin API — Razor templates calling into the `IDocument` interface.

## What the build pipeline does

When you click **Build**, the editor runs a pipeline (configured in
`Statiq/CustomPipelines.cs`) that does roughly:

1. **Load files** from `sites/<name>/input/` (markdown posts + images)
2. **Load themes** from `sites/<name>/themes/input/` (Razor templates + assets)
3. **Parse frontmatter** — YAML block at top of each `.md` file becomes the
   `IDocument`'s metadata
4. **Render each document** through its declared `Layout:` template
6. **Run shortcodes** — `<?# Figure src="..." alt="..." ?>...<?#/ Figure ?>`
   expands to a styled `<figure>` tag
5. **Post-process** — strip month from post URLs (if `StripMonthFromPostUrls`
   is true), http → https rewrite, etc.
8. **Run derived** — Tags, RSS feed, sitemap, 404
9. **Write** everything to `sites/<name>/output/`

The whole thing runs in-process — no `dotnet run` subprocess, no temp
working dir. Hence why a 30-post site builds in ~10 seconds.

## The themes directory

A theme is `sites/<name>/themes/` (after the Sep-2026 refactor; previously
`themes/<name>/` at the editor root). The shape is:

```
sites/<name>/themes/input/
├── _Layout.cshtml                # master template
├── _PostLayout.cshtml            # Layout: _PostLayout
├── _ListLayout.cshtml            # Layout: _ListLayout
├── _TagLayout.cshtml             # one tag page (auto-generated)
├── _SitemapTemplate.cshtml       # sitemap.xml
├── _ViewImports.cshtml            # @using directives, @inherits
├── index.cshtml                   # home page
├── archives.cshtml                # all posts grouped by month
├── tags.cshtml                    # all tags with post counts
├── about.cshtml                   # static about page
├── feed.cshtml                    # RSS feed
├── 404.cshtml                     # error page
├── robots.txt                     # SEO
└── assets/
    ├── css/site.css               # main stylesheet
    ├── favicon.svg                # favicon
    └── images/og-default.webp    # OG image fallback
```

Look at `themes/_demo/input/` in this repo — every file above is there,
commented, runnable.

## The IDocument interface

Every template `@inherits StatiqRazorPage<IDocument>`. That gives you:

```csharp
Document.GetString("Title")      // returns "" if missing
Document.Get<DateTime>("Date")    // throws if missing — check .ContainsKey() first
Document.GetList<string>("Tags")  // returns empty list if missing
Document.GetLink()                // the canonical URL path
Document.Destination               // NormalizedPath to where it'll be written
Document.Source                    // NormalizedPath to the source .md file
Document.ContainsKey("Field")     // safe check before reading
```

Plus there's the global `Context` which gives you access to **all** input
documents (useful for list pages):

```csharp
foreach (var doc in Documents) {
    // every other post on the site
}
```

And Statiq engine settings:

```csharp
var siteTitle = Context.Settings.GetString("SiteTitle");  // defined per-site in Settings
var umamiId = Context.Settings.GetString("UmamiSiteId");  // injected from sites/config.json
```

## A minimal theme: just `_Layout.cshtml`

You can run a theme with **only one file** — a `_Layout.cshtml` that
inherits from `null` (or itself) and emits the full HTML:

```cshtml
@{
    Layout = null;
    var title = Document.GetString("Title") ?? "My site";
}
<!DOCTYPE html>
<html>
<head><title>@title</title></head>
<body>
    <h1>@title</h1>
    <div>@RenderBody()</div>
</body>
</html>
```

Posts declare `Layout: _Layout` in their frontmatter, and each post's
markdown body is rendered into `@RenderBody()`. Done.

The demo theme goes a step further and uses parent/child layouts
(`_Layout` is the shell, `_PostLayout` and `_ListLayout` are child layouts
that declare `Layout = "_Layout.cshtml"` at the top). This is the Razor
Pages pattern — familiar if you've ever used ASP.NET Core MVC views.

## Settings access

Per-site config goes into `sites/<name>/config.json`. The editor's
`CustomPipelines.cs` reads it via `Bootstrapper.ApplyAll(s, cfg, paths)`
and injects every field as a Statiq setting:

| Config field | Statiq setting key |
|---|---|
| `Theme` | `Theme` |
| `Host` | `Host` |
| `GoogleAnalyticsId` | `GoogleAnalyticsId` |
| `AdSenseId` | `GoogleAdSenseId` |
| `UmamiSiteId` | `UmamiSiteId` |
| `UmamiUrl` | `UmamiUrl` |
| `UseHtmlExtensions` | `UseExtensions` |
| `LinkHideExtensions` | `LinkHideExtensions` |
| (derived) `Host + www.Host` | `InternalDomains` |

In your theme:

```cshtml
var gaId = Context.Settings.GetString("GoogleAnalyticsId");
@if (!string.IsNullOrEmpty(gaId)) {
    <script async src="https://www.googletagmanager.com/gtag/js?id=@gaId"></script>
}
```

The `_demo` theme shows this pattern for analytics.

## What's _not_ obvious

A few things I learned the hard way:

- **Don't put `const int` in `@{ }` blocks.** Statiq 1.0-beta.60's Razor
  compiler treats the generated class as top-level statements, and `const`
  declarations break that. Use `var`.
- **`StripMonthFromPostUrls: true`** turns `posts/2026-09/foo.md` into
  `posts/foo.html`. If your theme hardcodes `posts/2026-09/foo.html` in
  any link, those will 404 after the build.
- **Layout frontmatter field omits `.cshtml`** — `Layout: _PostLayout`
  works because Statiq appends `.cshtml` for you. But `Layout: _PostLayout.cshtml`
  also works — both are accepted.
- **Draft posts are still rendered** unless `Draft: true` is in their
  frontmatter. The editor's `Draft filter` (in `CustomPipelines.cs`)
  drops them before the post-process step.

That's the whole theme story. Open `themes/_demo/input/_Layout.cshtml`,
change the `<h1>` to your own, click **Build**, see what happens.