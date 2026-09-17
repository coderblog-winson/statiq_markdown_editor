// =====================================================
//  StatiqRunner.cs
//
//  Runs the Statiq.Web Bootstrapper IN-PROCESS (no spawn).
//  Each call is one site, configured via SiteConfig.
//
//  BuildAsync: full pipeline run, writes output/ to disk,
//              returns build status + log lines + per-doc errors.
//  GetActiveSitePathsSync: cheap, synchronous. Reads the current
//              active site name from IConfiguration (or the
//              env-specific override) and returns resolved paths.
//              Used by MarkdownFileService / ImageService to
//              locate files without round-tripping through JSON
//              parse every call.
// =====================================================
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Statiq.App;
using SME.Statiq;
using StatiqMarkdownEditor.Models;

namespace StatiqMarkdownEditor.Services;

public class StatiqRunner
{
    private readonly string _editorRoot;
    private readonly IOptionsMonitor<StatiqProjectOptions> _opt;
    private readonly ILogger<StatiqRunner> _log;

    public StatiqRunner(IWebHostEnvironment env, IOptionsMonitor<StatiqProjectOptions> opt, ILogger<StatiqRunner> log)
    {
        _editorRoot = env.ContentRootPath;
        _opt = opt;
        _log = log;
    }

    public string EditorRoot => _editorRoot;

    /// <summary>Read a site's config.json + return parsed SiteConfig.</summary>
    public async Task<SiteConfig> LoadConfigAsync(string siteName, CancellationToken ct = default)
    {
        var path = Path.Combine(_editorRoot, "sites", siteName, "config.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Site '{siteName}' config.json not found at {path}");

        await using var stream = File.OpenRead(path);
        var cfg = await JsonSerializer.DeserializeAsync<SiteConfig>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
        if (cfg == null) throw new InvalidDataException($"Invalid config.json for site '{siteName}'");
        cfg.Name = siteName;
        return cfg;
    }

    /// <summary>List all sites in sites/ that have a config.json.</summary>
    public IEnumerable<string> ListSites()
    {
        var dir = Path.Combine(_editorRoot, "sites");
        if (!Directory.Exists(dir)) yield break;
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (File.Exists(Path.Combine(sub, "config.json")))
                yield return Path.GetFileName(sub);
        }
    }

    /// <summary>
    /// Resolve the active site's paths synchronously from the merged
    /// IConfiguration (which already wins env-specific overrides).
    /// Returns null when no site is active or the config is missing.
    ///
    /// We re-parse on every call rather than caching: the file is tiny
    /// (~200 bytes) and this method is only called on user actions,
    /// not in a render loop. Cache invalidation after a SetActive
    /// would add complexity for no measurable benefit.
    /// </summary>
    public SitePaths? GetActiveSitePathsSync()
    {
        var name = _opt.CurrentValue.ActiveProjectName;
        if (string.IsNullOrEmpty(name)) return null;

        var cfgPath = Path.Combine(_editorRoot, "sites", name, "config.json");
        if (!File.Exists(cfgPath)) return null;

        try
        {
            var json = File.ReadAllText(cfgPath);
            var cfg = JsonSerializer.Deserialize<SiteConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (cfg == null) return null;
            cfg.Name = name;
            return cfg.ResolvePaths(_editorRoot);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Run the full Statiq pipeline for a site. Writes to sites/<name>/output/.</summary>
    public async Task<BuildResult> BuildAsync(string siteName, CancellationToken ct = default)
    {
        var cfg = await LoadConfigAsync(siteName, ct);
        var paths = cfg.ResolvePaths(_editorRoot);

        if (!Directory.Exists(paths.InputDir))
            return BuildResult.Fail($"Input dir does not exist: {paths.InputDir}");
        if (!Directory.Exists(paths.ThemeDir))
            return BuildResult.Fail($"Theme dir does not exist: {paths.ThemeDir}");

        Directory.CreateDirectory(paths.OutputDir);
        Directory.CreateDirectory(paths.CacheDir);

        var logCapture = new List<string>();

        // Bootstrapper is the canonical Statiq entry point — same as
        // `dotnet run` in a legacy statiq project. We invoke it in-process.
        // No external dotnet process, no temp working dir.
        // (Bootstrapper 1.0.0-beta.60 doesn't expose SetWorkingDirectory;
        //  we use Directory.SetCurrentDirectory as the equivalent — every
        //  relative path in settings / themes is resolved from cwd.)
        var prevCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(paths.EditorRoot);
        try
        {
            var bootstrapper = Bootstrapper.Factory
                .CreateWeb(Array.Empty<string>())
                .SetOutputPath(paths.OutputDir)
                .ApplyAll(cfg, paths)
                .ConfigureServices(services =>
                {
                    // Capture all logs into logCapture so the caller can stream
                    // them back to the UI modal / store for later.
                    services.AddSingleton<ILoggerProvider>(new ListLoggerProvider(logCapture));
                    services.AddLogging(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information));
                });

            _log.LogInformation("Building site {Site}: input={Input} theme={Theme} output={Output}",
                siteName, paths.InputDir, paths.ThemeDir, paths.OutputDir);

            var exitCode = await bootstrapper.RunAsync();
            return new BuildResult
            {
                Site = siteName,
                ExitCode = exitCode,
                Success = exitCode == 0,
                OutputDir = paths.OutputDir,
                LogLines = logCapture,
                Error = exitCode == 0 ? null : $"Statiq exited with code {exitCode}",
            };
        }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
        }
    }
}

public class BuildResult
{
    public string Site { get; set; } = "";
    public int ExitCode { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string OutputDir { get; set; } = "";
    public List<string> LogLines { get; set; } = new();

    public static BuildResult Fail(string err) => new()
    {
        Success = false,
        Error = err,
    };
}

/// <summary>In-memory ILoggerProvider that appends to a list — for surfacing logs to UI.</summary>
internal class ListLoggerProvider : ILoggerProvider
{
    private readonly List<string> _sink;
    public ListLoggerProvider(List<string> sink) => _sink = sink;
    public ILogger CreateLogger(string categoryName) => new ListLogger(_sink, categoryName);
    public void Dispose() { }

    private class ListLogger : ILogger
    {
        private readonly List<string> _sink;
        private readonly string _cat;
        public ListLogger(List<string> sink, string cat) { _sink = sink; _cat = cat; }
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
            EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = $"[{logLevel}] {_cat}: {formatter(state, exception)}";
            if (exception != null) line += $" -> {exception.Message}";
            _sink.Add(line);
        }

        private class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}