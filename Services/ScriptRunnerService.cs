using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace StatiqMarkdownEditor.Services;

/// <summary>
/// Manages the lifecycle of the two external scripts the editor can launch
/// against a Statiq project:
///
///   * <c>PreviewScriptPath</c> — a long-lived script that builds the
///     site to <c>output/</c> then starts a local HTTP server. We
///     launch it <i>detached</i> (nohup + disown) so the Python server
///     survives even if the .NET editor app restarts. The script
///     writes its own log; we record status separately so the UI can
///     poll "is the server ready?" by TCP-probing the port.
///
///   * <c>DeployScriptPath</c> — a one-shot build + rsync. We
///     <i>track</i> this process so the UI can show running/done and
///     the final exit code. stdout/stderr are appended to a log file
///     as the process runs so the editor can stream it without
///     holding the pipes open.
///
/// Both actions are mutually exclusive per kind: starting a second
/// preview while one is already running returns a 409-equivalent error
/// rather than spawning a duplicate. The deploy script never blocks
/// the editor — calls return as soon as the child process is started.
/// </summary>
public class ScriptRunnerService
{
    private readonly ILogger<ScriptRunnerService> _log;

    // Deploy is tracked via Process (so we know HasExited + exit code).
    private Process? _deployProcess;
    private readonly object _deployLock = new();

    // Preview is detached — the .NET process is NOT the parent of the
    // long-running script. We track "have we ever started one?" and
    // "is the port responding?" instead. _previewLogFile is also the
    // handle the editor reads from to stream output.
    private string? _previewLogFile;
    private int _previewPort;
    private DateTime? _previewStartedAt;
    private readonly object _previewLock = new();

    public ScriptRunnerService(ILogger<ScriptRunnerService> log) => _log = log;

    public string? PreviewLogFile => _previewLogFile;
    public string? DeployLogFile => _deployLogFile;

    private string? _deployLogFile;

    // ----------------------------------------------------------------
    //  Preview
    // ----------------------------------------------------------------

