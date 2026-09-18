using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StatiqMarkdownEditor.Auth;

namespace StatiqMarkdownEditor.Pages;

/// <summary>
/// Serves the login form. The form posts to /api/auth/login (JSON/fetch),
/// so this page only needs to render: CSRF cookie + token + the form.
/// </summary>
public class LoginModel : PageModel
{
    private readonly AuthService _auth;

    public LoginModel(AuthService auth)
    {
        _auth = auth;
    }

    public string CsrfToken { get; set; } = string.Empty;
    public string ReturnUrl { get; set; } = "/";
    public bool AuthDisabled { get; set; } = false;

    public void OnGet(string? returnUrl)
    {
        AuthDisabled = !_auth.IsEnabled;

        // Force re-fetch on every page load — if the browser served a
        // cached version of the form, its hidden _csrf field could be
        // stale (older token alphabet, or a different session) and
        // diverge from the freshly-issued one in the response cookie.
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";

        // If already signed in, jump straight to the editor.
        if (_auth.IsEnabled
            && Request.Cookies.TryGetValue(AuthService.SessionCookieName, out var token)
            && _auth.TryValidateSessionToken(token, out _))
        {
            Response.Redirect(string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl);
            return;
        }

        ReturnUrl = string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl;

        // If a CSRF cookie is already present (and looks like one of our
        // tokens), reuse it — that way the form's hidden field and the
        // Set-Cookie header always carry the same string. Otherwise issue
        // a fresh token and set the cookie.
        string csrfToken;
        if (Request.Cookies.TryGetValue(AuthService.CsrfCookieName, out var existing)
            && !string.IsNullOrEmpty(existing)
            && existing.Length >= 32)
        {
            csrfToken = existing;
        }
        else
        {
            csrfToken = _auth.GenerateCsrfToken();
            Response.Cookies.Append(
                AuthService.CsrfCookieName,
                csrfToken,
                _auth.BuildCsrfCookieOptions(TimeSpan.FromHours(8)));
        }

        CsrfToken = csrfToken;
    }
}