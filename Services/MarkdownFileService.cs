using System.Text;
using System.Text.RegularExpressions;
using StatiqMarkdownEditor.Models;

namespace StatiqMarkdownEditor.Services;

/// <summary>
/// All file IO for the editor. Scoped to the currently-active site
/// resolved via <see cref="StatiqRunner.GetActiveSitePathsSync"/>.
///
/// Convention (post Sep-2026 refactor):
///   posts   live under <c>sites/&lt;name&gt;/input/posts/</c>
///   images  live under <c>sites/&lt;name&gt;/input/images/</c>
///
/// Earlier versions used <c>StatiqProjectOptions.Active.Root</c> +
/// ContentSubdir / ImagesSubdir / Watermark fields — those moved to
/// per-site <c>config.json</c>.
/// </summary>
public class MarkdownFileService
{
    private readonly StatiqRunner _runner;
    private readonly FrontmatterService _fm;
    private readonly ImageService _imgSvc;
    private readonly ILogger<MarkdownFileService> _log;

    public MarkdownFileService(
        StatiqRunner runner,
        FrontmatterService fm,
        ImageService imgSvc,
        ILogger<MarkdownFileService> log)
    {
        _runner = runner;
        _fm = fm;
        _imgSvc = imgSvc;
        _log = log;
    }

    /// <summary>Absolute path to <c>sites/&lt;active&gt;/input/posts/</c>. Empty if no active site.</summary>
    public string ContentRoot
    {
        get
        {
            var paths = _runner.GetActiveSitePathsSync();
            if (paths == null) return string.Empty;
            return Path.Combine(paths.InputDir, "posts");
        }
    }

    /// <summary>Absolute path to <c>sites/&lt;active&gt;/input/images/</c>. Empty if no active site.</summary>
    public string ImagesRoot
    {
        get
        {
            var paths = _runner.GetActiveSitePathsSync();
            if (paths == null) return string.Empty;
            return Path.Combine(paths.InputDir, "images");
        }
    }

    private bool HasActiveSite(out string root)
    {
        root = ContentRoot;
        return !string.IsNullOrEmpty(root) && Directory.Exists(root);
    }

    // -------- list --------

    public List<PostSummary> ListPosts()
    {
        if (!HasActiveSite(out var contentRoot))
            return new List<PostSummary>();

        var list = new List<PostSummary>();
        foreach (var path in Directory.EnumerateFiles(contentRoot, "*.md", SearchOption.AllDirectories))
        {
            try
            {
                list.Add(BuildSummary(path));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to read {Path}", path);
            }
        }
        return list
            .OrderByDescending(p => p.Date ?? DateTime.MinValue)
            .ThenByDescending(p => p.RelativePath)
            .ToList();
    }

