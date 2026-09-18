using Microsoft.Extensions.Options;
using StatiqMarkdownEditor.Auth;
using StatiqMarkdownEditor.Models;
using StatiqMarkdownEditor.Pages;
using StatiqMarkdownEditor.Services;
using SME.Statiq;

// ---------- CLI subcommand: --init-auth ----------
//
// Generates Auth/auth.json with a fresh PBKDF2 hash for a password
// prompted from stdin. Bails out before starting the web host so the
// auth.json is the only side effect.
//
// Usage:
//   dotnet run -- --init-auth
//   # then type the password, press Enter, repeat.
if (args.Contains("--init-auth"))
{
    var code = InitAuthCommand.Run(args);
    Environment.Exit(code);
}

// Detach from stdin so Statiq's Bootstrapper doesn't start a ConsoleListener
// task that reads stdin. When the user hits Ctrl+C in their terminal, the
// Kestrel host's shutdown waits on every running IHostedService / pending
// task before returning — and Statiq's ConsoleListener holds an async
// read on stdin that gets cancelled in a way that throws
// ObjectDisposedException. The exception itself is harmless but it
// stalls the shutdown long enough that the process appears unresponsive
// (you have to pkill it from another terminal).
//
// Redirecting stdin to /dev/null is the cleanest fix — Statiq's
// Bootstrapper.RunAsync() checks Console.IsInputRedirected / the
// underlying ConsoleListener guards on a non-empty stdin and bails out
// without registering the listener task.
try
{
    Console.SetIn(TextReader.Null);
}
catch
{
    // Some hosts (Electron shell, ASP.NET test server) don't allow
    // redirecting stdin. Swallow — the alternative (no fix) is worse
    // than the symptom (occasional Ctrl+C stall on real terminals).
}

// Decide content root BEFORE CreateBuilder. ASP.NET Core 8+ refuses to
// let you change ContentRoot via builder.WebHost.UseContentRoot after
// CreateBuilder — you have to pass it via WebApplicationOptions. (Throws
// NotSupportedException at runtime otherwise.)
//
// Why we override: single-file publish on macOS resolves ContentRoot to
// the *original* source-project root (via AppContext.BaseDirectory), not
// the packaged executable's directory. For the packaged .app case we want
// ContentRoot to be the .app's `Contents/Resources/server/` directory so
// that wwwroot/, appsettings.json, Auth/, sites/, themes/ all resolve
// relative to the actual install location — NOT the source tree on a
// developer's machine.
//
// Heuristic: if `wwwroot/` exists next to the running exe, we are in a
// packaged layout (Electron's main.js sets `cwd: path.dirname(exe)` and
// the extraResources rule copies the whole dist/server/ tree alongside
// the binary). Otherwise fall back to AppContext.BaseDirectory which is
// the right answer for `dotnet run` / dev loop.
var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
var resolvedContentRoot =
    !string.IsNullOrEmpty(exeDir) && Directory.Exists(Path.Combine(exeDir, "wwwroot"))
        ? exeDir
        : AppContext.BaseDirectory;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = resolvedContentRoot,
});

// Tight shutdown timeout. Default is 30 seconds, which means a stuck
// background task (e.g. Statiq's in-process ConsoleListener holding on
// to a stdin read after Ctrl+C) would keep the editor unresponsive for
// half a minute before the host force-exits. 3 seconds is long enough
// for a clean shutdown of all our own services and short enough that
// the user doesn't get stuck wondering if Ctrl+C did anything.
builder.Services.Configure<HostOptions>(opts =>
{
    opts.ShutdownTimeout = TimeSpan.FromSeconds(3);
});

builder.Services.AddRazorPages();
builder.Services.Configure<StatiqProjectOptions>(
    builder.Configuration.GetSection("StatiqProject"));
builder.Services.Configure<AuthOptions>(
    builder.Configuration.GetSection("Auth"));
builder.Services.AddSingleton<FrontmatterService>();
builder.Services.AddSingleton<MarkdownFileService>();
builder.Services.AddSingleton<ImageService>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<StatiqRunner>();
builder.Services.AddSingleton<ScriptRunnerService>();
builder.Services.AddSingleton<AuthService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.UseStatusCodePages("text/plain", "Status: {0}");

