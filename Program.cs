using Microsoft.Extensions.Options;
using StatiqMarkdownEditor.Models;
using StatiqMarkdownEditor.Pages;
using StatiqMarkdownEditor.Services;
using SME.Statiq;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.Configure<StatiqProjectOptions>(
    builder.Configuration.GetSection("StatiqProject"));
builder.Services.AddSingleton<FrontmatterService>();
builder.Services.AddSingleton<MarkdownFileService>();
builder.Services.AddSingleton<ImageService>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<StatiqRunner>();
builder.Services.AddSingleton<ScriptRunnerService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.UseStatusCodePages("text/plain", "Status: {0}");

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

app.MapRazorPages();

app.Run();