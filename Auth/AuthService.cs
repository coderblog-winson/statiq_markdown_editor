using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;

namespace StatiqMarkdownEditor.Auth;

/// <summary>
/// Bound from the "Auth" section of appsettings.json:
///   Auth.Enabled   = bool   (default false — local-only by design)
///   Auth.HmacKey   = string (optional override; falls back to passwordHash)
///
/// Wired via <see cref="IOptionsMonitor{T}"/> so that edits to appsettings.json
/// take effect immediately, without restarting the ASP.NET host. The default
/// JSON configuration provider reloads on file change (reloadOnChange: true),
/// and IOptionsMonitor picks up the new snapshot on every CurrentValue read.
/// </summary>
public sealed class AuthOptions
{
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Optional HMAC secret for signing session tokens. If empty, we derive
    /// it from the password hash in auth.json (which means an attacker who
    /// can read auth.json can forge sessions — but if they can read auth.json
    /// they already have everything, so this is fine for the threat model).
    /// Override here for setups where auth.json is rotated out-of-band.
    /// </summary>
    public string HmacKey { get; set; } = string.Empty;
}

/// <summary>
/// Handles auth.json loading, password verification, and session token
/// signing/verification. Session tokens are stateless (HMAC-SHA256 signed)
/// so there is no server-side session table to maintain — restarting the
/// editor simply invalidates every session, which is fine for a tool that
/// runs on a single laptop.
///
/// Cookie: name = "statiq_editor_session", HttpOnly, SameSite=Strict,
/// Secure when the request is HTTPS. Lifetime is taken from
/// AuthConfig.SessionExpiryHours (default 24h).
/// </summary>
public sealed class AuthService
{
    public const string SessionCookieName = "statiq_editor_session";
    public const string CsrfCookieName = "statiq_editor_csrf";
    public const string CsrfFormField = "_csrf";
    public const string CsrfHeaderName = "X-CSRF-Token";

    private readonly IOptionsMonitor<AuthOptions> _optionsMonitor;
    private readonly ILogger<AuthService> _logger;
    private readonly string _configPath;

    private AuthConfig? _config;
    private byte[]? _hmacKey;
    private readonly object _lock = new();

    public AuthService(
        IOptionsMonitor<AuthOptions> options,
        ILogger<AuthService> logger,
        IHostEnvironment env)
    {
        _optionsMonitor = options;
        _logger = logger;
        // Default path: <editor root>/Auth/auth.json (NOT under wwwroot/, so
        // the web server does not serve it). Overridable via env var for
        // Docker / shared-secret scenarios.
        var fromEnv = Environment.GetEnvironmentVariable("STATIQ_EDITOR_AUTH_FILE");
        _configPath = !string.IsNullOrEmpty(fromEnv)
            ? fromEnv
            : Path.Combine(env.ContentRootPath, "Auth", "auth.json");
    }

    // Read the live Auth:Enabled value from the OptionsMonitor on every
    // access. The JSON configuration provider reloads on file change, so
    // toggling Auth.Enabled in appsettings.json takes effect on the very
    // next request — no host restart needed.
    public bool IsEnabled => _optionsMonitor.CurrentValue.Enabled;

    public string ConfigPath => _configPath;

    /// <summary>True if Enabled AND auth.json exists and parses.</summary>
    public bool IsReady()
    {
        if (!_optionsMonitor.CurrentValue.Enabled) return false;
        return LoadConfig() != null;
    }

    public AuthConfig? LoadConfig()
    {
        lock (_lock)
        {
            if (_config != null) return _config;

            if (!File.Exists(_configPath))
            {
                _logger.LogWarning("Auth.Enabled=true but auth.json not found at {Path}. Run `dotnet run --init-auth` to create one.", _configPath);
                return null;
            }

            try
            {
                var json = File.ReadAllText(_configPath);
                _config = JsonSerializer.Deserialize<AuthConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
                if (_config is null || string.IsNullOrEmpty(_config.PasswordHash) || string.IsNullOrEmpty(_config.Salt))
                {
                    _logger.LogError("auth.json at {Path} is malformed (missing hash/salt).", _configPath);
                    return null;
                }
                _hmacKey = !string.IsNullOrEmpty(_optionsMonitor.CurrentValue.HmacKey)
                    ? Encoding.UTF8.GetBytes(_optionsMonitor.CurrentValue.HmacKey)
                    : Encoding.UTF8.GetBytes(_config.PasswordHash);
                return _config;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load auth.json from {Path}.", _configPath);
                return null;
            }
        }
    }

