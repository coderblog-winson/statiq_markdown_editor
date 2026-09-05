using Microsoft.Extensions.Options;
using StatiqMarkdownEditor.Models;
using StatiqMarkdownEditor.Pages;
using StatiqMarkdownEditor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.Configure<StatiqProjectOptions>(
    builder.Configuration.GetSection("StatiqProject"));
builder.Services.AddSingleton<FrontmatterService>();
builder.Services.AddSingleton<MarkdownFileService>();
builder.Services.AddSingleton<ImageService>();
builder.Services.AddSingleton<SettingsService>();
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
// Paginated list. Query params:
//   page     — 1-based page number (default 1)
//   pageSize — items per page (default 12, capped at 100)
//   category — exact-match filter
//   q        — case-insensitive title/filename substring filter
//
// Response shape:
//   { items: PostSummary[], total, page, pageSize, totalPages }
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
// the Statiq project's input/images/ directory. This lets the live preview
// pane render <img src="/images/2026-09/foo.webp"> without needing a
// Statiq build step. The handler reads the current Root from
// IOptionsMonitor so changes to Settings take effect immediately.
app.MapGet("/images/{*path}", (string path, IOptionsMonitor<StatiqProjectOptions> opt) =>
{
    if (string.IsNullOrEmpty(path)) return Results.BadRequest(new { error = "path required" });
    var o = opt.CurrentValue.Active;
    if (string.IsNullOrEmpty(o.Root)) return Results.NotFound();
    // Defensive normalisation
    var trimmed = path.TrimStart('/');
    var imagesRoot = Path.Combine(o.Root, o.ImagesSubdir ?? "input/images");
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

app.MapGet("/api/settings", async (IOptionsMonitor<StatiqProjectOptions> opt, SettingsService svc) =>
{
    // Trigger a v1→v2 migration if needed (one-time, idempotent).
    var (loaded, migrated) = await svc.LoadAsync();
    if (migrated)
    {
        // LoadAsync reloaded the config root; re-read so the response
        // matches what other consumers (MarkdownFileService, etc.) will see.
    }
    var o = opt.CurrentValue;
    var active = o.Active;
    var actualRoot = Directory.Exists(active.Root) ? active.Root : null;
    return Results.Ok(new
    {
        projects = o.Projects.Select(p => new
        {
            name = p.Name,
            root = p.Root,
            rootExists = Directory.Exists(p.Root),
            contentSubdir = p.ContentSubdir,
            imagesSubdir = p.ImagesSubdir,
            previewScriptPath = p.PreviewScriptPath,
            deployScriptPath = p.DeployScriptPath,
            previewPort = p.PreviewPort,
            watermark = p.Watermark,
        }),
        activeProjectName = o.ActiveProjectName,
        migrated = migrated,
        // The "active project" view (used by editor + list pages).
        active = new
        {
            name = active.Name,
            root = active.Root,
            rootExists = actualRoot is not null,
            contentSubdir = active.ContentSubdir,
            imagesSubdir = active.ImagesSubdir,
            fullContentPath = actualRoot is null ? null : Path.Combine(actualRoot, active.ContentSubdir),
            fullImagesPath = actualRoot is null ? null : Path.Combine(actualRoot, active.ImagesSubdir),
            previewScriptPath = active.PreviewScriptPath,
            deployScriptPath = active.DeployScriptPath,
            previewPort = active.PreviewPort,
            watermark = active.Watermark,
        },
    });
});

app.MapPut("/api/settings", async (SettingsUpdateRequest body, SettingsService svc) =>
{
    if (body is null) return Results.BadRequest(new { error = "body required" });
    var (ok, err) = await svc.UpdateAsync(body);
    return ok ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = err });
});

// Lightweight "switch project" endpoint. The top-nav dropdown hits this
// instead of round-tripping the whole settings payload on every click.
app.MapPost("/api/projects/activate", async (HttpContext ctx, SettingsService svc) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<ActivateProjectRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Name))
        return Results.BadRequest(new { error = "name required" });
    var (ok, err) = await svc.SetActiveProjectAsync(body.Name);
    return ok ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = err });
});

