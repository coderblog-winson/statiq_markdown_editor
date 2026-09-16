// =====================================================
//  CustomPipelines.cs
//
//  Shared custom Statiq modules + pipeline configuration,
//  parameterized by SiteConfig so multiple sites can reuse them.
//
//  Aggregates (and de-duplicates) the custom modules that used
//  to live in each legacy project's Program.cs:
//    - SetDestination: strip YYYY-MM month subdir from post URLs
//                      (only if SiteConfig.StripMonthFromPostUrls)
//    - Tags pipeline: GroupDocuments("Tags") + _TagLayout.cshtml
//    - Figure shortcode: <figure><img/><figcaption/></figure>
//    - TagAutoLinkModule: auto-link tag names to /tag/<slug>.html
//    - ExternalLinkTargetModule: target=_blank on off-site links
//    - RSS metadata, SEO metadata (SitemapPriority/ChangeFreq/LastModified)
//    - Draft filter: drop Draft=true markdown posts
//    - http→https rewrite (Statiq default emits http://)
//    - Custom Sitemap pipeline (Statiq default ignores our metadata,
//      so we wipe its modules and emit the XML ourselves)
//
//  SiteConfig-driven, no per-site hard-coding.
// =====================================================
using System.Text;
using System.Text.RegularExpressions;
using System.Net;
using Microsoft.Extensions.Logging;
using Statiq.App;
using Statiq.Common;
using Statiq.Core;
using Statiq.Razor;
using Statiq.Web;

namespace SME.Statiq;

