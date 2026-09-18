namespace StatiqMarkdownEditor.Auth;

/// <summary>
/// Sits in front of every request. When AuthService.IsEnabled is true,
/// rejects requests that don't carry a valid session cookie. When auth is
/// off (the local-tool default), the middleware is a no-op.
///
/// Allow-list (no auth needed even when enabled):
///   /Login              — login page (GET form, POST submit)
///   /api/auth/login     — credential check endpoint
///   /api/auth/status    — "are we locked?" probe used by the login page
///   /api/auth/logout    — clears the session cookie
///   static files        — wwwroot assets (css/js/icons); not sensitive
///
/// Everything else — Razor pages AND /api/* — requires a valid session.
/// Failed auth returns:
///   - 401 JSON for /api/* paths
///   - 302 → /Login for everything else
/// </summary>
public sealed class RequireAuthMiddleware
{
    private readonly RequestDelegate _next;

    public RequireAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext ctx, AuthService auth)
    {
        if (!auth.IsEnabled)
        {
            await _next(ctx);
            return;
        }

        // If auth.json is missing/malformed, refuse all non-allow-list
        // paths with a clear 503 message. The /api/auth/* endpoints stay
        // reachable so the operator can see the error.
        if (!auth.IsReady())
        {
            if (IsAllowListed(ctx))
            {
                await _next(ctx);
                return;
            }
            await WriteAuthUnavailable(ctx);
            return;
        }

        var path = ctx.Request.Path.Value ?? "/";

        // Allow list
        if (IsAllowListed(ctx))
        {
            await _next(ctx);
            return;
        }

        // Check the session cookie
        if (ctx.Request.Cookies.TryGetValue(AuthService.SessionCookieName, out var token)
            && auth.TryValidateSessionToken(token, out _))
        {
            await _next(ctx);
            return;
        }

        // Reject
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(
                "{\"error\":\"authentication required\",\"loginUrl\":\"/Login\"}");
            return;
        }

        // Browser navigation → redirect to login (preserve return URL).
        var returnUrl = path + ctx.Request.QueryString.Value;
        ctx.Response.StatusCode = StatusCodes.Status302Found;
        ctx.Response.Headers.Location = "/Login?returnUrl="
            + Uri.EscapeDataString(returnUrl);
    }

    private static bool IsAllowListed(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        // Razor page for login form
        if (path.Equals("/Login", StringComparison.OrdinalIgnoreCase)) return true;
        // Error page (so we don't loop on startup errors)
        if (path.Equals("/Error", StringComparison.OrdinalIgnoreCase)) return true;
        // Auth API endpoints (login submit, status probe, logout)
        if (path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static async Task WriteAuthUnavailable(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(
                "{\"error\":\"auth.json missing or invalid — run `dotnet run --init-auth` to create one\"}");
        }
        else
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync(
                "auth.json missing or invalid. Run `dotnet run --init-auth` to create one.");
        }
    }
}