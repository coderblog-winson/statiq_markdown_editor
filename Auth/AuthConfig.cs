using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace StatiqMarkdownEditor.Auth;

/// <summary>
/// auth.json file shape. Contains a PBKDF2-derived hash of the admin
/// password, a per-file salt, and a few safety knobs. NEVER commit this
/// file — it grants access to every site the editor manages.
///
/// To create one:
///   dotnet run --init-auth
/// which prompts for a password and writes Auth/auth.json (mode 600 on
/// unix). Or set STATIQ_EDITOR_AUTH_FILE to point at any json file with
/// the same shape (handy for Docker / shared-secret deployment).
/// </summary>
public sealed class AuthConfig
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>Base64-encoded PBKDF2-SHA256 hash of the admin password.</summary>
    [JsonPropertyName("passwordHash")]
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Base64-encoded per-file salt.</summary>
    [JsonPropertyName("salt")]
    public string Salt { get; set; } = string.Empty;

    /// <summary>PBKDF2 iteration count. 100k is the OWASP 2023 floor for SHA-256.</summary>
    [JsonPropertyName("iterations")]
    public int Iterations { get; set; } = 100_000;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>How long a session cookie stays valid after login.</summary>
    [JsonPropertyName("sessionExpiryHours")]
    public int SessionExpiryHours { get; set; } = 24;

    /// <summary>
    /// Constant-time verify of a candidate password against the stored hash.
    /// Uses PBKDF2-HMAC-SHA256. Returns true on match, false otherwise.
    /// </summary>
    public bool VerifyPassword(string candidate)
    {
        if (string.IsNullOrEmpty(PasswordHash) || string.IsNullOrEmpty(Salt))
            return false;

        byte[] saltBytes;
        byte[] expectedHash;
        try
        {
            saltBytes = Convert.FromBase64String(Salt);
            expectedHash = Convert.FromBase64String(PasswordHash);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
            password: candidate,
            salt: saltBytes,
            iterations: Iterations,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: expectedHash.Length);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    /// <summary>
    /// Compute a hash + salt for a fresh password. Used by the
    /// `dotnet run --init-auth` helper. Returns the populated AuthConfig.
    /// </summary>
    public static AuthConfig CreateForPassword(string password, int iterations = 100_000)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password: password,
            salt: salt,
            iterations: iterations,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: 32);

        return new AuthConfig
        {
            Version = 1,
            PasswordHash = Convert.ToBase64String(hash),
            Salt = Convert.ToBase64String(salt),
            Iterations = iterations,
            CreatedAt = DateTimeOffset.UtcNow,
            SessionExpiryHours = 24,
        };
    }
}