namespace StatiqMarkdownEditor.Auth;

/// <summary>
/// JSON body shape for POST /api/auth/login (the non-form variant —
/// the Razor login page posts form-urlencoded instead, but a CLI
/// client can post JSON).
/// </summary>
public sealed record LoginRequest(string? Password, string? Csrf, string? ReturnUrl);