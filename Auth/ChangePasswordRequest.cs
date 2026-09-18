namespace StatiqMarkdownEditor.Auth;

/// <summary>
/// JSON body for POST /api/auth/change-password. All three fields are
/// required and the new password must be at least
/// <see cref="AuthService.MinPasswordLength"/> characters (enforced by
/// the endpoint, not here).
/// </summary>
public sealed record ChangePasswordRequest(
    string? CurrentPassword,
    string? NewPassword,
    string? Csrf);