    /// <summary>
    /// Returns true if the candidate password matches the stored PBKDF2
    /// hash. Always re-loads config from disk to honour rotations.
    /// </summary>
    public bool VerifyPassword(string candidate)
    {
        var cfg = LoadConfig();
        return cfg != null && cfg.VerifyPassword(candidate);
    }

    /// <summary>
    /// Build a session token: base64(expiry_unix | nonce16 | hmac(payload)).
    /// Stateless — no DB lookup needed to verify.
    /// </summary>
    public string IssueSessionToken()
    {
        var cfg = LoadConfig()
            ?? throw new InvalidOperationException("auth.json not loaded; cannot issue session.");
        var key = _hmacKey
            ?? throw new InvalidOperationException("HMAC key not initialised.");

        long expiryUnix = DateTimeOffset.UtcNow.AddHours(cfg.SessionExpiryHours).ToUnixTimeSeconds();
        byte[] nonce = RandomNumberGenerator.GetBytes(16);

        // payload = expiry (8 bytes BE) + nonce (16 bytes)
        byte[] payload = new byte[8 + 16];
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(0, 8), expiryUnix);
        nonce.CopyTo(payload, 8);

        byte[] sig = HMACSHA256.HashData(key, payload);
        byte[] full = new byte[payload.Length + sig.Length];
        Buffer.BlockCopy(payload, 0, full, 0, payload.Length);
        Buffer.BlockCopy(sig, 0, full, payload.Length, sig.Length);