// Auth gate. When Auth.Enabled=true, this rejects every request that
// doesn't carry a valid session cookie (except the /Login page and the
// /api/auth/* endpoints). When disabled (the local-tool default), it's
// a no-op. See Auth/RequireAuthMiddleware.cs.
app.UseMiddleware<RequireAuthMiddleware>();

// ---------- Minimal API: posts ----------
//
// All file-IO endpoints are scoped to the currently-active site, which
// MarkdownFileService / ImageService resolve via StatiqRunner.GetActiveSitePathsSync.
// Switch the active site via /api/projects/activate.
app.MapGet("/api/posts", (HttpContext ctx, MarkdownFileService svc) =>
{
    var q = ctx.Request.Query;
    int page = int.TryParse(q["page"], out var p) && p > 0 ? p : 1;
    int pageSize = int.TryParse(q["pageSize"], out var ps) && ps > 0 && ps <= 100 ? ps : 12;

    var all = svc.ListPosts();
    if (!string.IsNullOrWhiteSpace(q["category"]))
        all = all.Where(p => string.Equals(p.Category, q["category"], StringComparison.OrdinalIgnoreCase)).ToList();
    if (!string.IsNullOrWhiteSpace(q["q"]))
    {
        var needle = q["q"]!.ToString();
        all = all.Where(p =>
            (p.Title?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ||
            p.RelativePath.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    var total = all.Count;
    var totalPages = total == 0 ? 0 : (total + pageSize - 1) / pageSize;
    // If the caller asked for a page past the end, clamp to the last
    // real page rather than returning an empty array — feels less
    // broken when the user is paging back from a higher page after
    // a delete narrows the result set.
    if (totalPages > 0 && page > totalPages) page = totalPages;
    var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();

    return Results.Ok(new
    {
        items,
        total,
        page,
        pageSize,
        totalPages,
    });
});

app.MapGet("/api/posts/{*path}", (string path, MarkdownFileService svc) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "path required" });
    var (ok, post, err) = svc.ReadPost(path);
    return ok ? Results.Ok(post) : Results.NotFound(new { error = err });
});

app.MapPut("/api/posts/{*path}", async (string path, HttpRequest req, MarkdownFileService svc) =>
{
    var body = await req.ReadFromJsonAsync<PostContent>();
    if (body is null || body.Content is null)
        return Results.BadRequest(new { error = "content body required" });
    var (ok, err) = svc.WritePost(path, body);
    return ok ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = err });
});

app.MapDelete("/api/posts/{*path}", (string path, MarkdownFileService svc) =>
{
    var (ok, err) = svc.DeletePost(path);
    return ok ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = err });
});

app.MapPost("/api/posts", (NewPostRequest body, MarkdownFileService svc) =>
{
    if (body is null || string.IsNullOrWhiteSpace(body.Title))
        return Results.BadRequest(new { error = "title required" });
    var (ok, relativePath, err) = svc.CreatePost(body);
    return ok
        ? Results.Ok(new { ok = true, path = relativePath })
        : Results.BadRequest(new { error = err });
});

app.MapPost("/api/posts/rename", (HttpContext ctx, RenameRequest body, MarkdownFileService svc) =>
{
    var path = ctx.Request.Query["path"].ToString();
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "path query param required" });
    if (body is null || string.IsNullOrWhiteSpace(body.NewSlug))
        return Results.BadRequest(new { error = "newSlug required" });
    var (ok, newPath, err) = svc.RenamePost(path, body.NewSlug);
    return ok
        ? Results.Ok(new { ok = true, path = newPath })
        : Results.BadRequest(new { error = err });
});

app.MapGet("/api/categories", (MarkdownFileService svc) => Results.Ok(svc.ListCategories()));

