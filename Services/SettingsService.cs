using System.Text;
using System.Text.Json;

namespace StatiqMarkdownEditor.Services;

/// <summary>
/// Persists the editor's currently-active site name to
/// <c>appsettings.json</c> (or <c>appsettings.{Environment}.json</c> when
/// the env-specific override is present — see <see cref="GetEffectiveConfigPath"/>).
///
/// The actual list of sites lives in <c>sites/&lt;name&gt;/config.json</c>
/// — auto-discovered by <see cref="StatiqRunner.ListSites"/>. This service
/// only remembers which one the user picked.
///
/// Legacy: previously this also parsed a 3-project array (CoderBlog /
/// WinsonInvest / Tableware) with their absolute <c>Root</c> paths,
/// <c>ContentSubdir</c>, preview/deploy script paths, watermarks, etc.
/// All of that moved to per-site <c>config.json</c> + theme pages in the
/// in-process Statiq.Web refactor (Sep 2026).
/// </summary>
public class SettingsService
{
    private readonly IConfigurationRoot _configRoot;
    private readonly IHostEnvironment _env;
    private readonly ILogger<SettingsService> _log;

    // Lock guarding the read-modify-write cycle in SetActiveProjectAsync.
    private static readonly SemaphoreSlim _writeLock = new(1, 1);

    public SettingsService(IConfiguration config, IHostEnvironment env, ILogger<SettingsService> log)
    {
        _configRoot = (IConfigurationRoot)config;
        _env = env;
        _log = log;
    }

    public string ConfigPath => Path.Combine(_env.ContentRootPath, "appsettings.json");

    /// <summary>
    /// Read the active site name from the merged config (IConfiguration,
    /// not the on-disk file — env-specific overrides win the merge).
    /// Returns empty string if not set.
    /// </summary>
    public string GetActiveProjectName()
    {
        var sp = _configRoot.GetSection("StatiqProject");
        return sp["ActiveProjectName"] ?? "";
    }

    /// <summary>
    /// Update the active site name and persist. The caller is responsible
    /// for verifying the name is valid (matches a directory under
    /// <c>sites/</c>); we don't double-check here so the editor can
    /// tolerate a stale config pointing at a renamed site without
    /// refusing the write.
    /// </summary>
    public async Task<(bool ok, string? error)> SetActiveProjectAsync(string name)
    {
        await _writeLock.WaitAsync();
        try
        {
            var current = GetActiveProjectName();
            if (string.Equals(current, name, StringComparison.Ordinal))
            {
                // No-op write. Still touch the file? No — skip the I/O.
                return (true, null);
            }

            await PersistAsync(name);
            _configRoot.Reload();
            _log.LogInformation("Active site → {Name}", name);
            return (true, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to persist active site name");
            return (false, ex.Message);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Where to actually write when persisting. In the default case
    /// (no env-specific override) this is just <c>appsettings.json</c>.
    /// If the user keeps their real config in
    /// <c>appsettings.{Environment}.json</c>, we write there instead so
    /// the change actually wins the merge.
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

    /// <summary>
    /// Write only <c>StatiqProject.ActiveProjectName</c> while preserving
    /// every other top-level key (Logging, AllowedHosts, etc.). If the
    /// active section didn't exist before, we still create it; if it
    /// did, we replace just that key.
    /// </summary>
    private async Task PersistAsync(string name)
    {
        var targetPath = GetEffectiveConfigPath();
        var existing = await ReadFileRootAsync(targetPath);
        var dir = Path.GetDirectoryName(targetPath) ?? ".";
        Directory.CreateDirectory(dir);

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            w.WriteStartObject();

            if (existing.ValueKind == JsonValueKind.Object)
            {
                bool wroteActive = false;
                foreach (var prop in existing.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "StatiqProject", StringComparison.Ordinal))
                    {
                        // Replace the whole StatiqProject section with just the active name.
                        w.WriteStartObject("StatiqProject");
                        w.WriteString("ActiveProjectName", name);
                        w.WriteEndObject();
                        wroteActive = true;
                    }
                    else
                    {
                        prop.WriteTo(w);
                    }
                }
                if (!wroteActive)
                {
                    w.WriteStartObject("StatiqProject");
                    w.WriteString("ActiveProjectName", name);
                    w.WriteEndObject();
                }
            }
            else
            {
                // Existing file is missing/empty/invalid → fresh skeleton.
                w.WriteStartObject("StatiqProject");
                w.WriteString("ActiveProjectName", name);
                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        var tmp = targetPath + ".tmp";
        var text = new UTF8Encoding(false).GetString(ms.ToArray()).TrimEnd('\r', '\n') + "\n";
        await File.WriteAllTextAsync(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, targetPath, overwrite: true);
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
}