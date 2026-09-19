using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SME.Statiq;
using StatiqMarkdownEditor.Models;

namespace StatiqMarkdownEditor.Services;

/// <summary>
/// Receives an uploaded image (any format ImageSharp can decode), converts it to
/// WebP, and writes it under <c>sites/&lt;active&gt;/input/images/{YYYY-MM}/</c>.
/// The target month comes from the post's frontmatter Date when available,
/// otherwise the upload time.
///
/// Watermarking is delegated to the sibling <c>WatermarkTool</c> project: when
/// the active site's <c>config.json</c> has a non-empty <c>Watermark</c> field,
/// the upload is staged to a temp file, WatermarkTool EXE is invoked to draw
/// the watermark and re-encode as WebP. Empty watermark → encode with
/// ImageSharp directly (faster, no EXE spawn).
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
    /// Per-site watermark text from the active site's <c>config.json</c>.
    /// Empty = no watermark (ImageSharp encode path). Non-empty = dispatch
    /// through WatermarkTool EXE.
    /// </summary>
    private string Watermark
    {
        get
        {
            var cfg = LoadActiveSiteConfig();
            return cfg?.Watermark?.Trim() ?? string.Empty;
        }
    }

    /// <summary>
    /// Path to the WatermarkTool EXE. Reads from
    /// <see cref="SiteConfig.WatermarkToolPath"/> if set, otherwise defaults to
    /// <c>{editorRoot}/WatermarkTool/bin/Debug/net9.0/WatermarkTool[.exe]</c>
    /// (the standard debug-build location — WatermarkTool is a sub-project
    /// inside the editor repo, not a sibling).
    /// </summary>
    private string WatermarkToolExePath
    {
        get
        {
            var cfg = LoadActiveSiteConfig();
            if (cfg != null && !string.IsNullOrEmpty(cfg.WatermarkToolPath))
                return cfg.WatermarkToolPath;

            var exeSuffix = OperatingSystem.IsWindows() ? ".exe" : "";
            return Path.GetFullPath(Path.Combine(
                _runner.EditorRoot, "WatermarkTool", "bin", "Debug", "net9.0",
                "WatermarkTool" + exeSuffix));
        }
    }

    /// <summary>
    /// Sync-load the active site's <c>config.json</c>. Returns null if no
    /// active site or config can't be parsed — both call sites already treat
    /// null as "feature disabled".
    /// </summary>
    private SiteConfig? LoadActiveSiteConfig()
    {
        var paths = _runner.GetActiveSitePathsSync();
        if (paths == null) return null;
        var cfgPath = Path.Combine(paths.SiteDir, "config.json");
        if (!File.Exists(cfgPath)) return null;
        try
        {
            var json = File.ReadAllText(cfgPath);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var cfg = JsonSerializer.Deserialize<SiteConfig>(json, opts);
            if (cfg == null) return null;
            cfg.Name = Path.GetFileName(paths.SiteDir);
            return cfg;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not parse site config at {Path}", cfgPath);
            return null;
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

        var watermark = Watermark;
        var useWatermarkTool = !string.IsNullOrEmpty(watermark);

        try
        {
            Directory.CreateDirectory(dir);
            int width, height;
            long savedSize;

            if (useWatermarkTool)
            {
                // WatermarkTool reads the source, draws the watermark, and writes
                // the destination as WebP. We just stage the input to a temp file
                // and probe the resulting dims after (Image.Identify reads only
                // the header, doesn't decode pixels).
                var tempIn = Path.Combine(Path.GetTempPath(), $"wm-{Guid.NewGuid():N}.src");
                try
                {
                    using (var fs = File.Create(tempIn))
                        input.CopyTo(fs);

                    var exitCode = RunWatermarkTool(tempIn, fullPath, watermark);
                    if (exitCode != 0)
                        return (false, null, $"WatermarkTool exited with code {exitCode} (check server logs)");

                    var info = Image.Identify(fullPath);
                    width = info.Width;
                    height = info.Height;
                    savedSize = new FileInfo(fullPath).Length;
                }
                finally
                {
                    try { if (File.Exists(tempIn)) File.Delete(tempIn); }
                    catch (Exception ex) { _log.LogWarning(ex, "Could not delete temp input {Path}", tempIn); }
                }
            }
            else
            {
                // No watermark — ImageSharp encode directly (faster, no EXE spawn).
                using (var image = Image.Load(input))
                {
                    width = image.Width;
                    height = image.Height;
                    // WebP with quality 85 — matches the og:image convention used
                    // in the coderblog project. No resize: we trust the source.
                    var encoder = new WebpEncoder { Quality = 85 };
                    using var fs = File.Create(fullPath);
                    image.Save(fs, encoder);
                }
                savedSize = new FileInfo(fullPath).Length;
            }

            _log.LogInformation("Saved {Path} ({Bytes} bytes from {Source}, watermark={Wm})",
                fullPath, savedSize, suggestedName, useWatermarkTool ? watermark : "(none)");

            // The URL the markdown references must match what Statiq emits at
            // build time. Convention: drop the "input/" prefix so
            // `input/images/2026-09/foo.webp` becomes `/images/2026-09/foo.webp`
            // in the published HTML. Coderblog, alphaLedger, and most themes
            // follow this.
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

    /// <summary>
    /// Invoke WatermarkTool EXE in single-file mode. Returns the exit code.
    /// Throws FileNotFoundException when the EXE is missing — caller logs and
    /// surfaces the error to the user.
    /// </summary>
    private int RunWatermarkTool(string inPath, string outPath, string text)
    {
        var exe = WatermarkToolExePath;
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException(
                $"WatermarkTool EXE not found at {exe}. " +
                "Build it first: dotnet build -c Debug from WatermarkTool/. " +
                "Or set config.json WatermarkToolPath to a custom location.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // ArgumentList auto-escapes each arg — safer than building a string.
        psi.ArgumentList.Add("--in");
        psi.ArgumentList.Add(inPath);
        psi.ArgumentList.Add("--out");
        psi.ArgumentList.Add(outPath);
        psi.ArgumentList.Add("--text");
        psi.ArgumentList.Add(text);

        _log.LogInformation("Invoking WatermarkTool: {Exe} --in {In} --out {Out} --text \"{Text}\"",
            exe, inPath, outPath, text);

        using var proc = Process.Start(psi)!;
        // Drain stdout/stderr asynchronously so the child doesn't block on a
        // full pipe buffer if it prints a lot.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(60_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException("WatermarkTool did not finish within 60s");
        }
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (!string.IsNullOrWhiteSpace(stdout)) _log.LogDebug("WatermarkTool stdout: {Out}", stdout.Trim());
        if (!string.IsNullOrWhiteSpace(stderr)) _log.LogDebug("WatermarkTool stderr: {Err}", stderr.Trim());

        return proc.ExitCode;
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