app.MapGet("/api/projects", (IOptionsMonitor<StatiqProjectOptions> opt) =>
{
    var o = opt.CurrentValue;
    return Results.Ok(new
    {
        projects = o.Projects.Select(p => new { name = p.Name, root = p.Root }),
        activeProjectName = o.ActiveProjectName,
    });
});

// ---------- Script runner (Preview / Deploy) ----------
//
// Two script paths are configured in Settings. The "Preview" script is
// expected to be a long-lived bash script that builds the site and
// then starts a local HTTP server — we launch it detached so the
// server survives the .NET process. The "Deploy" script is a one-shot
// build+rsync that we track via Process so the UI can show
// running/done + exit code.
//
// In v2 the script paths are stored RELATIVE to the active project's
// root. We resolve them here (Root + relative path) before handing
// them to the runner.

static string ResolveScriptPath(StatiqProjectOptions opt, bool preview)
{
    var p = opt.Active;
    if (string.IsNullOrEmpty(p.Root)) return "";
    var rel = preview ? p.PreviewScriptPath : p.DeployScriptPath;
    if (string.IsNullOrWhiteSpace(rel)) return "";
    if (Path.IsPathRooted(rel)) return rel; // tolerate absolute paths
    return Path.Combine(p.Root, rel);
}

app.MapPost("/api/scripts/preview", (IOptionsMonitor<StatiqProjectOptions> opt, ScriptRunnerService runner) =>
{
    var scriptPath = ResolveScriptPath(opt.CurrentValue, preview: true);
    var port = opt.CurrentValue.Active.PreviewPort;
    var (ok, err) = runner.StartPreview(scriptPath, port);
    if (!ok) return Results.BadRequest(new { error = err });
    var status = runner.PreviewStatus();
    return Results.Ok(new { ok = true, port = status.port, logFile = status.logFile });
});

app.MapGet("/api/scripts/preview/status", (ScriptRunnerService runner) =>
{
    var s = runner.PreviewStatus();
    return Results.Ok(new
    {
        running = s.running,
        serverReady = s.serverReady,
        port = s.port,
        logFile = s.logFile,
        startedAt = s.startedAt,
    });
});

app.MapPost("/api/scripts/preview/stop", (ScriptRunnerService runner) =>
{
    var (ok, err, killed) = runner.StopPreview();
    if (!ok) return Results.BadRequest(new { error = err });
    return Results.Ok(new { ok = true, killed });
});

app.MapGet("/api/scripts/preview/log", (HttpContext ctx, ScriptRunnerService runner) =>
{
    var tail = 500;
    if (int.TryParse(ctx.Request.Query["tail"], out var t) && t > 0 && t <= 5000) tail = t;
    return Results.Ok(new { text = runner.ReadLog("preview", tail) });
});

app.MapPost("/api/scripts/deploy", (IOptionsMonitor<StatiqProjectOptions> opt, ScriptRunnerService runner) =>
{
    var scriptPath = ResolveScriptPath(opt.CurrentValue, preview: false);
    var (ok, err) = runner.StartDeploy(scriptPath);
    if (!ok) return Results.BadRequest(new { error = err });
    var s = runner.DeployStatus();
    return Results.Ok(new { ok = true, logFile = s.logFile });
});

app.MapGet("/api/scripts/deploy/status", (ScriptRunnerService runner) =>
{
    var s = runner.DeployStatus();
    return Results.Ok(new
    {
        running = s.running,
        exitCode = s.exitCode,
        logFile = s.logFile,
    });
});

app.MapGet("/api/scripts/deploy/log", (HttpContext ctx, ScriptRunnerService runner) =>
{
    var tail = 500;
    if (int.TryParse(ctx.Request.Query["tail"], out var t) && t > 0 && t <= 5000) tail = t;
    return Results.Ok(new { text = runner.ReadLog("deploy", tail) });
});

app.MapRazorPages();

app.Run();
