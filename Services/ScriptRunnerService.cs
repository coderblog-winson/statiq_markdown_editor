// =====================================================
//  ScriptRunnerService.cs
//
//  Spawns per-site sh scripts (preview.sh / build_deploy.sh /
//  git_sync.sh) as detached bash processes and tracks their state for
//  the UI modal. Each script kind gets its own slot so multiple can
//  run concurrently (e.g. Deploy can start while Preview is still up).
//
//  Behaviour:
//    - Preview  = long-lived bash that builds + starts HTTP server
//    - Deploy   = one-shot bash that builds + rsyncs + git-syncs
//    - GitSync  = one-shot bash that commits + pushes
//
//  All scripts are launched with `nohup bash <abs_path> <args> > <log>
//  2>&1 &` so they survive the editor process and write to a known
//  log file in sites/<name>/scripts/logs/.
//
//  Port recovery (preview only): before launching the preview script,
//  we run the same lsof + kill dance the script itself would do — that
//  way the UI never sees a "port busy" failure when the previous
//  preview was orphaned (parent died, port held by python http.server).
// =====================================================
using System.Diagnostics;
using System.Text;
using SME.Statiq;

namespace StatiqMarkdownEditor.Services;

public enum ScriptKind
{
    Preview,
    Deploy,
    GitSync,
}

public class ScriptRunnerService
{
    private readonly string _editorRoot;
    private readonly StatiqRunner _runner;
    private readonly ILogger<ScriptRunnerService> _log;

    // Per-kind state — only one running process per kind at a time.
    // Multiple kinds can run concurrently (preview + deploy).
    private readonly Dictionary<ScriptKind, RunningScript> _running = new();

    private sealed class RunningScript
    {
        public required ScriptKind Kind { get; init; }
        public required string SiteName { get; init; }
        public required string ScriptPath { get; init; }
        public required Process Process { get; init; }
        public required string LogFile { get; init; }
        public required DateTime StartedAt { get; init; }
    }

    public ScriptRunnerService(
        IWebHostEnvironment env,
        StatiqRunner runner,
        ILogger<ScriptRunnerService> log)
    {
        _editorRoot = env.ContentRootPath;
        _runner = runner;
        _log = log;
    }

