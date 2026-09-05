using System.Text.Json.Serialization;

namespace StatiqMarkdownEditor.Models;

/// <summary>
/// One Statiq project on disk. Multiple of these are listed in
/// <see cref="StatiqProjectOptions.Projects"/>; the user picks one
/// to be "active" via the top-nav dropdown. Script paths are stored
/// RELATIVE to <see cref="Root"/> so a project tree is self-contained
/// and portable — copy the folder and the config still works.
/// </summary>
public class StatiqProjectEntry
{
    /// <summary>Display name shown in the project dropdown. e.g. "CoderBlog".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Absolute path to the project root (folder containing Program.cs).</summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>Posts subdir, relative to <see cref="Root"/>. Defaults to "input/posts".</summary>
    public string ContentSubdir { get; set; } = "input/posts";

    /// <summary>Images subdir, relative to <see cref="Root"/>. Defaults to "input/images".</summary>
    public string ImagesSubdir { get; set; } = "input/images";

    /// <summary>
    /// Path to the preview shell script, relative to <see cref="Root"/>.
    /// Leave empty to disable the Preview button for this project.
    /// </summary>
    public string PreviewScriptPath { get; set; } = "preview.sh";

    /// <summary>
    /// Path to the build+deploy shell script, relative to <see cref="Root"/>.
    /// Leave empty to disable the Deploy button.
    /// </summary>
    public string DeployScriptPath { get; set; } = "build_deploy.sh";

    /// <summary>
    /// Port the preview server listens on. The Preview script is invoked
    /// with this as its first argument (preview.sh takes the port as <c>$1</c>).
    /// </summary>
    public int PreviewPort { get; set; } = 5080;

    /// <summary>
    /// Text drawn in the bottom-right corner of every image uploaded or
    /// pasted into this project (e.g. "WinsonInvest.com"). Leave empty
    /// to skip watermarking. Rendered by <c>ImageService</c> using
    /// ImageSharp.Drawing.
    /// </summary>
    public string Watermark { get; set; } = string.Empty;
}

/// <summary>
/// Top-level config. The active project is the one the editor is
/// currently pointed at — its <c>Root</c> + <c>ContentSubdir</c> drive
/// every API endpoint, and the top-nav dropdown changes this.
/// </summary>
public class StatiqProjectOptions
{
    /// <summary>All known projects. Empty list is a UI-visible "no project".</summary>
    public List<StatiqProjectEntry> Projects { get; set; } = new();

    /// <summary>
    /// Name of the project to use for this session. If it doesn't match
    /// any entry, <see cref="Active"/> falls back to the first project
    /// in the list (or an empty entry if the list is empty).
    /// </summary>
    public string ActiveProjectName { get; set; } = string.Empty;

    /// <summary>
    /// Resolved "current" project entry. Computed on every access so it
    /// picks up changes whenever <c>Projects</c> or
    /// <c>ActiveProjectName</c> is rebound.
    /// </summary>
    [JsonIgnore]
    public StatiqProjectEntry Active =>
        Projects.FirstOrDefault(p => p.Name == ActiveProjectName)
        ?? Projects.FirstOrDefault()
        ?? new StatiqProjectEntry { Name = "(none)" };
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

public class SettingsUpdateRequest
{
    /// <summary>All projects. Sent in full on every save (no merge).</summary>
    public List<StatiqProjectEntry> Projects { get; set; } = new();
    /// <summary>Name of the project to use as active. Empty = no change.</summary>
    public string? ActiveProjectName { get; set; }
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
