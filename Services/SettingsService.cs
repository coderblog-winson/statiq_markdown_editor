using System.Text;
using System.Text.Json;
using StatiqMarkdownEditor.Models;

namespace StatiqMarkdownEditor.Services;

/// <summary>
/// Reads and writes appsettings.json (the project's "app config").
///
/// v2 (this file): the config stores a LIST of Statiq projects plus a
/// pointer to the "active" one. v1 stored a single project inline.
/// On first read after the upgrade we detect the v1 shape (a
/// <c>StatiqProject.Root</c> key without <c>StatiqProject.Projects</c>)
/// and migrate in place, pre-populating the three known projects the
/// user works with.
///
/// v3 adds a per-project <c>Watermark</c> string (drawn in the
/// bottom-right of every image uploaded into that project). v2
/// configs without Watermark on a project get the project-specific
/// default written on first save; v1 configs that get migrated to
/// v2 directly get the v2 watermarks too.
///
/// All other top-level keys (Logging, AllowedHosts, etc.) are
/// preserved verbatim. After every save we call
/// <c>IConfigurationRoot.Reload()</c> so the running app picks up the
/// new values without a restart.
/// </summary>
public class SettingsService
{
    private readonly IConfigurationRoot _configRoot;
    private readonly IHostEnvironment _env;
    private readonly ILogger<SettingsService> _log;

    // Lock guarding the file read-modify-write cycle in UpdateAsync.
    // Without it two concurrent PUTs can race and the loser's payload
    // wins, dropping the winner's data.
    private static readonly SemaphoreSlim _writeLock = new(1, 1);

    public SettingsService(IConfiguration config, IHostEnvironment env, ILogger<SettingsService> log)
    {
        _configRoot = (IConfigurationRoot)config;
        _env = env;
        _log = log;
    }

    public string ConfigPath => Path.Combine(_env.ContentRootPath, "appsettings.json");

    /// <summary>
    /// Read the raw file and migrate v1 → v2 if needed. Returns the
    /// current v2-shaped section. Also triggers a config-root reload
    /// so IOptionsMonitor consumers see the new values.
    /// </summary>
    public async Task<(StatiqProjectOptions options, bool migrated)> LoadAsync()
    {
        // Source of truth = the merged IConfiguration (appsettings.json
        // + appsettings.{Environment}.json + env vars). The on-disk file
        // is just a persistence target — reading it directly is wrong
        // because it can be an empty skeleton (the committed shape for
        // public repos) while the real config lives in an env-specific
        // override file.
        var sp = _configRoot.GetSection("StatiqProject");
        // IConfiguration represents arrays as child sections keyed "0",
        // "1", "2", … — NOT as a scalar at the section's own path. So
        // `sp["Projects"]` returns null even when the array is present.
        // We detect "v2 shape" by looking for the child section.
        var projectsSection = sp.GetSection("Projects");
        // (debug logging removed)

        // v2 shape: has a `Projects` section. Bind it from the merged
        // config, then backfill any missing Watermarks.
        if (projectsSection.Exists())
        {
            var opts = new StatiqProjectOptions
            {
                ActiveProjectName = sp["ActiveProjectName"] ?? "",
                Projects = projectsSection.Get<List<StatiqProjectEntry>>() ?? new(),
            };
            var anyMissing = opts.Projects.Any(p => string.IsNullOrEmpty(p.Watermark));
            if (anyMissing)
            {
                _log.LogInformation("Detected v2 config without Watermark fields — backfilling defaults");
                foreach (var p in opts.Projects)
                    if (string.IsNullOrEmpty(p.Watermark))
                        p.Watermark = DefaultWatermarkFor(p.Name);
                await PersistAsync(opts);
                _configRoot.Reload();
                return (opts, true);
            }
            return (opts, false);
        }

        // v1 shape (single Root, no Projects) — only detectable from
        // the committed skeleton file, since env-specific overrides
        // never carry a v1 shape. Read the file to find it.
        var root = await ReadDiskRootAsync();
        if (root.ValueKind != JsonValueKind.Object) return (new StatiqProjectOptions(), false);
        if (!root.TryGetProperty("StatiqProject", out var diskSp) || diskSp.ValueKind != JsonValueKind.Object)
            return (new StatiqProjectOptions(), false);
        if (diskSp.TryGetProperty("Projects", out _) || !diskSp.TryGetProperty("Root", out _))
            return (new StatiqProjectOptions(), false);

        // v1 detected. Migrate to v2 in place, then re-read.
        _log.LogInformation("Detected v1 single-project config — migrating to multi-project v2");
        var migrated = await MigrateV1ToV2Async(root);
        return (migrated, true);
    }

