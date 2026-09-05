using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using StatiqMarkdownEditor.Models;

namespace StatiqMarkdownEditor.Services;

/// <summary>
/// All file IO for the editor. Scoped to a single Statiq project (path injected via options).
/// v1: no locking, no concurrent-write protection. Single-user local tool.
/// </summary>
public class MarkdownFileService
{
    private readonly IOptionsMonitor<StatiqProjectOptions> _opt;
    private readonly FrontmatterService _fm;
    private readonly ILogger<MarkdownFileService> _log;

    public MarkdownFileService(
        IOptionsMonitor<StatiqProjectOptions> opt,
        FrontmatterService fm,
        ILogger<MarkdownFileService> log)
    {
        _opt = opt;
        _fm = fm;
        _log = log;
    }

    private StatiqProjectOptions CurrentOpt => _opt.CurrentValue;

    public string ContentRoot
    {
        get
        {
            var p = CurrentOpt.Active;
            return string.IsNullOrEmpty(p.Root) ? string.Empty : Path.Combine(p.Root, p.ContentSubdir);
        }
    }

    public string ImagesRoot
    {
        get
        {
            var p = CurrentOpt.Active;
            return string.IsNullOrEmpty(p.Root) ? string.Empty : Path.Combine(p.Root, p.ImagesSubdir);
        }
    }

    // -------- list --------

    public List<PostSummary> ListPosts()
    {
        if (string.IsNullOrEmpty(CurrentOpt.Active.Root) || !Directory.Exists(ContentRoot))
            return new List<PostSummary>();

        var list = new List<PostSummary>();
        foreach (var path in Directory.EnumerateFiles(ContentRoot, "*.md", SearchOption.AllDirectories))
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

    public (bool ok, string? error) DeletePost(string relativePath)
    {
        var (fullOk, full, err) = ResolveSafePath(relativePath);
        if (!fullOk) return (false, err);
        if (!File.Exists(full)) return (false, $"file not found: {relativePath}");
        try
        {
            File.Delete(full);
            return (true, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Delete failed: {Path}", full);
            return (false, ex.Message);
        }
    }

    // -------- create --------

    public (bool ok, string? relativePath, string? error) CreatePost(NewPostRequest req)
    {
        if (string.IsNullOrEmpty(CurrentOpt.Active.Root) || !Directory.Exists(ContentRoot))
            return (false, null, $"content root missing: {ContentRoot}");

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
        var slug = hasExplicitSlug
            ? Slugify(req.Slug!)
            : $"{Slugify(title)}-{date:yyyy}";
        if (string.IsNullOrWhiteSpace(slug))
            return (false, null, "could not derive a slug from title");

        // Drop the new file under a {YYYY-MM}/ subdirectory so the posts tree
        // matches the convention used in the coderblog project (see
        // input/posts/202609/...). The month is "now" — this is where the post
        // is *created*, not when it claims to be published. Existing posts
        // outside a month dir are not touched.
        var yearMonth = DateTime.Today.ToString("yyyy-MM");
        var targetDir = Path.Combine(ContentRoot, yearMonth);

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
        if (string.IsNullOrEmpty(CurrentOpt.Active.Root) || !Directory.Exists(ContentRoot))
            return (false, string.Empty, $"content root missing: {ContentRoot}");

        var trimmed = relativePath.TrimStart('/');
        var combined = Path.GetFullPath(Path.Combine(ContentRoot, trimmed.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(ContentRoot);
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
    public static bool ContainsCjk(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var ch in s)
        {
            // CJK Unified Ideographs
            if (ch >= '\u4E00' && ch <= '\u9FFF') return true;
            // CJK Extension A
            if (ch >= '\u3400' && ch <= '\u4DBF') return true;
            // CJK Compatibility Ideographs
            if (ch >= '\uF900' && ch <= '\uFAFF') return true;
            // Fullwidth forms
            if (ch >= '\uFF00' && ch <= '\uFFEF') return true;
        }
        return false;
    }

    private static int CountWords(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        int n = 0;
        bool inWord = false;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            var isWs = char.IsWhiteSpace(c) || c == '\n' || c == '\r' || c == '\t';
            if (isWs) inWord = false;
            else if (!inWord) { inWord = true; n++; }
        }
        return n;
    }
}
