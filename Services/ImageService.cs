using System.Text.RegularExpressions;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using StatiqMarkdownEditor.Models;

namespace StatiqMarkdownEditor.Services;

/// <summary>
/// Receives an uploaded image (any format ImageSharp can decode), converts it to
/// WebP, and writes it under <c>sites/&lt;active&gt;/input/images/{YYYY-MM}/</c>.
/// The target month comes from the post's frontmatter Date when available,
/// otherwise the upload time.
///
/// Watermarking is wired up but disabled by default: the per-site
/// <c>Watermark</c> field lives on <see cref="SiteConfig"/> and is empty
/// unless the user opts in. When non-empty, the text is drawn bottom-right
/// in white bold sans-serif with a subtle dark drop shadow. Font is
/// resolved from a short list of system paths (Helvetica on macOS,
/// Arial Bold on Windows, DejaVu on Linux) — no font file bundled.
/// </summary>
public class ImageService
{
    private readonly StatiqRunner _runner;
    private readonly ILogger<ImageService> _log;

    /// <summary>Hard cap to keep one paste from blowing up memory.</summary>
    public const long MaxBytes = 25 * 1024 * 1024; // 25 MB

    public ImageService(StatiqRunner runner, ILogger<ImageService> log)
    {
        _runner = runner;
        _log = log;
    }

    private string ImagesRoot
    {
        get
        {
            var paths = _runner.GetActiveSitePathsSync();
            if (paths == null) return string.Empty;
            return Path.Combine(paths.InputDir, "images");
        }
    }

    /// <summary>
    /// Per-site watermark text. Empty = no watermark. Reads from the active
    /// site's <c>config.json</c> if it has a <c>Watermark</c> field.
    /// </summary>
    private string Watermark
    {
        get
        {
            var paths = _runner.GetActiveSitePathsSync();
            // SiteConfig doesn't yet expose Watermark — when it does,
            // read it here. Until then: no watermark.
            return string.Empty;
        }
    }