public static class CustomPipelines
{
    /// <summary>
    /// Apply all shared custom pipelines to the Bootstrapper.
    /// Called by StatiqRunner.BuildAsync once per site.
    /// </summary>
    public static Bootstrapper ApplyAll(this Bootstrapper b, SiteConfig cfg, SitePaths paths)
    {
        return b
            .ConfigureSettings(s =>
            {
                // Pass our config to the engine as Statiq settings keys (so
                // Razor templates can read them via IExecutionContext.Settings).
                s["Theme"]          = cfg.Theme;
                s["Host"]           = cfg.Host;
                if (!string.IsNullOrEmpty(cfg.GoogleAnalyticsId))
                    s["GoogleAnalyticsId"] = cfg.GoogleAnalyticsId;
                if (!string.IsNullOrEmpty(cfg.AdSenseId))
                    s["GoogleAdSenseId"]   = cfg.AdSenseId;
                if (!string.IsNullOrEmpty(cfg.UmamiSiteId))
                    s["UmamiSiteId"] = cfg.UmamiSiteId;
                if (!string.IsNullOrEmpty(cfg.UmamiUrl))
                    s["UmamiUrl"] = cfg.UmamiUrl;
                s["UseExtensions"]      = cfg.UseHtmlExtensions;
                s["LinkHideExtensions"] = cfg.LinkHideExtensions;
                // Internal domain list for ExternalLinkTargetModule (one or two
                // variants of the same hostname).
                s["InternalDomains"] = string.Join(",",
                    new[] { cfg.Host, $"www.{cfg.Host}" }
                        .Where(h => !string.IsNullOrEmpty(h)));
            })
            .ConfigureFileSystem((fileSystem, settings) =>
            {
                // 1. The site's own input dir is the source of truth.
                fileSystem.InputPaths.Clear();
                fileSystem.InputPaths.Add(paths.InputDir);

                // 2. Theme dir is ALSO an input path — themes typically
                //    carry _Layout.cshtml, _Sidebar.cshtml, partials, etc.
                //
                //    Layout in config.json is RELATIVE TO SITE ROOT
                //    (sites/<name>/themes/ after the Sep-2026 refactor).
                //    We pass the absolute ThemeDir through unchanged.
                var themePathNormalized = new NormalizedPath(paths.ThemeDir);
                var themeInputPath = themePathNormalized.Combine("input");
                if (fileSystem.GetDirectory(themeInputPath).Exists)
                {
                    fileSystem.InputPaths.Add(themeInputPath);
                    System.Console.WriteLine($"[themes] using {themeInputPath}");
                }
                else
                {
                    fileSystem.InputPaths.Add(themePathNormalized);
                    System.Console.WriteLine($"[themes] using {themePathNormalized} (no input/ subdir)");
                }
                foreach (var p in fileSystem.InputPaths)
                    System.Console.WriteLine($"[themes]   InputPath: {p}");
            })
            .ConfigureEngine(engine =>
            {
                // ---- Custom SetDestination (post-process): ----
                var contentPipeline = engine.Pipelines["Content"];
                contentPipeline.PostProcessModules.Add(
                    new SetDestination(Config.FromDocument(doc =>
                    {
                        // 1. search-index.json: fix to root regardless of theme.
                        var src = doc.Source.IsNull ? "" : doc.Source.FileName.ToString();
                        if (src.Contains("search-index.json"))
                            return new NormalizedPath("search-index.json");

                        // 2. Post articles: optionally strip YYYY-MM month subdir.
                        var destStr = doc.Destination.ToString();
                        if (cfg.StripMonthFromPostUrls)
                        {
                            var monthMatch = Regex.Match(
                                destStr,
                                @"^posts/\d{4}-\d{2}/(.+\.html)$");
                            if (monthMatch.Success)
                                return new NormalizedPath($"posts/{monthMatch.Groups[1].Value}");
                        }
                        return doc.Destination;
                    }))
                );

                // ---- Tags pipeline: ----
                engine.Pipelines.Add("Tags", new Pipeline
                {
                    Dependencies = { "Content" },
                    ProcessModules =
                    {
                        new ReplaceDocuments("Content"),
                        new FilterDocuments(Config.FromDocument(doc => doc.ContainsKey("Tags"))),
                        new GroupDocuments("Tags"),
                        new MergeContent(new ReadFiles("_TagLayout.cshtml")),
                        new RenderRazor().WithModel(Config.FromDocument((doc, _) => doc)),
                        new SetDestination(Config.FromDocument(doc =>
                        {
                            var tagName = doc.GetString(Keys.GroupKey);
                            var slug = tagName.ToLower()
                                .Replace(" ", "-")
                                .Replace(".", "")
                                .Replace("/", "-");
                            return new NormalizedPath($"tag/{slug}.html");
                        })),
                        new WriteFiles()
                    }
                });

                // ---- Figure shortcode: ----
                engine.Shortcodes.Add("Figure",
                    (KeyValuePair<string, string>[] args,
                     string content, IDocument doc, IExecutionContext ctx) =>
                {
                    var src     = args.FirstOrDefault(x => x.Key == "src").Value;
                    var alt     = args.FirstOrDefault(x => x.Key == "alt").Value ?? "";
                    var caption = content;
                    string html = cfg.FigureStyle switch
                    {
                        "custom-figure" => $@"
                            <figure class=""custom-figure"">
                                <img src=""{src}"" alt=""{alt}"" />
                                <figcaption>{caption}</figcaption>
                            </figure>",
                        "inline-styled" => $@"
                            <figure style=""width: 90%; margin: 2rem auto; text-align: center;"">
                                <img src=""{src}"" alt=""{alt}"" style=""width: 100%; height: auto; border-radius: 5px;"" />
                                <figcaption style=""margin-top: 10px; color: #888; font-style: italic; font-size: 0.9em;"">
                                {caption}
                                </figcaption>
                            </figure>",
                        _ => $@"<figure><img src=""{src}"" alt=""{alt}"" /><figcaption>{caption}</figcaption></figure>"
                    };
                    return new ShortcodeResult(html);
                });
            })
            .ModifyPipeline(nameof(global::Statiq.Web.Pipelines.Content), pipeline =>
            {
                // --- Process modules (during content rendering) ---
                pipeline.ProcessModules.Add(new TagAutoLinkModule());
                pipeline.ProcessModules.Add(new ExternalLinkTargetModule());

                // RSS feed source: any markdown doc that's not a draft and has a Date.
                pipeline.ProcessModules.Add(new SetMetadata("Rss", Config.FromDocument(doc =>
                    !doc.GetBool("Draft", false) &&
                    doc.ContainsKey("Date") &&
                    doc.Source != null &&
                    doc.Source.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase))));

                // --- Sitemap metadata ---
                pipeline.ProcessModules.Add(new SetMetadata("IncludeInSitemap", Config.FromDocument(doc =>
                {
                    if (doc.GetBool("Draft", false)) return false;
                    var dest = doc.Destination.ToString();
                    if (string.IsNullOrEmpty(dest)) return false;
                    if (!dest.EndsWith(".html")) return false;
                    if (dest == "404.html" || dest.EndsWith("/404.html")) return false;
                    if (dest.Contains("search")) return false;
                    if (dest.StartsWith("page/")) return false;
                    return true;
                })));
                pipeline.ProcessModules.Add(new SetMetadata("SitemapPriority", Config.FromDocument(doc =>
                {
                    var dest = doc.Destination.ToString();
                    if (dest == "index.html" || dest.EndsWith("/index.html")) return "1.0";
                    if (dest.StartsWith("category/")) return "0.8";
                    if (dest.StartsWith("tag/")) return "0.6";
                    if (dest == "about.html" || dest == "archives.html") return "0.5";
                    return "0.9";
                })));
                pipeline.ProcessModules.Add(new SetMetadata("SitemapChangeFreq", Config.FromDocument(doc =>
                {
                    var dest = doc.Destination.ToString();
                    if (dest == "index.html" || dest.EndsWith("/index.html")) return "weekly";
                    if (dest.StartsWith("category/")) return "weekly";
                    if (dest.StartsWith("tag/")) return "weekly";
                    if (dest == "about.html" || dest == "archives.html") return "monthly";
                    return "monthly";
                })));
                pipeline.ProcessModules.Add(new SetMetadata("LastModified", Config.FromDocument(doc =>
                {
                    if (doc.ContainsKey("Date"))
                        return doc.Get<DateTime>("Date");
                    if (!doc.Source.IsNull && File.Exists(doc.Source.FullPath))
                        return File.GetLastWriteTime(doc.Source.FullPath);
                    return DateTime.UtcNow;
                })));

                // --- Draft filter: drop posts/...md with Draft=true ---
                pipeline.ProcessModules.Add(new FilterDocuments(Config.FromDocument(doc =>
                {
                    if (doc.Source.IsNull) return true;
                    if (!doc.Source.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase)) return true;
                    var destStr = doc.Destination.ToString();
                    var inPosts = destStr.StartsWith("posts/")
                                  || destStr.StartsWith("posts" + Path.DirectorySeparatorChar);
                    if (!inPosts) return true;
                    return !doc.GetBool("Draft", false);
                })));

                // --- PostProcess: http→https ---
                if (!string.IsNullOrEmpty(cfg.Host))
                {
                    pipeline.PostProcessModules.Add(new ReplaceInContent(
                        $"http://{cfg.Host}",
                        $"https://{cfg.Host}"));
                }
            })
            .ModifyPipeline("Sitemap", pipeline =>
            {
                // Statiq's default GenerateSitemap silently ignores our
                // LastModified / SitemapChangeFreq / SitemapPriority metadata.
                // Wipe its modules and emit the XML ourselves.
                pipeline.ProcessModules.Clear();
                pipeline.PostProcessModules.Clear();

                pipeline.ProcessModules.Add(new ReplaceDocuments("Content"));
                pipeline.ProcessModules.Add(new FilterDocuments(Config.FromDocument(doc =>
                    doc.GetBool("IncludeInSitemap", false))));

                pipeline.ProcessModules.Add(new ExecuteConfig(Config.FromContext(ctx =>
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                    sb.AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");
                    foreach (var d in ctx.Inputs)
                    {
                        var loc = (d.GetLink(true) ?? "").Replace("http://", "https://");
                        if (string.IsNullOrEmpty(loc)) continue;
                        string lastmod;
                        if (d.ContainsKey("Date"))
                            lastmod = d.Get<DateTime>("Date").ToString("yyyy-MM-dd");
                        else if (!d.Source.IsNull && File.Exists(d.Source.FullPath))
                            lastmod = File.GetLastWriteTime(d.Source.FullPath).ToString("yyyy-MM-dd");
                        else
                            lastmod = DateTime.UtcNow.ToString("yyyy-MM-dd");
                        var priority   = d.GetString("SitemapPriority")  ?? "0.5";
                        var changefreq = d.GetString("SitemapChangeFreq") ?? "monthly";
                        sb.AppendLine("  <url>");
                        sb.AppendLine($"    <loc>{WebUtility.HtmlEncode(loc)}</loc>");
                        sb.AppendLine($"    <lastmod>{lastmod}</lastmod>");
                        sb.AppendLine($"    <changefreq>{changefreq}</changefreq>");
                        sb.AppendLine($"    <priority>{priority}</priority>");
                        sb.AppendLine("  </url>");
                    }
                    sb.AppendLine("</urlset>");
                    return ctx.CreateDocument(new NormalizedPath("sitemap.xml"), sb.ToString()).Yield();
                })));

                pipeline.ProcessModules.Add(new SetDestination("sitemap.xml"));
                pipeline.ProcessModules.Add(new WriteFiles());
            });
    }
}