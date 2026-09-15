// =====================================================
//  SiteConfig.cs
//
//  Per-site configuration for the editor-managed Statiq sites
//  (loaded from sites/<name>/config.json). Captures everything
//  each site needs to know about itself — paths, host, analytics
//  IDs, and feature toggles — without hard-coding.
//
//  Lives next to the editor rather than per-site, so adding a
//  new site only requires dropping a config.json + themes dir;
//  no code change.
// =====================================================

namespace SME.Statiq;

public class SiteConfig
{
    /// <summary>Site slug — matches its directory name under sites/.</summary>
    public string Name { get; set; } = "";

    /// <summary>Theme path RELATIVE TO EDITOR ROOT. e.g. "themes/coderblog".</summary>
    public string Theme { get; set; } = "";

    /// <summary>Public hostname, no scheme. e.g. "coderblog.in".</summary>
    public string Host { get; set; } = "";

    /// <summary>
    /// If true, input/posts/YYYY-MM/foo.md is written to output/posts/foo.html
    /// (month subdir stripped from URL). CoderBlog/WinsonInvest use this for
    /// flat permalinks. Tableware keeps months in URLs.
    /// </summary>
    public bool StripMonthFromPostUrls { get; set; } = true;

    /// <summary>
    /// CSS class used on the rendered Figure shortcode's <figure> tag.
    /// "custom-figure" matches CoderBlog theme; "inline-styled" matches Tableware.
    /// </summary>
    public string FigureStyle { get; set; } = "custom-figure";

    /// <summary>Optional analytics/ads IDs — injected as Statiq settings keys.</summary>
    public string GoogleAnalyticsId { get; set; } = "";
    public string AdSenseId { get; set; } = "";
    public string UmamiSiteId { get; set; } = "";
    public string UmamiUrl { get; set; } = "";

    /// <summary>Use .html extensions in URLs (CoderBlog=true, Tableware=true).</summary>
    public bool UseHtmlExtensions { get; set; } = true;

    /// <summary>Append output to absolute URL. (Optional, default false.)</summary>
    public bool LinkHideExtensions { get; set; } = false;

    /// <summary>
    /// Path to the preview shell script, RELATIVE TO <c>sites/&lt;name&gt;/</c>.
    /// Runs <c>dotnet run</c> + <c>python3 -m http.server</c> at <see cref="PreviewPort"/>.
    /// Leave empty to disable the Preview button for this site.
    /// </summary>
    public string PreviewScript { get; set; } = "scripts/preview.sh";

    /// <summary>
    /// Path to the build+deploy shell script, RELATIVE TO <c>sites/&lt;name&gt;/</c>.
    /// Typically runs <c>dotnet run</c> + rsync to VPS + invokes git_sync.sh.
    /// Leave empty to disable the Deploy button.
    /// </summary>
    public string DeployScript { get; set; } = "scripts/build_deploy.sh";

    /// <summary>
    /// Path to the git-sync shell script, RELATIVE TO <c>sites/&lt;name&gt;/</c>.
    /// Standalone git add/commit/push — invoked by the Git Sync button.
    /// Leave empty to disable the Git Sync button.
    /// </summary>
    public string GitSyncScript { get; set; } = "scripts/git_sync.sh";

    /// <summary>
    /// Port the preview HTTP server listens on. <c>preview.sh</c> takes this
    /// as its first argument (or reads it from env). The Preview script is
    /// responsible for actually binding; we just tell it which port.
    /// </summary>
    public int PreviewPort { get; set; } = 5080;

    /// <summary>
    /// Compute the absolute paths under the editor root. Called by StatiqRunner
    /// before passing the config to Bootstrapper.
    /// </summary>
    public SitePaths ResolvePaths(string editorRoot)
    {
        return new SitePaths
        {
            EditorRoot = editorRoot,
            SiteDir = Path.Combine(editorRoot, "sites", Name),
            InputDir = Path.Combine(editorRoot, "sites", Name, "input"),
            OutputDir = Path.Combine(editorRoot, "sites", Name, "output"),
            CacheDir = Path.Combine(editorRoot, "sites", Name, "cache"),
            ThemeDir = Path.IsPathRooted(Theme)
                ? Theme
                : Path.Combine(editorRoot, Theme),
        };
    }
}

public class SitePaths
{
    public required string EditorRoot { get; init; }
    public required string SiteDir { get; init; }
    public required string InputDir { get; init; }
    public required string OutputDir { get; init; }
    public required string CacheDir { get; init; }
    public required string ThemeDir { get; init; }
}