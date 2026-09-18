using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StatiqMarkdownEditor.Auth;

namespace StatiqMarkdownEditor.Pages;

/// <summary>
/// GET /Logout — clears the session cookie and renders the "signed out"
/// page. Idempotent (safe to revisit). Note: this is GET-by-design rather
/// than POST because the only side-effect is dropping the local session,
/// and the SameSite=Strict session cookie already mitigates cross-site
/// triggering. If you need stricter semantics, swap the link for a tiny
/// form that POSTs to /api/auth/logout.
/// </summary>
public class LogoutModel : PageModel
{
    public void OnGet()
    {
        Response.Cookies.Delete(AuthService.SessionCookieName);
        Response.Cookies.Delete(AuthService.CsrfCookieName);
    }
}