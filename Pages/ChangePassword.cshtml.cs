using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StatiqMarkdownEditor.Auth;

namespace StatiqMarkdownEditor.Pages;

/// <summary>
/// Serves the change-password form. The form posts to
/// /api/auth/change-password (JSON/fetch), so this page only renders
/// the form + CSRF token.
/// </summary>
public class ChangePasswordModel : PageModel
{
    private readonly AuthService _auth;

    public ChangePasswordModel(AuthService auth)
    {
        _auth = auth;
    }

    public string CsrfToken { get; set; } = string.Empty;
    public bool AuthDisabled { get; set; } = false;

    public void OnGet()
    {
        AuthDisabled = !_auth.IsEnabled;

        // Force re-fetch — same reasoning as LoginModel.OnGet. The page
        // contains a hidden _csrf token that's regenerated every request;
        // a stale cache would have a token that's already been consumed
        // and the form would fail with "csrf token missing or mismatched".
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";

        // Don't bother rendering the form if auth is off — the card
        // shows a message explaining why and links back to the editor.
        if (AuthDisabled) return;

        // Issue a CSRF token (non-HttpOnly cookie so JS can echo it back).
        CsrfToken = _auth.GenerateCsrfToken();
        Response.Cookies.Append(
            AuthService.CsrfCookieName,
            CsrfToken,
            _auth.BuildCsrfCookieOptions(TimeSpan.FromHours(8)));
    }
}