    /// <summary>
    /// Launch a script for a site. For Preview kind, also pre-emptively
    /// frees the configured port (lsof + SIGTERM + SIGKILL) so the
    /// user's preview.sh doesn't have to deal with "address already in use".
    /// </summary>
    public (bool ok, string? error, ScriptRunResult? result) Start(
        string siteName, ScriptKind kind, SiteConfig cfg)
    {
        var scriptRel = kind switch
        {
            ScriptKind.Preview => cfg.PreviewScript,
            ScriptKind.Deploy  => cfg.DeployScript,
            ScriptKind.GitSync => cfg.GitSyncScript,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(scriptRel))
            return (false, $"{kind} script not configured for site '{siteName}'", null);

        var siteDir = Path.Combine(_editorRoot, "sites", siteName);
        var scriptPath = Path.IsPathRooted(scriptRel)
            ? scriptRel
            : Path.Combine(siteDir, scriptRel);

        if (!File.Exists(scriptPath))
            return (false, $"script not found on disk: {scriptPath}", null);

        // For preview only: ALWAYS free the port before launching. The
        // user wants "click Preview → server up" even if a previous
        // preview server is still alive (e.g. orphaned after editor
        // restart, or user double-clicked). Kill + restart every time.
        if (kind == ScriptKind.Preview)
        {
            // Build first. preview.sh fails fast (exit 1) when
            // sites/<name>/output/index.html doesn't exist, which the
            // user perceives as "preview button is broken" — but
            // really they just hadn't built yet. Running an in-process
            // build (the editor's own Bootstrapper, fast — no script
            // spawn) before launching the HTTP server turns the
            // Preview button into a single click that always works.
            var buildResult = _runner.BuildAsync(siteName).GetAwaiter().GetResult();
            if (!buildResult.Success)
            {
                return (false,
                    $"preview aborted: build failed ({buildResult.Error ?? "see build log"})",
                    null);
            }

            // If WE have a recorded running process for this kind, kill
            // it first so we don't have two PIDs racing for the port.
            if (_running.TryGetValue(kind, out var prev) && !HasExited(prev.Process))
            {
                try { prev.Process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                _running.Remove(kind);
            }
            // Then make sure the port is free (kills anything left over
            // from a previous orphaned preview).
            var (portOk, _, killed) = FreePort(cfg.PreviewPort);
            if (killed > 0)
                _log.LogInformation("[preview] pre-emptively freed port {Port} (killed {Killed})",
                    cfg.PreviewPort, killed);
        }
        else
        {
            // For one-shot scripts (deploy, git-sync), reject if a previous
            // run of the SAME kind is still active.
            if (_running.TryGetValue(kind, out var active) && !HasExited(active.Process))
            {
                return (false,
                    $"{kind} already running for site '{active.SiteName}' (PID {active.Process.Id})",
                    null);
            }
        }

        // Per-script logs in sites/<name>/scripts/logs/ — they survive
        // across runs so the user can dig back into yesterday's deploy
        // even after the modal closes.
        var logsDir = Path.Combine(siteDir, "scripts", "logs");
        Directory.CreateDirectory(logsDir);
        var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var logFile = Path.Combine(logsDir, $"{kind.ToString().ToLowerInvariant()}-{ts}.log");

        // Args: preview takes the port as $1. Deploy/git-sync take no args.
        var args = kind switch
        {
            ScriptKind.Preview => $"\"{scriptPath}\" {cfg.PreviewPort}",
            _ => $"\"{scriptPath}\"",
        };

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"-c \"{args}\"",
            WorkingDirectory = siteDir,
            RedirectStandardOutput = false, // shell writes directly to log file
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = false,
            Environment =
            {
                // dotnet only available via /usr/local/share/dotnet in user shell
                ["PATH"] = "/usr/local/share/dotnet:/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin",
            },
        };

        // Append `> "$logFile" 2>&1` to the shell -c command so the user
        // can `tail -f` the log file directly. Build it carefully to
        // handle paths with spaces.
        var quotedLog = "\"" + logFile + "\"";
        psi.Arguments = $"-c \"{args} > {quotedLog} 2>&1\"";

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to start {Kind} for {Site}", kind, siteName);
            return (false, $"spawn failed: {ex.Message}", null);
        }
        if (proc == null)
            return (false, "Process.Start returned null", null);

        _running[kind] = new RunningScript
        {
            Kind = kind,
            SiteName = siteName,
            ScriptPath = scriptPath,
            Process = proc,
            LogFile = logFile,
            StartedAt = DateTime.UtcNow,
        };

        _log.LogInformation("Started {Kind} for {Site}: PID={Pid} script={Script} log={Log}",
            kind, siteName, proc.Id, scriptPath, logFile);