    public List<string> ListCategories()
    {
        return ListPosts()
            .Where(p => !string.IsNullOrWhiteSpace(p.Category))
            .Select(p => p.Category!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Distinct top-level subdirectories under <c>input/posts/</c> that
    /// represent topic categories (e.g. <c>financial_information</c>,
    /// <c>stock_investment</c>). Excludes the per-month <c>YYYY-MM</c> /
    /// <c>YYYYMM</c> directories — those are date buckets, not topics.
    ///
    /// Used by the New Post form's CategoryFolder dropdown and (optionally)
    /// by sidebar templates that want to auto-list topic folders.
    /// </summary>
    public List<string> ListCategoryFolders()
    {
        if (!HasActiveSite(out var contentRoot))
            return new List<string>();
        if (!Directory.Exists(contentRoot))
            return new List<string>();

        var monthLike = new Regex(@"^\d{4}-?\d{2}$", RegexOptions.Compiled);
        return Directory.EnumerateDirectories(contentRoot)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n) && !monthLike.IsMatch(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // -------- read --------

    public (bool ok, PostContent? post, string? error) ReadPost(string relativePath)
    {
        var (fullOk, full, err) = ResolveSafePath(relativePath);
        if (!fullOk) return (false, null, err);

        if (!File.Exists(full))
            return (false, null, $"file not found: {relativePath}");

        try
        {
            var raw = File.ReadAllText(full, Encoding.UTF8);
            var (fm, body, _) = _fm.Split(raw);
            return (true, new PostContent
            {
                RelativePath = ToRelative(full),
                Frontmatter = fm,
                Content = body,
                RawText = raw,
            }, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Read failed: {Path}", full);
            return (false, null, ex.Message);
        }
    }

    // -------- write --------

    public (bool ok, string? error) WritePost(string relativePath, PostContent body)
    {
        if (body is null) return (false, "body required");
        var (fullOk, full, err) = ResolveSafePath(relativePath);
        if (!fullOk) return (false, err);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            // Two write modes:
            //   1) raw — client sends RawText, we write it verbatim. Use this
            //      for the "edit full file in Monaco" flow so frontmatter
            //      round-trips byte-for-byte.
            //   2) composed — caller provides Frontmatter + Content; we
            //      re-emit with stable key order. Use this for the "create
            //      new post" flow.
            string text;
            if (!string.IsNullOrEmpty(body.RawText))
            {
                text = body.RawText;
            }
            else
            {
                text = _fm.Compose(body.Frontmatter, body.Content ?? string.Empty);
            }
            File.WriteAllText(full, text, new UTF8Encoding(false));
            return (true, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Write failed: {Path}", full);
            return (false, ex.Message);
        }
    }

    // -------- delete --------

    public (bool ok, string? error, int imagesDeleted) DeletePost(string relativePath, bool deleteImages = false)
    {
        var (fullOk, full, err) = ResolveSafePath(relativePath);
        if (!fullOk) return (false, err, 0);
        if (!File.Exists(full)) return (false, $"file not found: {relativePath}", 0);

        // Read the body BEFORE we delete the file — we need it to find any
        // referenced images we may also want to clean up.
        string body;
        try { body = File.ReadAllText(full); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read post body before delete: {Path}", full);
            body = string.Empty;
        }

        int imagesDeleted = 0;
        if (deleteImages)
        {
            // Cascade delete: scan the body for any local image references and
            // try to remove the files. Failures (file already gone, symlink, etc.)
            // are logged but never block the post delete — the cascade is a
            // convenience, not a transactional rollback.
            foreach (var url in ExtractLocalImageUrls(body))
            {
                var (imgOk, imgErr) = _imgSvc.DeleteImageByUrl(url);
                if (imgOk) imagesDeleted++;
                else _log.LogWarning("Could not cascade-delete image {Url}: {Err}", url, imgErr);
            }
        }

        try
        {
            File.Delete(full);
            return (true, null, imagesDeleted);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Delete failed: {Path}", full);
            return (false, ex.Message, 0);
        }
    }

    // -------- create --------

    public (bool ok, string? relativePath, string? error) CreatePost(NewPostRequest req)
    {
        if (!HasActiveSite(out var contentRoot))
            return (false, null, $"content root missing: {contentRoot}");

        var title = (req.Title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title))
            return (false, null, "title required");

        // Server-side guard: if the title contains CJK characters, the
        // auto-derived slug would itself be CJK, which produces file names and
        // URLs Statiq handles poorly. Force the caller to supply an ASCII slug.
        var titleHasCjk = ContainsCjk(title);
        var hasExplicitSlug = !string.IsNullOrWhiteSpace(req.Slug);
        if (titleHasCjk && !hasExplicitSlug)
        {
            return (false, null,
                "title contains CJK characters — please provide an ASCII slug (lowercase letters, digits, dashes).");
        }

        var date = req.Date ?? DateTime.Today;
        // The auto-derived slug is just the title — no year suffix. Year was
        // historically appended to disambiguate same-title posts, but it made
        // every URL look like a date-stamped blog, and the per-month folder
        // already groups posts by date. Collisions are still resolved by the
        // -2 / -3 fallback further down.
        var slug = hasExplicitSlug
            ? Slugify(req.Slug!)
            : Slugify(title);
        if (string.IsNullOrWhiteSpace(slug))
            return (false, null, "could not derive a slug from title");

        // Resolve the target directory.
        //   CategoryFolder set  → posts/<folder>/YYYY-MM/<slug>.md
        //   CategoryFolder empty → posts/YYYY-MM/<slug>.md
        // The category folder is the winsoninvest-style convention: posts are
        // grouped by topic (financial_information, stock_investment, etc.) and
        // the sidebar's Topics nav links to /posts/<folder>/index.html. Sites
        // that don't use this convention can leave CategoryFolder empty and
        // the post lands in the bare month directory.
        var yearMonth = DateTime.Today.ToString("yyyy-MM");
        var categoryFolder = (req.CategoryFolder ?? string.Empty).Trim();
        string targetDir;
        if (string.IsNullOrEmpty(categoryFolder))
        {
            targetDir = Path.Combine(contentRoot, yearMonth);
        }
        else
        {
            // Defence in depth — refuse path-traversal and shell-confusing chars.
            if (categoryFolder.Contains("..") || categoryFolder.Contains('/') ||
                categoryFolder.Contains('\\') || categoryFolder.Contains(Path.DirectorySeparatorChar) ||
                categoryFolder.Contains(Path.AltDirectorySeparatorChar))
            {
                return (false, null, "CategoryFolder must be a single folder name (no '/' or '..')");
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(categoryFolder, @"^[a-z0-9][a-z0-9_-]*$"))
            {
                return (false, null, "CategoryFolder must start with a lowercase letter/digit and contain only lowercase letters, digits, underscore, dash");
            }
            targetDir = Path.Combine(contentRoot, categoryFolder, yearMonth);
        }

        var fileName = $"{slug}.md";
        var full = Path.Combine(targetDir, fileName);
        if (File.Exists(full))
        {
            // Disambiguate: append -2, -3, ...
            int n = 2;
            while (File.Exists(Path.Combine(targetDir, $"{slug}-{n}.md"))) n++;
            fileName = $"{slug}-{n}.md";
            full = Path.Combine(targetDir, fileName);
        }

        // Layout defaults to _PostLayout (the convention in this Statiq project).
        // The user can override to _ListLayout, blank, or anything else.
        var layout = string.IsNullOrWhiteSpace(req.Layout) ? "_PostLayout" : req.Layout!.Trim();

        // Every frontmatter field is written to disk, even when the user
        // didn't fill it in. The reasons:
        //   1. The user expects to see the same set of keys on every new
        //      post — they shouldn't have to add a missing key just to
        //      set its value.
        //   2. `Draft: false` should be visible by default so flipping
        //      it to `true` is a one-line in-file edit.
        // Empty strings come out as `Field: ""`, not as a missing key.
        var fm = new FrontmatterData
        {
            Title = title,
            Description = req.Description ?? string.Empty,
            Date = date,
            Layout = layout,
            Image = req.Image ?? string.Empty,
            Category = string.IsNullOrWhiteSpace(req.Category) ? string.Empty : req.Category!.Trim(),
            Draft = req.Draft,
            Tags = req.Tags ?? new List<string>(),
        };
        var body = $"\nWrite your post here. The intro paragraph is what shows up in the homepage card, so make it count.\n";

        try
        {
            Directory.CreateDirectory(targetDir);
            var text = _fm.Compose(fm, body);
            File.WriteAllText(full, text, new UTF8Encoding(false));
            return (true, ToRelative(full), null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Create failed: {Path}", full);
            return (false, null, ex.Message);
        }
    }

    // -------- rename --------

    public (bool ok, string? newRelativePath, string? error) RenamePost(string relativePath, string newSlug)
    {
        var (fullOk, full, err) = ResolveSafePath(relativePath);
        if (!fullOk) return (false, null, err);
        if (!File.Exists(full)) return (false, null, "file not found");

        var slug = Slugify(newSlug);
        if (string.IsNullOrWhiteSpace(slug))
            return (false, null, "invalid slug");

        var newName = $"{slug}.md";
        var newFull = Path.Combine(Path.GetDirectoryName(full)!, newName);
        if (string.Equals(newFull, full, StringComparison.Ordinal))
            return (true, ToRelative(full), null);
        if (File.Exists(newFull))
            return (false, null, $"target already exists: {newName}");

        try
        {
            File.Move(full, newFull);
            return (true, ToRelative(newFull), null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Rename failed: {Path} -> {New}", full, newFull);
            return (false, null, ex.Message);
        }
    }

    // -------- helpers --------

    private PostSummary BuildSummary(string fullPath)
    {
        var raw = File.ReadAllText(fullPath, Encoding.UTF8);
        var (fm, body, _) = _fm.Split(raw);
        var rel = ToRelative(fullPath);
        return new PostSummary
        {
            RelativePath = rel,
            Title = fm.Title ?? Path.GetFileNameWithoutExtension(fullPath),
            Description = fm.Description,
            Date = fm.Date,
            Image = fm.Image,
            Category = fm.Category,
            Tags = fm.Tags,
            Layout = fm.Layout,
            WordCount = CountWords(body),
            CharCount = body.Length,
        };
    }

    /// <summary>
    /// Block any path that tries to escape the content root (e.g. "../../etc/passwd").
    /// relativePath uses forward slashes — we normalise to the platform separator.
    /// </summary>
    private (bool ok, string full, string? err) ResolveSafePath(string relativePath)
    {
        if (!HasActiveSite(out var contentRoot))
            return (false, string.Empty, $"content root missing: {contentRoot}");

        var trimmed = relativePath.TrimStart('/');
        var combined = Path.GetFullPath(Path.Combine(contentRoot, trimmed.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(contentRoot);
        if (!combined.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(combined, rootFull, StringComparison.Ordinal))
        {
            return (false, string.Empty, "path escapes content root");
        }
        return (true, combined, null);
    }

    private string ToRelative(string fullPath)
    {
        var root = Path.GetFullPath(ContentRoot);
        var rel = Path.GetRelativePath(root, fullPath);
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static readonly Regex SlugifyStrip = new("[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex SlugifyCollapse = new("-{2,}", RegexOptions.Compiled);

    // Patterns for finding image references in a post body. Three flavours:
    //   1) <?# Figure src="/images/..." ?>
    //      Our _PostLayout convention — these are always local.
    //   2) ![alt](/images/... "title")
    //      Standard markdown image with optional title.
    //   3) <img src="/images/...">
    //      Raw HTML.
    // All three require the URL to start with /images/ — anything else is
    // either a CDN/external link or pointing at theme-shipped assets, and
    // should not be touched by the cascade.
    private static readonly Regex FigurePattern = new(
        @"<\?#\s*Figure[^>]*src=""([^""]+)""[^>]*\?>",
        RegexOptions.Compiled);
    private static readonly Regex MdImagePattern = new(
        @"!\[[^\]]*\]\(([^)\s]+)(?:\s+""[^""]*"")?\)",
        RegexOptions.Compiled);
    private static readonly Regex HtmlImgPattern = new(
        @"<img[^>]+src=""([^""]+)""[^>]*/?>",
        RegexOptions.Compiled);

    /// <summary>
    /// Extract every local image URL referenced in the markdown body.
    /// Only paths starting with "/images/" are returned — CDN URLs and
    /// theme-shipped assets are filtered out so the cascade never touches
    /// anything we don't own.
    /// </summary>
    public static List<string> ExtractLocalImageUrls(string body)
    {
        if (string.IsNullOrEmpty(body)) return new List<string>();
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pat in new[] { FigurePattern, MdImagePattern, HtmlImgPattern })
        {
            foreach (Match m in pat.Matches(body))
            {
                var url = m.Groups[1].Value.Trim();
                if (!url.StartsWith("/images/", StringComparison.OrdinalIgnoreCase)) continue;
                // Strip the optional title portion of a markdown image — already
                // handled by the regex, but defensive in case of weird inputs.
                var bang = url.IndexOf(' ');
                if (bang > 0) url = url[..bang];
                found.Add(url);
            }
        }
        return found.ToList();
    }

    public static string Slugify(string s)
    {
        var lowered = s.ToLowerInvariant();
        var stripped = SlugifyStrip.Replace(lowered, "-");
        var collapsed = SlugifyCollapse.Replace(stripped, "-");
        return collapsed.Trim('-');
    }

    /// <summary>
    /// True if the string contains any CJK Unified / Extension A / Compatibility
    /// / Fullwidth character. Same coverage as the client-side check in new.js.
    /// </summary>
    private static bool ContainsCjk(string s)
    {
        foreach (var ch in s)
        {
            int code = ch;
            if (
                (0x4E00 <= code && code <= 0x9FFF) ||      // CJK Unified
                (0x3400 <= code && code <= 0x4DBF) ||      // CJK Extension A
                (0x20000 <= code && code <= 0x2A6DF) ||    // CJK Extension B
                (0x3040 <= code && code <= 0x309F) ||      // Hiragana
                (0x30A0 <= code && code <= 0x30FF) ||      // Katakana
                (0xAC00 <= code && code <= 0xD7AF) ||      // Hangul
                (0x3000 <= code && code <= 0x303F) ||      // CJK punctuation
                (0xFF00 <= code && code <= 0xFFEF))        // Fullwidth ASCII
            {
                return true;
            }
        }
        return false;
    }

    private static int CountWords(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return 0;
        // Cheap word count: split on whitespace. CJK characters count
        // individually (each "word" ≈ one character in this approximation).
        // For posts that are mostly English this is good enough; for CJK-only
        // posts it over-counts but stays in the same order of magnitude.
        return body.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}