    public (bool ok, ImageUploadResponse? resp, string? error) SaveWebp(
        Stream input, string suggestedName, DateTime? postDate, long sizeBytes)
    {
        if (sizeBytes > MaxBytes)
            return (false, null, $"file too large ({sizeBytes / 1024 / 1024} MB; max {MaxBytes / 1024 / 1024} MB)");
        if (string.IsNullOrEmpty(ImagesRoot))
            return (false, null, "no active site — pick one in Settings");

        var targetMonth = (postDate ?? DateTime.Today);
        var yearMonth = targetMonth.ToString("yyyy-MM");
        var dir = Path.Combine(ImagesRoot, yearMonth);

        // Defence in depth: ensure dir is inside the configured images root.
        var dirFull = Path.GetFullPath(dir);
        var imagesFull = Path.GetFullPath(ImagesRoot);
        if (!dirFull.StartsWith(imagesFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(dirFull, imagesFull, StringComparison.Ordinal))
        {
            return (false, null, "refusing to write outside images root");
        }

        // Derive a clean base name. Prefer the suggested name (e.g. the original
        // file name) if the user uploaded one; fall back to "image".
        var baseName = Slugify(Path.GetFileNameWithoutExtension(suggestedName));
        if (string.IsNullOrEmpty(baseName)) baseName = "image";

        // Add a short disambiguator so multiple pastes don't collide.
        var stamp = DateTime.UtcNow.ToString("HHmmss");
        var baseWithStamp = $"{baseName}-{stamp}";

        string fileName;
        string fullPath;
        int n = 1;
        do
        {
            fileName = n == 1 ? $"{baseWithStamp}.webp" : $"{baseWithStamp}-{n}.webp";
            fullPath = Path.Combine(dir, fileName);
            n++;
        } while (File.Exists(fullPath) && n < 1000);
        if (File.Exists(fullPath))
            return (false, null, "could not find a free filename after 1000 tries");

        // Pull watermark up front so we can decide whether to draw before
        // any expensive work. Empty watermark = no draw (cheaper + correct
        // for sites that opt out).
        var watermark = Watermark.Trim();
        var drawWatermark = !string.IsNullOrEmpty(watermark);
        Font? wmFont = null;
        if (drawWatermark)
        {
            try { wmFont = ResolveFont(); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Watermark font could not be resolved; skipping watermark for {Path}", fullPath);
                drawWatermark = false;
            }
        }

        try
        {
            Directory.CreateDirectory(dir);
            int width, height;
            long savedSize;
            using (var image = Image.Load(input))
            {
                width = image.Width;
                height = image.Height;

                if (drawWatermark && wmFont is not null)
                {
                    ApplyWatermark(image, watermark!, wmFont);
                }

                // WebP with quality 85 — matches the og:image convention used in
                // the coderblog project. No resize: we trust the source dimensions.
                var encoder = new WebpEncoder { Quality = 85 };
                using (var fs = File.Create(fullPath))
                {
                    image.Save(fs, encoder);
                }
            }
            // File handle is closed by now — safe to read final size.
            savedSize = new FileInfo(fullPath).Length;
            _log.LogInformation("Saved {Path} ({Bytes} bytes from {Source}, watermark={Wm})",
                fullPath, savedSize, suggestedName, drawWatermark ? watermark : "(none)");

            // The URL the markdown references must match what Statiq emits
            // at build time. Convention: drop the "input/" prefix so
            // `input/images/2026-09/foo.webp` becomes `/images/2026-09/foo.webp`
            // in the published HTML. Coderblog, alphaLedger, and most
            // themes follow this.
            const string urlSubdir = "images";

            return (true, new ImageUploadResponse
            {
                Filename = fileName,
                Url = "/" + urlSubdir + "/" + yearMonth + "/" + fileName,
                FullPath = fullPath,
                Width = width,
                Height = height,
                SizeBytes = savedSize,
            }, null);
        }
        catch (UnknownImageFormatException ex)
        {
            return (false, null, $"unrecognised image format: {ex.Message}");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Image save failed: {Path}", fullPath);
            return (false, null, ex.Message);
        }
    }

    // ----------------------------------------------------------------
    //  Watermark rendering
    // ----------------------------------------------------------------

    /// <summary>
    /// Draw <paramref name="text"/> in the bottom-right of <paramref name="image"/>
    /// in white, with a small dark drop shadow so it stays legible on light
    /// backgrounds. Font size scales with image width (~3.2%) and the text
    /// is inset by ~3% from the right and bottom edges.
    /// </summary>
    private static void ApplyWatermark(Image image, string text, Font font)
    {
        // Font size proportional to image width. Capped to keep very large
        // hero images from getting absurdly big watermarks.
        var fontSize = Math.Clamp(image.Width * 0.032f, 18f, 96f);
        var sizedFont = new Font(font, fontSize, FontStyle.Bold);

        // Measure first so we can compute the right padding.
        var measureOptions = new RichTextOptions(sizedFont) { WrappingLength = image.Width };
        var size = TextMeasurer.MeasureSize(text, measureOptions);

        // Inset ~3% of width from the right and bottom; never less than 12px.
        var pad = Math.Max(12f, image.Width * 0.03f);
        var x = image.Width - size.Width - pad;
        var y = image.Height - size.Height - pad;

        // Drop shadow: render the same text in a dark semi-transparent color
        // a few pixels offset behind the white text. This keeps the watermark
        // readable on both light and dark images without us having to sample
        // the background.
        var shadowOffset = Math.Max(2f, fontSize * 0.08f);
        var shadowOpts = new RichTextOptions(sizedFont)
        {
            Origin = new PointF(x + shadowOffset, y + shadowOffset),
            WrappingLength = image.Width,
        };
        var mainOpts = new RichTextOptions(sizedFont)
        {
            Origin = new PointF(x, y),
            WrappingLength = image.Width,
        };

        image.Mutate(ctx => ctx
            .DrawText(shadowOpts, text, Color.FromRgba(0, 0, 0, 180))
            .DrawText(mainOpts, text, Color.FromRgba(255, 255, 255, 230))
        );
    }

    /// <summary>
    /// Try a short list of system bold-sans font paths (per platform) and
    /// load the first one that exists. Throws if nothing on the list is
    /// available — caller logs and falls back to "no watermark".
    /// </summary>
    private static Font ResolveFont()
    {
        var candidates = new List<string>();
        if (OperatingSystem.IsMacOS())
        {
            candidates.Add("/System/Library/Fonts/Helvetica.ttc");
            candidates.Add("/System/Library/Fonts/HelveticaNeue.ttc");
            candidates.Add("/System/Library/Fonts/Supplemental/Arial.ttf");
            candidates.Add("/System/Library/Fonts/Hiragino Sans GB.ttc");
            candidates.Add("/System/Library/Fonts/Supplemental/Arial Bold.ttf");
        }
        else if (OperatingSystem.IsWindows())
        {
            candidates.Add(@"C:\Windows\Fonts\arialbd.ttf");
            candidates.Add(@"C:\Windows\Fonts\segoeuib.ttf");
        }
        else // Linux / other
        {
            candidates.Add("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf");
            candidates.Add("/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf");
            candidates.Add("/usr/share/fonts/TTF/DejaVuSans-Bold.ttf");
        }

        Exception? lastEx = null;
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var collection = new FontCollection();
                var family = collection.Add(path);
                // Try Bold first; fall back to regular if no bold face is in
                // the family (common for .ttc files that only ship one style).
                // CreateFont requires an emSize, so we pass a placeholder
                // value here — the actual size is set per-render via
                // `new Font(face, size, FontStyle.Bold)`.
                const float probeSize = 12f;
                var face = family.TryGetMetrics(FontStyle.Bold, out _)
                    ? family.CreateFont(probeSize, FontStyle.Bold)
                    : family.CreateFont(probeSize);
                return face;
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }
        }
        throw new InvalidOperationException(
            $"No usable bold-sans font found. Tried: {string.Join(", ", candidates)}",
            lastEx);
    }

    // ----------------------------------------------------------------
    //  Delete
    // ----------------------------------------------------------------

    /// <summary>
    /// Delete the on-disk file under <c>sites/&lt;active&gt;/input/images/</c>
    /// that the given URL resolves to. Used by the post-delete cascade so
    /// that removing an article also removes its uploaded figures.
    ///
    /// Safety: only ever touches files under the configured images root, and
    /// never follows symlinks — a hostile markdown link could otherwise
    /// redirect to anywhere on disk.
    /// </summary>
    public (bool ok, string? error) DeleteImageByUrl(string url)
    {
        if (string.IsNullOrEmpty(ImagesRoot))
            return (false, "no active site — pick one in Settings");

        // Reject anything that doesn't start with /images/. CDN URLs,
        // /assets/ (theme-shipped), absolute http(s)://... — none of those
        // are user uploads, so we never touch them.
        var trimmed = (url ?? string.Empty).TrimStart('/');
        // URL is "/images/2026-09/foo.webp" — strip the "/images/" prefix so we
        // don't double-up with ImagesRoot (which already ends in /images).
        const string prefix = "images/";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return (false, $"url does not start with /images/: {url}");
        trimmed = trimmed[prefix.Length..];
        if (string.IsNullOrEmpty(trimmed))
            return (false, "url resolves to images/ root, refusing");

        var full = Path.GetFullPath(Path.Combine(
            ImagesRoot, trimmed.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(ImagesRoot);
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(full, rootFull, StringComparison.Ordinal))
        {
            return (false, $"refusing to delete outside images root: {url}");
        }

        if (!File.Exists(full))
            return (false, $"file not found: {url}");

        // Symlink check — same reason as /api/local-image's check.
        var attrs = File.GetAttributes(full);
        if ((attrs & FileAttributes.ReparsePoint) != 0)
            return (false, $"refusing to delete a symlink: {url}");

        try
        {
            File.Delete(full);
            _log.LogInformation("Deleted image: {Path}", full);
            return (true, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Image delete failed: {Path}", full);
            return (false, ex.Message);
        }
    }

    // ----------------------------------------------------------------
    //  Slug helper
    // ----------------------------------------------------------------

    private static readonly Regex SlugifyStrip = new("[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex SlugifyCollapse = new("-{2,}", RegexOptions.Compiled);

    public static string Slugify(string s)
    {
        var lowered = s.ToLowerInvariant();
        var stripped = SlugifyStrip.Replace(lowered, "-");
        var collapsed = SlugifyCollapse.Replace(stripped, "-");
        return collapsed.Trim('-');
    }
}