// Serve image files under /images/{*path} by reading them directly from
// the active site's input/images/ directory. This lets the live preview
// pane render <img src="/images/2026-09/foo.webp"> without a Statiq build.
app.MapGet("/images/{*path}", (string path, StatiqRunner runner) =>
{
    if (string.IsNullOrEmpty(path)) return Results.BadRequest(new { error = "path required" });
    var paths = runner.GetActiveSitePathsSync();
    if (paths == null) return Results.NotFound();
    var trimmed = path.TrimStart('/');
    var imagesRoot = Path.Combine(paths.InputDir, "images");
    var full = Path.GetFullPath(Path.Combine(imagesRoot, trimmed.Replace('/', Path.DirectorySeparatorChar)));
    var rootFull = Path.GetFullPath(imagesRoot);
    if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        && !string.Equals(full, rootFull, StringComparison.Ordinal))
    {
        return Results.NotFound();
    }
    if (!File.Exists(full)) return Results.NotFound();
    var ext = Path.GetExtension(full).ToLowerInvariant();
    var contentType = ext switch
    {
        ".webp" => "image/webp",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };
    return Results.File(full, contentType);
});

app.MapPut("/api/images", async (HttpRequest req, ImageService imgSvc) =>
{
    if (!req.HasFormContentType)
        return Results.BadRequest(new { error = "multipart/form-data required" });
    IFormCollection form;
    try
    {
        form = await req.ReadFormAsync();
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = "could not read form: " + ex.Message });
    }
    var file = form.Files["file"];
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "file field required" });

    // Where the image lands: explicit `targetDate` form field > "now".
    // We deliberately ignore the post's frontmatter Date — the user wants
    // images archived by the time they were *pasted*, not by when the post
    // claims to be published. Override with targetDate when needed.
    DateTime? postDate = null;
    var dateOverride = form["targetDate"].ToString();
    if (string.Equals(dateOverride, "now", StringComparison.OrdinalIgnoreCase))
    {
        postDate = null;
    }
    else if (!string.IsNullOrEmpty(dateOverride) && DateTime.TryParse(dateOverride, out var parsed))
    {
        postDate = parsed;
    }

    var (ok, resp, err) = imgSvc.SaveWebp(file.OpenReadStream(), file.FileName, postDate, file.Length);
    return ok ? Results.Ok(resp) : Results.BadRequest(new { error = err });
});

// ---------- Settings ----------
//
// The settings page is now read-only: sites are auto-discovered from
// sites/<name>/config.json. The only stateful thing the user controls
// is which site is active. ListSites + active = the entire /api/settings
// response. /api/projects/activate writes to appsettings[.Development].json.
app.MapGet("/api/settings", (StatiqRunner runner, SettingsService svc) =>
{
    var activeName = svc.GetActiveProjectName();
    var sites = runner.ListSites()
        .Select(n => DescribeSite(runner, n))
        .ToList();
    return Results.Ok(new
    {
        sites,
        activeProjectName = activeName,
    });
});

// Lightweight "switch active site" endpoint.
app.MapPost("/api/projects/activate", async (HttpContext ctx, SettingsService svc) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<ActivateProjectRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Name))
        return Results.BadRequest(new { error = "name required" });
    var (ok, err) = await svc.SetActiveProjectAsync(body.Name);
    return ok ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = err });
});

// Lightweight dropdown list (used by _Layout.cshtml's header dropdown).
app.MapGet("/api/projects", (StatiqRunner runner, SettingsService svc) =>
{
    var sites = runner.ListSites().Select(n => new { name = n }).ToList();
    return Results.Ok(new
    {
        sites,
        activeProjectName = svc.GetActiveProjectName(),
    });
});

// ---------- Editor-managed Statiq sites (in-process Bootstrapper) ----------
//
// These endpoints drive the editor's own copy of Statiq.Web. The site
// list is whatever lives in sites/<name>/config.json; everything (theme
// path, host, analytics IDs) is read from there.
static object DescribeSite(StatiqRunner runner, string name)
{
    try
    {
        var cfg = runner.LoadConfigAsync(name).GetAwaiter().GetResult();
        var paths = cfg.ResolvePaths(runner.EditorRoot);
        var postsDir = Path.Combine(paths.InputDir, "posts");
        return new
        {
            name,
            theme = cfg.Theme,
            host = cfg.Host,
            inputExists = Directory.Exists(paths.InputDir),
            themeExists = Directory.Exists(paths.ThemeDir),
            outputExists = Directory.Exists(paths.OutputDir),
            postCount = Directory.Exists(postsDir)
                ? Directory.EnumerateFiles(postsDir, "*.md", SearchOption.AllDirectories).Count()
                : 0,
        };
    }
    catch (Exception ex)
    {
        return new { name, error = ex.Message };
    }
}