    /// <summary>
    /// Read the on-disk <c>appsettings.json</c> as a JsonElement so
    /// callers that need to preserve non-StatiqProject top-level keys
    /// (Logging, AllowedHosts, etc.) can pass it to <see cref="PersistAsync"/>.
    /// Returns a default (empty) JsonElement if the file is missing or
    /// unreadable — the merge step will then write a fresh file with
    /// just the StatiqProject section.
    /// </summary>
    private async Task<JsonElement> ReadDiskRootAsync()
    {
        if (!File.Exists(ConfigPath)) return default;
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath, Encoding.UTF8));
            // Clone the root so it survives `doc` being disposed.
            return doc.RootElement.Clone();
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// Map the known project names to the watermark text the user wants
    /// drawn in the bottom-right of their images. New project names
    /// fall through to empty (no watermark) — the user can set it in
    /// Settings → active project panel.
    /// </summary>
    private static string DefaultWatermarkFor(string projectName) => projectName switch
    {
        "CoderBlog"    => "Coderblog.In",
        "WinsonInvest" => "Winsoninvest.com",
        "Tableware"    => "Tableware.com",
        _              => string.Empty,
    };

    /// <summary>
    /// Convert a v1 root element (which has <c>StatiqProject.Root</c> etc.
    /// inline) into a v2 <see cref="StatiqProjectOptions"/> with three
    /// pre-populated projects, then persist the result back to
    /// <see cref="ConfigPath"/> and reload the config root.
    /// </summary>
    private async Task<StatiqProjectOptions> MigrateV1ToV2Async(JsonElement root)
    {
        var sp = root.GetProperty("StatiqProject");

        // Pull out the v1 fields, defaulting to empty.
        var v1Root = GetStr(sp, "Root");
        var v1Content = GetStr(sp, "ContentSubdir", "input/posts");
        var v1Images = GetStr(sp, "ImagesSubdir", "input/images");
        var v1Preview = GetStr(sp, "PreviewScriptPath");
        var v1Deploy = GetStr(sp, "DeployScriptPath");
        var v1Port = GetInt(sp, "PreviewPort", 5080);

        // v1 used absolute paths. v2 stores them relative to Root, so
        // strip the Root prefix off. If the script isn't under Root
        // (e.g. user pointed it elsewhere), fall back to just the
        // filename so the path is still meaningful.
        string ToRel(string abs)
        {
            if (string.IsNullOrEmpty(abs)) return "";
            if (!string.IsNullOrEmpty(v1Root) && abs.StartsWith(v1Root, StringComparison.Ordinal))
            {
                var rel = abs.Substring(v1Root.Length).TrimStart('/', '\\');
                return rel;
            }
            return Path.GetFileName(abs);
        }

        var projects = new List<StatiqProjectEntry>
        {
            // Carry over the existing single project as "CoderBlog" —
            // its root is the v1 root, defaults otherwise.
            new StatiqProjectEntry
            {
                Name = "CoderBlog",
                Root = v1Root,
                ContentSubdir = string.IsNullOrEmpty(v1Content) ? "input/posts" : v1Content,
                ImagesSubdir = string.IsNullOrEmpty(v1Images) ? "input/images" : v1Images,
                PreviewScriptPath = ToRel(v1Preview),
                DeployScriptPath = ToRel(v1Deploy),
                PreviewPort = v1Port,
                Watermark = DefaultWatermarkFor("CoderBlog"),
            },
            // The other two are pre-populated so the user doesn't have
            // to type them in. Their preview.sh may not exist on disk
            // (WinsonInvest / Tableware only ship build_deploy.sh) —
            // leave preview path empty so the Preview button reports a
            // "not configured" error rather than failing to spawn.
            new StatiqProjectEntry
            {
                Name = "WinsonInvest",
                Root = "/Volumes/Software/MyWebSites/WinsonInvest.com/winsoninvest.statiq/winsoninvest.statiq",
                PreviewScriptPath = "", // no preview.sh in this project
                DeployScriptPath = "build_deploy.sh",
                PreviewPort = 5081,
                Watermark = DefaultWatermarkFor("WinsonInvest"),
            },
            new StatiqProjectEntry
            {
                Name = "Tableware",
                Root = "/Volumes/Software/MyWebSites/Tableware.com/tableware.statiq/tableware.statiq.site",
                PreviewScriptPath = "", // no preview.sh in this project
                DeployScriptPath = "build_deploy.sh",
                PreviewPort = 5082,
                Watermark = DefaultWatermarkFor("Tableware"),
            },
        };

        var v2 = new StatiqProjectOptions
        {
            Projects = projects,
            ActiveProjectName = "CoderBlog",
        };

        await PersistAsync(v2);
        _configRoot.Reload();
        return v2;
    }

    public async Task<(bool ok, string? error)> UpdateAsync(SettingsUpdateRequest req)
    {
        if (req is null) return (false, "request body required");
        if (req.Projects == null) return (false, "projects list required");

        // Normalise: trim names, paths, ensure non-empty defaults.
        var projects = req.Projects
            .Where(p => !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.Root))
            .Select(p => new StatiqProjectEntry
            {
                Name = p.Name.Trim(),
                Root = p.Root.Trim().TrimEnd('/', '\\'),
                ContentSubdir = string.IsNullOrWhiteSpace(p.ContentSubdir) ? "input/posts" : p.ContentSubdir.Trim().Trim('/').Trim('\\'),
                ImagesSubdir = string.IsNullOrWhiteSpace(p.ImagesSubdir) ? "input/images" : p.ImagesSubdir.Trim().Trim('/').Trim('\\'),
                PreviewScriptPath = p.PreviewScriptPath?.Trim() ?? "",
                DeployScriptPath = p.DeployScriptPath?.Trim() ?? "",
                PreviewPort = p.PreviewPort > 0 ? p.PreviewPort : 5080,
                Watermark = p.Watermark?.Trim() ?? "",
            })
            .ToList();

        // De-duplicate by name (last write wins).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<StatiqProjectEntry>();
        foreach (var p in projects)
        {
            if (seen.Add(p.Name)) deduped.Add(p);
        }

        // Active name: if not provided OR doesn't match any project,
        // fall back to the first one.
        var activeName = (req.ActiveProjectName ?? "").Trim();
        if (string.IsNullOrEmpty(activeName) || !deduped.Any(p => p.Name == activeName))
        {
            activeName = deduped.FirstOrDefault()?.Name ?? "";
        }

        var v2 = new StatiqProjectOptions
        {
            Projects = deduped,
            ActiveProjectName = activeName,
        };

        await _writeLock.WaitAsync();
        try
        {
            JsonElement existing = default;
            if (File.Exists(ConfigPath))
            {
                try { existing = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath, Encoding.UTF8)).RootElement; }
                catch { /* fall through with default */ }
            }

            await PersistAsync(v2);
            _configRoot.Reload();
            _log.LogInformation("Updated {Path}; {Count} project(s), active={Active}",
                ConfigPath, deduped.Count, activeName);
            return (true, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to update {Path}", ConfigPath);
            return (false, ex.Message);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Just the active-name update. Used by the top-nav project switcher
    /// (don't want to round-trip the whole projects list on every click).
    /// </summary>
    public async Task<(bool ok, string? error)> SetActiveProjectAsync(string name)
    {
        await _writeLock.WaitAsync();
        try
        {
            var (current, _) = await LoadAsync();
            if (string.IsNullOrEmpty(name) || !current.Projects.Any(p => p.Name == name))
                return (false, $"Unknown project: {name}");

            current.ActiveProjectName = name;
            await PersistAsync(current);
            _configRoot.Reload();
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ----------------------------------------------------------------
    //  Helpers
    // ----------------------------------------------------------------

    /// <summary>
    /// The file we should actually write to when persisting config. In
    /// the default case (no env-specific override) this is just
    /// <c>appsettings.json</c>. But when the user keeps their real
    /// config in <c>appsettings.{Environment}.json</c> (the privacy
    /// refactor pattern), writing to <c>appsettings.json</c> would be
    /// silently shadowed by the env-specific file and the change would
    /// never take effect — so we write to the env file instead. Either
    /// way, the on-disk file we choose to write becomes the most
    /// specific source, winning the merge.
    /// </summary>
    private string GetEffectiveConfigPath()
    {
        var envName = _env.EnvironmentName;
        if (!string.IsNullOrEmpty(envName))
        {
            var envPath = Path.Combine(_env.ContentRootPath, $"appsettings.{envName}.json");
            if (File.Exists(envPath)) return envPath;
        }
        return ConfigPath;
    }

    private async Task PersistAsync(StatiqProjectOptions v2)
    {
        // Read the file we're about to write to (not always the base
        // appsettings.json) so we preserve its non-StatiqProject keys.
        var targetPath = GetEffectiveConfigPath();
        var existing = await ReadFileRootAsync(targetPath);
        var dir = Path.GetDirectoryName(targetPath) ?? ".";

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            w.WriteStartObject();

            // Preserve unrelated top-level keys (Logging, AllowedHosts, …)
            if (existing.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in existing.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "StatiqProject", StringComparison.Ordinal))
                        continue;
                    prop.WriteTo(w);
                }
            }

            w.WriteStartObject("StatiqProject");
            w.WriteStartArray("Projects");
            foreach (var p in v2.Projects)
            {
                w.WriteStartObject();
                w.WriteString("Name", p.Name);
                w.WriteString("Root", p.Root);
                w.WriteString("ContentSubdir", p.ContentSubdir);
                w.WriteString("ImagesSubdir", p.ImagesSubdir);
                w.WriteString("PreviewScriptPath", p.PreviewScriptPath);
                w.WriteString("DeployScriptPath", p.DeployScriptPath);
                w.WriteNumber("PreviewPort", p.PreviewPort);
                w.WriteString("Watermark", p.Watermark ?? string.Empty);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteString("ActiveProjectName", v2.ActiveProjectName);
            w.WriteEndObject();

            w.WriteEndObject();
        }

        var tmp = targetPath + ".tmp";
        var bytes = ms.ToArray();
        var text = new UTF8Encoding(false).GetString(bytes).TrimEnd('\r', '\n') + "\n";
        await File.WriteAllTextAsync(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, targetPath, overwrite: true);
        _log.LogInformation("Persisted config to {Path}", targetPath);
    }

    private static async Task<JsonElement> ReadFileRootAsync(string path)
    {
        if (!File.Exists(path)) return default;
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, Encoding.UTF8));
            return doc.RootElement.Clone();
        }
        catch
        {
            return default;
        }
    }

    private static string GetStr(JsonElement obj, string key, string fallback = "")
    {
        if (obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? fallback;
        return fallback;
    }
    private static int GetInt(JsonElement obj, string key, int fallback)
    {
        if (obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
            return n;
        return fallback;
    }
}