        return Convert.ToBase64String(full);
    }

    /// <summary>
    /// Verify a session token's signature AND that it has not expired.
    /// Returns true and outputs the expiry if valid.
    /// </summary>
    public bool TryValidateSessionToken(string? token, out DateTimeOffset expiry)
    {
        expiry = default;
        if (string.IsNullOrEmpty(token)) return false;

        byte[] raw;
        try { raw = Convert.FromBase64String(token); }
        catch (FormatException) { return false; }

        // 8 expiry + 16 nonce + 32 HMAC-SHA256
        if (raw.Length != 56) return false;

        var key = _hmacKey;
        if (key is null) return false;

        byte[] payload = new byte[24];
        Buffer.BlockCopy(raw, 0, payload, 0, 24);
        byte[] sig = new byte[32];
        Buffer.BlockCopy(raw, 24, sig, 0, 32);

        byte[] expected = HMACSHA256.HashData(key, payload);
        if (!CryptographicOperations.FixedTimeEquals(expected, sig)) return false;

        long expiryUnix = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(0, 8));
        expiry = DateTimeOffset.FromUnixTimeSeconds(expiryUnix);
        if (expiry < DateTimeOffset.UtcNow) return false;

        return true;
    }

    /// <summary>Build a session cookie's CookieOptions for the current request.</summary>
    public CookieOptions BuildSessionCookieOptions(HttpContext ctx, TimeSpan lifetime)
    {
        bool isHttps = ctx.Request.IsHttps;
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = isHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.Add(lifetime),
            IsEssential = true,
        };
    }

    /// <summary>Build the (non-HttpOnly) CSRF cookie that JS reads on login pages.</summary>
    public CookieOptions BuildCsrfCookieOptions(TimeSpan lifetime)
    {
        return new CookieOptions
        {
            HttpOnly = false,
            Secure = false,  // JS needs to read it
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.Add(lifetime),
            IsEssential = true,
        };
    }

    /// <summary>Generate a CSRF token as base64url (no '+' '/' or '=' so it
    /// round-trips cleanly through a cookie value AND a form field — the
    /// standard base64 alphabet gets URL-encoded by ASP.NET Core when
    /// written to a Set-Cookie header, which makes the cookie value
    /// diverge from the form field value and trips the comparison).</summary>
    public string GenerateCsrfToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>Constant-time compare of two CSRF tokens.</summary>
    public bool CsrfTokensMatch(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        byte[] ab = Encoding.UTF8.GetBytes(a);
        byte[] bb = Encoding.UTF8.GetBytes(b);
        return ab.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ab, bb);
    }

    /// <summary>
    /// Result of <see cref="ChangePasswordAsync"/>. The caller (the
    /// change-password endpoint) maps each value to a distinct HTTP
    /// status so the UI can show a specific error message.
    /// </summary>
    public enum ChangePasswordResult
    {
        Ok,
        NotReady,           // auth.json missing or unreadable
        WeakNewPassword,    // length < MinPasswordLength
        SameAsCurrent,      // new == current — pointless rotation
        WrongCurrent,       // currentPassword didn't match
        WriteFailed,        // IO error writing auth.json
    }

    public const int MinPasswordLength = 8;

    /// <summary>
    /// Rotate the admin password. Verifies the current password with the
    /// stored hash, derives a fresh hash + salt for the new password, and
    /// atomically rewrites auth.json (write to .tmp + rename) so a crash
    /// mid-rotation never leaves the file half-written with the old hash
    /// deleted but no new one yet.
    ///
    /// Side effect: the in-memory _config + _hmacKey are cleared, which
    /// means every existing session cookie becomes invalid on the next
    /// request. That is the desired behaviour — a stolen cookie should
    /// not survive a password rotation.
    /// </summary>
    public async Task<ChangePasswordResult> ChangePasswordAsync(string currentPassword, string newPassword)
    {
        var cfg = LoadConfig();
        if (cfg is null) return ChangePasswordResult.NotReady;

        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < MinPasswordLength)
            return ChangePasswordResult.WeakNewPassword;

        // Verify current first, before we touch anything. Compare in
        // constant time (VerifyPassword already does this internally).
        if (!cfg.VerifyPassword(currentPassword ?? string.Empty))
            return ChangePasswordResult.WrongCurrent;

        // Refuse a no-op rotation — it would re-hash and rewrite the
        // file but invalidate every session for no reason.
        if (cfg.VerifyPassword(newPassword))
            return ChangePasswordResult.SameAsCurrent;

        var newCfg = AuthConfig.CreateForPassword(newPassword, cfg.Iterations);
        newCfg.SessionExpiryHours = cfg.SessionExpiryHours;

        var tmp = _configPath + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(newCfg, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            await File.WriteAllTextAsync(tmp, json);

            // File.Move overwrite is .NET 5+; falls back to delete+rename
            // on older runtimes. On Windows, rename over an existing
            // file fails — guard with delete if needed. On unix/macOS,
            // rename atomically replaces the target.
            if (OperatingSystem.IsWindows() && File.Exists(_configPath))
            {
                File.Replace(tmp, _configPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, _configPath, overwrite: true);
            }

            // Re-apply 600 perms (in case the .tmp file got different perms
            // from the editor process). Ignore failures — best effort.
            try
            {
                if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
                {
                    System.Diagnostics.Process.Start("chmod", $"600 {_configPath}")?.WaitForExit();
                }
            }
            catch { /* non-fatal */ }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write auth.json during password rotation.");
            // Best-effort cleanup of the .tmp file
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return ChangePasswordResult.WriteFailed;
        }

        // Invalidate in-memory cache so subsequent Verify / SessionToken
        // calls reload from disk (with the new hash). Every existing
        // session cookie is now invalid because the HMAC key changed.
        lock (_lock)
        {
            _config = null;
            _hmacKey = null;
        }
        _logger.LogInformation("Admin password rotated. All existing sessions invalidated.");
        return ChangePasswordResult.Ok;
    }
}