app.MapGet("/api/sites", (StatiqRunner runner) =>
{
    var sites = runner.ListSites().Select(n => DescribeSite(runner, n)).ToList();
    return Results.Ok(new { sites });
});

app.MapGet("/api/sites/{name}/config", async (string name, StatiqRunner runner) =>
{
    try
    {
        var cfg = await runner.LoadConfigAsync(name);
        return Results.Ok(cfg);
    }
    catch (FileNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// In-process build. Synchronous (blocks until Statiq finishes) — the
// caller polls /api/sites/{name}/build/log for progress, just like the
// legacy deploy script flow.
app.MapPost("/api/sites/{name}/build", async (string name, StatiqRunner runner) =>
{
    try
    {
        var result = await runner.BuildAsync(name);
        return result.Success
            ? Results.Ok(new { ok = true, outputDir = result.OutputDir, exitCode = result.ExitCode, lines = result.LogLines.Count })
            : Results.BadRequest(new { ok = false, error = result.Error ?? "build failed", exitCode = result.ExitCode, log = result.LogLines });
    }
    catch (FileNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// ---------- Per-site sh scripts (Preview / Deploy / Git Sync) ----------
//
// These endpoints spawn the per-site sh scripts declared in each site's
// config.json (PreviewScript / DeployScript / GitSyncScript). The
// runner is responsible for port recovery (Preview only), stdout/err
// capture, and exit code tracking.

static async Task<IResult> RunSiteScript(string name, ScriptKind kind, StatiqRunner runner, ScriptRunnerService svc, HttpContext ctx)
{
    SiteConfig cfg;
    try { cfg = await runner.LoadConfigAsync(name); }
    catch (FileNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

    var (ok, err, result) = svc.Start(name, kind, cfg);
    if (!ok) return Results.BadRequest(new { error = err });
    return Results.Ok(new
    {
        ok = true,
        kind = kind.ToString().ToLowerInvariant(),
        siteName = result!.SiteName,
        pid = result.Pid,
        logFile = result.LogFile,
        port = kind == ScriptKind.Preview ? cfg.PreviewPort : 0,
        previewUrl = kind == ScriptKind.Preview ? $"http://127.0.0.1:{cfg.PreviewPort}" : null,
    });
}

app.MapPost("/api/sites/{name}/preview", (string name, StatiqRunner runner, ScriptRunnerService svc, HttpContext ctx) =>
    RunSiteScript(name, ScriptKind.Preview, runner, svc, ctx));

app.MapPost("/api/sites/{name}/deploy", (string name, StatiqRunner runner, ScriptRunnerService svc, HttpContext ctx) =>
    RunSiteScript(name, ScriptKind.Deploy, runner, svc, ctx));

app.MapPost("/api/sites/{name}/git-sync", (string name, StatiqRunner runner, ScriptRunnerService svc, HttpContext ctx) =>
    RunSiteScript(name, ScriptKind.GitSync, runner, svc, ctx));

app.MapPost("/api/scripts/{kind}/stop", (string kind, ScriptRunnerService svc, StatiqRunner runner) =>
{
    if (!Enum.TryParse<ScriptKind>(kind, ignoreCase: true, out var parsed))
        return Results.BadRequest(new { error = $"unknown script kind: {kind}" });
    // For Preview stop we also need the cfg to free the port. Look it up
    // from the currently-running process's site name.
    var status = svc.Status(parsed);
    SiteConfig? cfg = null;
    if (status.Running && !string.IsNullOrEmpty(status.SiteName))
    {
        try { cfg = runner.LoadConfigAsync(status.SiteName).GetAwaiter().GetResult(); }
        catch { /* fall through with null cfg — only affects Preview port cleanup */ }
    }
    var (ok, err, killed) = svc.Stop(parsed, cfg);
    return ok ? Results.Ok(new { ok, killed }) : Results.BadRequest(new { error = err });
});

app.MapGet("/api/scripts/{kind}/status", (string kind, ScriptRunnerService svc) =>
{
    if (!Enum.TryParse<ScriptKind>(kind, ignoreCase: true, out var parsed))
        return Results.BadRequest(new { error = $"unknown script kind: {kind}" });
    var s = svc.Status(parsed);
    return Results.Ok(new
    {
        kind = s.Kind.ToString().ToLowerInvariant(),
        running = s.Running,
        exitCode = s.ExitCode,
        pid = s.Pid,
        siteName = s.SiteName,
        logFile = s.LogFile,
        startedAt = s.StartedAt,
    });
});

app.MapGet("/api/scripts/{kind}/log", (string kind, HttpContext ctx, ScriptRunnerService svc) =>
{
    if (!Enum.TryParse<ScriptKind>(kind, ignoreCase: true, out var parsed))
        return Results.BadRequest(new { error = $"unknown script kind: {kind}" });
    var tail = 500;
    if (int.TryParse(ctx.Request.Query["tail"], out var tn) && tn > 0 && tn <= 5000) tail = tn;
    return Results.Ok(new { text = svc.ReadLog(parsed, tail) });
});

// ---------- Auth endpoints ----------
//
// /api/auth/status — probe: is auth on, and am I signed in? The Login
//   page uses this to decide whether to show the form.
// /api/auth/login  — credential check. Accepts form-urlencoded (the
//   Login page's fetch call) or JSON {password, csrf, returnUrl}. On
//   success sets the session cookie and returns 200 + returnUrl.
// /api/auth/logout — clears the session cookie. POST-by-design (state
//   mutation); CSRF protected via the same _csrf field.
// /api/auth/change-password — rotate the admin password (triple-gated:
//   signed-in session cookie + CSRF token + correct current password).
//   On success the in-memory HMAC key is cleared, invalidating every
//   existing session — the user is bounced back to /Login.
app.MapGet("/api/auth/status", (HttpContext ctx, AuthService auth) =>
{
    bool authenticated = ctx.Request.Cookies.TryGetValue(AuthService.SessionCookieName, out var token)
        && auth.TryValidateSessionToken(token, out _);
    return Results.Ok(new { enabled = auth.IsEnabled, authenticated });
});

app.MapPost("/api/auth/login", async (HttpContext ctx, AuthService auth) =>
{
    if (!auth.IsEnabled)
        return Results.Ok(new { ok = true, message = "auth disabled — no login required" });

    // Read body once. Accept both form-urlencoded (Login page fetch) and JSON.
    string? password = null;
    string? csrf = null;
    string? returnUrl = null;

    if (ctx.Request.HasFormContentType)
    {
        var form = await ctx.Request.ReadFormAsync();
        password = form["password"].ToString();
        csrf = form["_csrf"].ToString();
        returnUrl = form["returnUrl"].ToString();
    }
    else
    {
        var body = await ctx.Request.ReadFromJsonAsync<LoginRequest>();
        password = body?.Password;
        csrf = body?.Csrf;
        returnUrl = body?.ReturnUrl;
    }

    if (string.IsNullOrEmpty(password))
        return Results.BadRequest(new { error = "password required" });

    // CSRF check: form must echo the token issued in the Csrf cookie.
    if (!ctx.Request.Cookies.TryGetValue(AuthService.CsrfCookieName, out var cookieCsrf)
        || string.IsNullOrEmpty(csrf)
        || !auth.CsrfTokensMatch(cookieCsrf, csrf))
    {
        // Diagnostic log — helps debug browser caching / cookie-blocking
        // extension / same-origin policy issues without exposing the
        // tokens in production logs (only lengths + first/last 4 chars).
        var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
        string fingerprint(string? s) => string.IsNullOrEmpty(s)
            ? "(missing)"
            : $"{s.Length} chars [{s[..Math.Min(4, s.Length)]}…{s[^Math.Min(4, s.Length)..]}]";
        logger.LogWarning(
            "CSRF mismatch: cookie={Cookie} form={Form} password={Pw} ua={UA}",
            fingerprint(cookieCsrf),
            fingerprint(csrf),
            fingerprint(password),
            ctx.Request.Headers.UserAgent.ToString());
        return Results.BadRequest(new { error = "csrf token missing or mismatched" });
    }

    if (!auth.VerifyPassword(password))
        return Results.Json(new { error = "incorrect password" }, statusCode: 401);

    var token = auth.IssueSessionToken();
    var cfg = auth.LoadConfig();
    var lifetime = TimeSpan.FromHours(cfg?.SessionExpiryHours ?? 24);
    ctx.Response.Cookies.Append(
        AuthService.SessionCookieName, token, auth.BuildSessionCookieOptions(ctx, lifetime));
    // Refresh CSRF cookie lifetime so it doesn't expire mid-session.
    ctx.Response.Cookies.Append(
        AuthService.CsrfCookieName,
        auth.GenerateCsrfToken(),
        auth.BuildCsrfCookieOptions(lifetime));

    return Results.Ok(new { ok = true, returnUrl = string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl });
});

app.MapPost("/api/auth/logout", (HttpContext ctx) =>
{
    ctx.Response.Cookies.Delete(AuthService.SessionCookieName);
    ctx.Response.Cookies.Delete(AuthService.CsrfCookieName);
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/auth/change-password", async (HttpContext ctx, AuthService auth, ILoggerFactory lf) =>
{
    if (!auth.IsEnabled) return Results.NotFound();

    // Must be signed in (RequireAuthMiddleware already blocks
    // unauthenticated /api/*, but be defensive in case the path
    // becomes allow-listed later).
    if (!ctx.Request.Cookies.TryGetValue(AuthService.SessionCookieName, out var sessionToken)
        || !auth.TryValidateSessionToken(sessionToken, out _))
    {
        return Results.Json(new { error = "sign in required" }, statusCode: 401);
    }

    var body = await ctx.Request.ReadFromJsonAsync<ChangePasswordRequest>();
    if (body is null)
        return Results.BadRequest(new { error = "invalid request body" });

    // CSRF check
    if (!ctx.Request.Cookies.TryGetValue(AuthService.CsrfCookieName, out var cookieCsrf)
        || string.IsNullOrEmpty(body.Csrf)
        || !auth.CsrfTokensMatch(cookieCsrf, body.Csrf))
    {
        return Results.BadRequest(new { error = "csrf token missing or mismatched" });
    }

    var logger = lf.CreateLogger("change-password");
    var result = await auth.ChangePasswordAsync(body.CurrentPassword ?? "", body.NewPassword ?? "");
    return result switch
    {
        AuthService.ChangePasswordResult.Ok => Results.Ok(new
        {
            ok = true,
            message = "password changed — please sign in again",
        }),
        AuthService.ChangePasswordResult.WrongCurrent => Results.Json(
            new { error = "current password is incorrect" }, statusCode: 401),
        AuthService.ChangePasswordResult.WeakNewPassword => Results.BadRequest(
            new { error = $"new password must be at least {AuthService.MinPasswordLength} characters" }),
        AuthService.ChangePasswordResult.SameAsCurrent => Results.BadRequest(
            new { error = "new password must differ from the current one" }),
        AuthService.ChangePasswordResult.NotReady => Results.Json(
            new { error = "auth.json is missing or unreadable" }, statusCode: 503),
        AuthService.ChangePasswordResult.WriteFailed => Results.Json(
            new { error = "failed to write auth.json" }, statusCode: 500),
        _ => Results.BadRequest(new { error = "unknown error" }),
    };
});

app.MapRazorPages();

// ---------- IP guard ----------
//
// Footgun protection: if the operator binds the editor to a non-loopback
// address (e.g. 0.0.0.0 to expose it to a LAN), authentication MUST be
// enabled — otherwise anyone who can reach the port can delete posts.
// The check runs once at startup so misconfigurations fail loudly.
//
// We can't trust app.Urls here (it's empty until Kestrel binds, which
// happens inside app.Run()). Instead, pull the binding hint from the
// same sources Kestrel consults: --urls CLI flag, ASPNETCORE_URLS env
// var, and launchSettings.json (dev only).
{
    var bindUrls = new List<string>();

    // 1. Command line: --urls <url>
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--urls") bindUrls.Add(args[i + 1]);
    }

    // 2. ASPNETCORE_URLS env var (semicolon-separated in ASP.NET Core 6+)
    var envUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
    if (!string.IsNullOrEmpty(envUrls))
    {
        bindUrls.AddRange(envUrls.Split(';', StringSplitOptions.RemoveEmptyEntries));
    }

    // 3. launchSettings.json (dev only — see Properties/launchSettings.json
    //    applicationUrl). If we find one, use its applicationUrl.
    var launchProfile = Environment.GetEnvironmentVariable("DOTNET_LAUNCH_PROFILE");
    if (!string.IsNullOrEmpty(launchProfile))
    {
        var launchSettingsPath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Properties", "launchSettings.json");
        if (File.Exists(launchSettingsPath))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(launchSettingsPath));
                if (doc.RootElement.TryGetProperty("profiles", out var profiles)
                    && profiles.TryGetProperty(launchProfile, out var profile)
                    && profile.TryGetProperty("applicationUrl", out var appUrl))
                {
                    bindUrls.AddRange(appUrl.GetString()?.Split(';', StringSplitOptions.RemoveEmptyEntries)
                                       ?? Array.Empty<string>());
                }
            }
            catch { /* ignore — dev-only fallback */ }
        }
    }

    bool bindsPublicly = bindUrls.Any(u =>
    {
        var uri = u.Replace("http://", "").Replace("https://", "");
        var host = uri.Split(':')[0];
        return host != "127.0.0.1" && host != "localhost" && host != "::1";
    });

    var authOptsMonitor = app.Services.GetRequiredService<IOptionsMonitor<AuthOptions>>();
    if (bindsPublicly && !authOptsMonitor.CurrentValue.Enabled)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("FATAL: Server is bound to a public address but Auth.Enabled is false.");
        Console.Error.WriteLine("       Refusing to start without authentication.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  Detected binding:");
        foreach (var u in bindUrls) Console.Error.WriteLine($"    {u}");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  Fix one of:");
        Console.Error.WriteLine("    - Set --urls to 127.0.0.1:5070 (local-only), OR");
        Console.Error.WriteLine("    - Set Auth.Enabled=true in appsettings.json AND create Auth/auth.json");
        Console.Error.WriteLine("      via `dotnet run --init-auth`.");
        Console.Error.WriteLine();
        return;
    }
}

// Safety net for "Ctrl+C leaves the process hanging". Root cause:
// Statiq's in-process Bootstrapper starts a ConsoleListener task that
// reads stdin in a background loop. When the host's CancellationTokenSource
// gets disposed during shutdown, the listener throws
// ObjectDisposedException. The exception is harmless but stalls
// GenericHost's shutdown long enough that users think the process is
// frozen and have to `pkill -9` from another terminal.
//
// Two backstops, wired BEFORE app.Run() so the IServiceProvider is
// still alive (GetRequiredService throws ObjectDisposedException if
// called after Run() returns — we hit that the hard way once):
//   1. ApplicationStopping hook fires a 3-second-timer force-exit.
//      The host still runs its graceful shutdown — if it completes first
//      we dispose the timer.
//   2. AppDomain UnhandledException catches the post-shutdown
//      ObjectDisposedException so it doesn't print a scary stack trace.
{
    var lifetime = app.Lifetime;
    System.Threading.Timer? shutdownTimer = null;
    shutdownTimer = new System.Threading.Timer(_ =>
    {
        Console.Error.WriteLine("[editor] shutdown stalled > 3s, force-exiting");
        Environment.Exit(0);
    });
    lifetime.ApplicationStopping.Register(() =>
    {
        shutdownTimer?.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
    });
    lifetime.ApplicationStopped.Register(() =>
    {
        shutdownTimer?.Dispose();
    });

    AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
    {
        if (e.ExceptionObject is ObjectDisposedException
            || e.ExceptionObject is AggregateException agg
                && agg.InnerExceptions.All(x => x is ObjectDisposedException))
        {
            Console.Error.WriteLine("[editor] post-shutdown ObjectDisposedException swallowed");
        }
    };
}

app.Run();