        return (true, null, new ScriptRunResult
        {
            Kind = kind,
            SiteName = siteName,
            Pid = proc.Id,
            LogFile = logFile,
            StartedAt = _running[kind].StartedAt,
        });
    }

    /// <summary>
    /// Stop a running script by kind. For Preview: also kill anything
    /// bound to the configured port (since the python http.server inside
    /// the script holds it). For Deploy/GitSync: process only.
    /// </summary>
    public (bool ok, string? error, int killed) Stop(ScriptKind kind, SiteConfig? cfg = null)
    {
        if (!_running.TryGetValue(kind, out var r))
            return (true, null, 0);

        var killed = 0;
        try
        {
            if (!r.Process.HasExited)
            {
                r.Process.Kill(entireProcessTree: true);
                killed++;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Error killing {Kind} PID {Pid}", kind, r.Process.Id);
        }

        // Preview's child python http.server might outlive the script's
        // process group if detached via nohup — kill anything on the port.
        if (kind == ScriptKind.Preview && cfg != null)
        {
            var (_, _, portKilled) = FreePort(cfg.PreviewPort);
            killed += portKilled;
        }

        _running.Remove(kind);
        return (true, null, killed);
    }

    /// <summary>
    /// Lightweight status snapshot for the UI modal.
    /// </summary>
    public ScriptStatusResult Status(ScriptKind kind)
    {
        if (!_running.TryGetValue(kind, out var r))
            return new ScriptStatusResult { Kind = kind, Running = false };

        var exited = HasExited(r.Process);
        return new ScriptStatusResult
        {
            Kind = kind,
            Running = !exited,
            ExitCode = exited ? r.Process.ExitCode : (int?)null,
            Pid = r.Process.Id,
            SiteName = r.SiteName,
            LogFile = r.LogFile,
            StartedAt = r.StartedAt,
        };
    }

    public string ReadLog(ScriptKind kind, int tail = 500)
    {
        if (!_running.TryGetValue(kind, out var r) || !File.Exists(r.LogFile))
            return "";

        // Read last N lines cheaply — open with FileShare.ReadWrite so we
        // don't lock the script's appending writes.
        try
        {
            using var fs = new FileStream(r.LogFile, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var all = sr.ReadToEnd();
            var lines = all.Split('\n');
            if (lines.Length <= tail) return all;
            return string.Join('\n', lines[^tail..]);
        }
        catch
        {
            return "";
        }
    }

    // ----------------------------------------------------------------
    //  Helpers
    // ----------------------------------------------------------------

    private static bool HasExited(Process p)
    {
        try { return p.HasExited; } catch { return true; }
    }

    /// <summary>
    /// Kill anything bound to <paramref name="port"/> (preview only).
    /// Returns (ok, error, killed). Strategy: lsof + SIGTERM → wait 3s →
    /// SIGKILL. Mirrors what preview.sh itself does, so the wrapper
    /// never sees a "port busy" error from the script.
    /// </summary>
    public static (bool ok, string? error, int killed) FreePort(int port)
    {
        try
        {
            // lsof returns empty on success (port free), or PIDs on the port.
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/sbin/lsof",
                Arguments = $"-ti tcp:{port}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var lsof = Process.Start(psi)!;
            var pidText = lsof.StandardOutput.ReadToEnd().Trim();
            lsof.WaitForExit(2000);

            if (string.IsNullOrEmpty(pidText))
                return (true, null, 0);

            var pids = pidText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), out var p) ? p : 0)
                .Where(p => p > 0)
                .ToArray();
            int killed = 0;
            foreach (var pid in pids)
            {
                try { Process.Start(new ProcessStartInfo
                    {
                        FileName = "/bin/kill",
                        Arguments = pid.ToString(),
                        UseShellExecute = false,
                    })!.WaitForExit(2000); killed++; }
                catch { /* ignore */ }
            }

            // Wait up to 3s for graceful exit.
            for (int i = 0; i < 6; i++)
            {
                if (!IsPortBusy(port)) break;
                Thread.Sleep(500);
            }
            if (IsPortBusy(port))
            {
                // Upgrade to SIGKILL.
                foreach (var pid in pids)
                {
                    try { Process.Start(new ProcessStartInfo
                    {
                        FileName = "/bin/kill",
                        Arguments = $"-9 {pid}",
                        UseShellExecute = false,
                    })!.WaitForExit(2000); }
                    catch { /* ignore */ }
                }
                Thread.Sleep(500);
            }

            if (IsPortBusy(port))
                return (false, "port still busy after SIGKILL", killed);
            return (true, null, killed);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, 0);
        }
    }

    private static bool IsPortBusy(int port)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/sbin/lsof",
                Arguments = $"-ti tcp:{port}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi)!;
            var txt = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(1500);
            return !string.IsNullOrEmpty(txt);
        }
        catch
        {
            return false;
        }
    }
}

public class ScriptRunResult
{
    public ScriptKind Kind { get; set; }
    public string SiteName { get; set; } = "";
    public int Pid { get; set; }
    public string LogFile { get; set; } = "";
    public DateTime StartedAt { get; set; }
}

public class ScriptStatusResult
{
    public ScriptKind Kind { get; set; }
    public bool Running { get; set; }
    public int? ExitCode { get; set; }
    public int Pid { get; set; }
    public string SiteName { get; set; } = "";
    public string LogFile { get; set; } = "";
    public DateTime StartedAt { get; set; }
}