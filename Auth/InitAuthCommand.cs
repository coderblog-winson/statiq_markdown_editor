using System.Text;
using System.Text.Json;

namespace StatiqMarkdownEditor.Auth;

/// <summary>
/// Handles the `dotnet run --init-auth` CLI subcommand: prompts for a
/// password, computes a PBKDF2-SHA256 hash, writes Auth/auth.json with
/// mode 600 (unix), and exits before the web host starts.
///
/// Lives in its own file because Program.cs is a top-level statements
/// file (no type declarations allowed after the main body).
/// </summary>
public static class InitAuthCommand
{
    public static int Run(string[] args)
    {
        var pwd1 = ReadPasswordFromStdin("New password: ");
        if (string.IsNullOrEmpty(pwd1))
        {
            Console.Error.WriteLine("aborted: empty password");
            return 1;
        }
        var pwd2 = ReadPasswordFromStdin("Confirm password: ");
        if (pwd1 != pwd2)
        {
            Console.Error.WriteLine("aborted: passwords do not match");
            return 1;
        }

        var cfg = AuthConfig.CreateForPassword(pwd1);
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "Auth");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "auth.json");
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));

        try
        {
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
            {
                System.Diagnostics.Process.Start("chmod", $"600 {path}")?.WaitForExit();
            }
        }
        catch
        {
            // chmod may fail in odd environments; not fatal.
        }

        Console.WriteLine();
        Console.WriteLine($"wrote {path} (mode 600)");
        Console.WriteLine("next step: set Auth.Enabled=true in appsettings.json and restart.");
        return 0;
    }

    /// <summary>
    /// Read a password from stdin. Uses Console.ReadKey(true) when the
    /// input is a real TTY (chars are masked); falls back to ReadLine
    /// for piped stdin so CI scripts can drive it.
    /// </summary>
    private static string? ReadPasswordFromStdin(string prompt)
    {
        Console.Write(prompt);
        var sb = new StringBuilder();
        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
                if (key.Key == ConsoleKey.Backspace && sb.Length > 0) { sb.Length--; continue; }
                if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
            }
        }
        catch (InvalidOperationException)
        {
            // Not a TTY (piped input). Fall back to ReadLine.
            var line = Console.ReadLine();
            return string.IsNullOrEmpty(line) ? null : line.Trim();
        }
        return sb.ToString();
    }
}