    /// <summary>
    /// Launch the preview script detached. Returns immediately. The
    /// script is expected to: build the site, then start a long-lived
    /// HTTP server (which is the thing we want to survive across
    /// editor restarts).
    /// </summary>
    public (bool ok, string? error) StartPreview(string scriptPath, int port)
    {
        lock (_previewLock)
        {
            if (string.IsNullOrEmpty(scriptPath)) return (false, "Preview script path is not configured. Set it in Settings.");
            if (!File.Exists(scriptPath)) return (false, $"Script not found: {scriptPath}");

            // Try to free the port BEFORE bailing out on "already in
            // use". The previous behaviour was to refuse the call when
            // the port was occupied — which meant a stale preview
            // server (from a previous editor run, or a leftover python
            // http.server after a crash) blocked the user forever even
            // though preview.sh itself has the same recovery logic.
            // Mirror what preview.sh does: lsof the port, SIGTERM the
            // occupants, then SIGKILL the survivors, then re-check.
            var (freed, killed, freeErr) = FreePort(port);
            if (!string.IsNullOrEmpty(freeErr))
            {
                _log.LogWarning("FreePort({Port}) errored: {Err} — will check IsPortOpen anyway", port, freeErr);
            }
            if (killed > 0)
            {
                _log.LogInformation("FreePort({Port}) killed {Killed} stale process(es)", port, killed);
            }

            if (IsPortOpen("127.0.0.1", port))
            {
                return (false, $"Port {port} is still in use after killing {killed} stale process(es) — probably a system process or permission issue. Change the port in Settings or run `lsof -i tcp:{port}` to see who has it.");
            }

            var scriptDir = Path.GetDirectoryName(scriptPath)!;
            var logFile = Path.Combine(scriptDir, ".editor_preview.log");

            try
            {
                // Truncate the log so each run starts fresh.
                File.WriteAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] Spawning preview: bash {scriptPath} {port}\n", Encoding.UTF8);

                // Detach with nohup + disown. The wrapper bash process
                // exits immediately; preview.sh keeps running in the
                // background and is reparented to init. All output is
                // appended to the log file via the wrapper.
                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"-c \"nohup bash '{scriptPath}' {port} >> '{logFile}' 2>&1 </dev/null & disown\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                var wrapper = Process.Start(psi);
                // The wrapper has no useful work to track; it spawns and
                // exits. Give it a moment to fork the real script.
                wrapper?.WaitForExit(2000);

                _previewLogFile = logFile;
                _previewPort = port;
                _previewStartedAt = DateTime.Now;
                _log.LogInformation("Preview started: {Script} (port {Port}, log {Log})", scriptPath, port, logFile);
                return (true, null);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to start preview: {Script}", scriptPath);
                return (false, ex.Message);
            }
        }
    }

    /// <summary>
    /// Free <paramref name="port"/> by killing any process listening on
    /// it. Mirrors the recovery logic in preview.sh so the editor side
    /// can recover from a stale preview server without bouncing the
    /// user with "port already in use". Returns the number of PIDs
    /// killed (may be 0 if the port was already free).
    /// </summary>
    public (bool ok, int killed, string? error) FreePort(int port)
    {
        if (port <= 0) return (true, 0, null);
        if (!IsPortOpen("127.0.0.1", port)) return (true, 0, null);

        int killed = 0;
        try
        {
            // 1. SIGTERM the occupants, give them ~3 s to exit cleanly.
            var termPsi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"pids=$(lsof -ti tcp:{port} 2>/dev/null); if [ -n \\\"$pids\\\" ]; then kill $pids 2>/dev/null; for _ in 1 2 3 4 5 6; do if ! lsof -ti tcp:{port} >/dev/null 2>&1; then break; fi; sleep 0.5; done; echo $pids; fi\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using (var p = Process.Start(termPsi))
            {
                if (p != null)
                {
                    var output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (int.TryParse(line.Trim(), out _)) killed++;
                    }
                }
            }

            // 2. If still listening, SIGKILL the survivors.
            if (IsPortOpen("127.0.0.1", port))
            {
                var killPsi = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"-c \"pids=$(lsof -ti tcp:{port} 2>/dev/null); if [ -n \\\"$pids\\\" ]; then kill -9 $pids 2>/dev/null; sleep 0.5; echo $pids; fi\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                };
                using var p2 = Process.Start(killPsi);
                if (p2 != null)
                {
                    var output = p2.StandardOutput.ReadToEnd();
                    p2.WaitForExit(3000);
                    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (int.TryParse(line.Trim(), out _)) killed++;
                    }
                }
            }

            return (true, killed, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FreePort({Port}) failed", port);
            return (false, killed, ex.Message);
        }
    }

    /// <summary>
    /// Stop the preview by killing the process listening on the
    /// configured port. Also tries to remove the PID file the script
    /// leaves behind, so the next run doesn't see "stale PID".
    /// </summary>
    public (bool ok, string? error, int killed) StopPreview()
    {
        lock (_previewLock)
        {
            if (_previewPort == 0) return (false, "No preview has been started from the editor yet.", 0);

            int killed = 0;
            try
            {
                // Find PIDs listening on the port and SIGKILL them.
                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"-c \"pids=$(lsof -ti tcp:{_previewPort}); if [ -n \\\"$pids\\\" ]; then kill -9 $pids 2>/dev/null; echo $pids; fi\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    var output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (int.TryParse(line.Trim(), out _)) killed++;
                    }
                }

                // Also nuke the script's PID file in case the port
                // happens to be free but the script wrote a stale PID.
                var scriptDir = Path.GetDirectoryName(_previewLogFile ?? string.Empty);
                if (!string.IsNullOrEmpty(scriptDir))
                {
                    var pidFile = Path.Combine(scriptDir, ".preview_server.pid");
                    if (File.Exists(pidFile))
                    {
                        try
                        {
                            var pidStr = (File.ReadAllText(pidFile) ?? "").Trim();
                            if (int.TryParse(pidStr, out var pid))
                            {
                                try { Process.GetProcessById(pid).Kill(entireProcessTree: true); killed++; }
                                catch { /* already gone */ }
                            }
                        }
                        catch { /* best effort */ }
                        try { File.Delete(pidFile); } catch { /* best effort */ }
                    }
                }

                if (_previewLogFile != null)
                {
                    try { File.AppendAllText(_previewLogFile, $"[{DateTime.Now:HH:mm:ss}] Stopped by editor (killed {killed} pid(s))\n", Encoding.UTF8); } catch { }
                }

                _previewLogFile = null;
                _previewPort = 0;
                _previewStartedAt = null;
                _log.LogInformation("Preview stopped ({Killed} pid(s))", killed);
                return (true, null, killed);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to stop preview");
                return (false, ex.Message, killed);
            }
        }
    }

    public (bool running, bool serverReady, int port, string? logFile, DateTime? startedAt) PreviewStatus()
    {
        lock (_previewLock)
        {
            var ready = _previewPort > 0 && IsPortOpen("127.0.0.1", _previewPort);
            return (running: _previewLogFile != null, serverReady: ready, port: _previewPort, logFile: _previewLogFile, startedAt: _previewStartedAt);
        }
    }

    // ----------------------------------------------------------------
    //  Deploy
    // ----------------------------------------------------------------

    public (bool ok, string? error) StartDeploy(string scriptPath)
    {
        lock (_deployLock)
        {
            if (_deployProcess is { HasExited: false })
            {
                return (false, "Deploy is already running. Wait for it to finish.");
            }
            if (string.IsNullOrEmpty(scriptPath)) return (false, "Deploy script path is not configured. Set it in Settings.");
            if (!File.Exists(scriptPath)) return (false, $"Script not found: {scriptPath}");

            var scriptDir = Path.GetDirectoryName(scriptPath)!;
            var logFile = Path.Combine(scriptDir, ".editor_deploy.log");
            // Truncate previous run's log
            File.WriteAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] Spawning deploy: bash {scriptPath}\n", Encoding.UTF8);

            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"bash '{scriptPath}' >> '{logFile}' 2>&1; echo $? > '{logFile}.exit'\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = scriptDir,
            };

            try
            {
                // For deploy we let bash handle the redirect so the
                // child inherits a clean stdout/stderr (the wrapper
                // is what writes the log). The wrapper exits as soon
                // as the child finishes; we poll the .exit file for
                // the exit code.
                var wrapper = Process.Start(psi);
                if (wrapper == null) return (false, "Failed to start deploy process.");
                _deployProcess = wrapper;
                _deployLogFile = logFile;
                _log.LogInformation("Deploy started: {Script} (wrapper pid {Pid}, log {Log})", scriptPath, wrapper.Id, logFile);
                return (true, null);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to start deploy: {Script}", scriptPath);
                return (false, ex.Message);
            }
        }
    }

    public (bool running, int? exitCode, string? logFile) DeployStatus()
    {
        lock (_deployLock)
        {
            int? code = null;
            var p = _deployProcess;
            if (p != null)
            {
                try
                {
                    if (p.HasExited) code = p.ExitCode;
                }
                catch { /* process object disposed */ }
            }
            // Also read the wrapper's `.exit` sidecar file — it has
            // the child's exit code even before the .NET Process
            // object notices the wrapper has exited.
            if (code == null && _deployLogFile != null)
            {
                var sidecar = _deployLogFile + ".exit";
                if (File.Exists(sidecar))
                {
                    var t = (File.ReadAllText(sidecar) ?? "").Trim();
                    if (int.TryParse(t, out var c)) code = c;
                }
            }
            return (running: p is { HasExited: false }, exitCode: code, logFile: _deployLogFile);
        }
    }

    // ----------------------------------------------------------------
    //  Log helpers
    // ----------------------------------------------------------------

    /// <summary>
    /// Read the last <paramref name="tailLines"/> lines from the log
    /// file. We use a simple "skip N-1 newlines" trick so we don't
    /// have to load the whole file on every poll.
    /// </summary>
    public string ReadLog(string action, int tailLines = 200)
    {
        string? logFile = action switch
        {
            "preview" => _previewLogFile,
            "deploy" => _deployLogFile,
            _ => null,
        };
        if (string.IsNullOrEmpty(logFile) || !File.Exists(logFile)) return "";
        try
        {
            // Read all lines; for typical log sizes (< 1 MB) this is
            // fine. If a run is producing huge logs, switch to a
            // ring-buffer or seek from the end.
            var all = File.ReadAllLines(logFile, Encoding.UTF8);
            if (all.Length <= tailLines) return string.Join("\n", all);
            return string.Join("\n", all, all.Length - tailLines, tailLines);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read log file: {Log}", logFile);
            return $"<error reading log: {ex.Message}>";
        }
    }

    private static bool IsPortOpen(string host, int port)
    {
        if (port <= 0) return false;
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            return task.Wait(500) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
