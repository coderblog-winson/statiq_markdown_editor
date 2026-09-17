using System.Text.Json.Serialization;

namespace StatiqMarkdownEditor.Models;

/// <summary>
/// Root config for the editor. After the Sep 2026 refactor this is
/// just one field: the active site name. The list of available sites
/// is auto-discovered by <c>StatiqRunner.ListSites()</c> from the
/// <c>sites/</c> directory; per-site settings (theme, host, paths)
/// live in <c>sites/&lt;name&gt;/config.json</c>.
/// </summary>
public class StatiqProjectOptions
{
    /// <summary>
    /// Directory name under <c>sites/</c>. Empty string = no site active.
    /// </summary>
    public string ActiveProjectName { get; set; } = string.Empty;
}

public class PostSummary
{
    public string RelativePath { get; set; } = string.Empty; // e.g. "postgres-17-performance-tuning-2026.md"
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Category { get; set; }
    public List<string> Tags { get; set; } = new();
    public DateTime? Date { get; set; }
    public string? Image { get; set; }
    public string? Layout { get; set; }
    public int WordCount { get; set; }
    public int CharCount { get; set; }
}

public class FrontmatterData
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public DateTime? Date { get; set; }
    public string? Layout { get; set; }
    public string? Image { get; set; }
    public string? Category { get; set; }
    /// <summary>
    /// When true, Statiq skips the post during build (it's a draft).
    /// We always emit this field — defaulting to <c>false</c> — so the
    /// user can flip it on later by editing the file directly.
    /// </summary>
    public bool Draft { get; set; }
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, object> Extra { get; set; } = new();
}

public class PostContent
{
    public string RelativePath { get; set; } = string.Empty;
    public FrontmatterData Frontmatter { get; set; } = new();
    /// <summary>Raw body (everything after the closing ---).</summary>
    public string Content { get; set; } = string.Empty;
    /// <summary>Raw full text on disk (only used on read).</summary>
    public string? RawText { get; set; }
}

public class NewPostRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Slug { get; set; } // optional override; otherwise generated from title
    public string? Category { get; set; }
    public List<string>? Tags { get; set; }
    public DateTime? Date { get; set; }
    public string? Image { get; set; }
    public string? Description { get; set; }
    /// <summary>Defaults to "_PostLayout" when null/empty.</summary>
    public string? Layout { get; set; }
    /// <summary>
    /// When true, the post is created with <c>Draft: true</c> (Statiq
    /// will skip it during build). Defaults to <c>false</c> so newly
    /// created posts are immediately publishable.
    /// </summary>
    public bool Draft { get; set; } = false;
}

public class RenameRequest
{
    public string NewSlug { get; set; } = string.Empty;
}

/// <summary>Body for <c>POST /api/projects/activate</c>.</summary>
public class ActivateProjectRequest
{
    public string Name { get; set; } = string.Empty;
}

public class ImageUploadResponse
{
    /// <summary>The final filename on disk (e.g. "my-image-142357.webp").</summary>
    public string Filename { get; set; } = string.Empty;
    /// <summary>Path the markdown should reference, e.g. "/images/2026-09/my-image-142357.webp".</summary>
    public string Url { get; set; } = string.Empty;
    /// <summary>Absolute path on disk.</summary>
    public string FullPath { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public long SizeBytes { get; set; }
}