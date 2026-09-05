using StatiqMarkdownEditor.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace StatiqMarkdownEditor.Services;

/// <summary>
/// Reads and writes the YAML frontmatter block at the top of Statiq markdown files.
/// We keep a stable, deterministic key order so the diff stays clean.
/// </summary>
public class FrontmatterService
{
    /// <summary>
    /// Coderblog canonical field order. Unknown keys go at the end, in
    /// their original order. <c>Draft</c> sits between Category and Tags
    /// to match the order some existing posts already use (see
    /// 202609/your-first-asp-net-core-web-api-in-5-min.md).
    /// </summary>
    public static readonly string[] CanonicalOrder =
    {
        "Title", "Description", "Date", "Layout", "Image", "Category", "Draft", "Tags",
    };

    private readonly IDeserializer _yamlDeserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly ISerializer _yamlSerializer = new SerializerBuilder()
        .DisableAliases()
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public (FrontmatterData fm, string body, int frontmatterLineCount) Split(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return (new FrontmatterData(), string.Empty, 0);

        // Frontmatter must start at byte 0 with `---` on its own line.
        if (!raw.StartsWith("---"))
            return (new FrontmatterData(), raw, 0);

        // Find the closing `---` line.
        var newlineAfterFirst = raw.IndexOf('\n');
        if (newlineAfterFirst < 0)
            return (new FrontmatterData(), raw, 0);

        var rest = raw[(newlineAfterFirst + 1)..];
        var closingIndex = rest.IndexOf("\n---", StringComparison.Ordinal);
        if (closingIndex < 0)
            return (new FrontmatterData(), raw, 0);

        // Make sure the closing fence is on its own line (followed by EOL or EOF).
        var afterClosing = closingIndex + 4; // skip "\n---"
        if (afterClosing < rest.Length && rest[afterClosing] != '\n' && rest[afterClosing] != '\r')
        {
            // The second `---` is mid-line, not a fence. Fallback: treat whole file as body.
            return (new FrontmatterData(), raw, 0);
        }

        var yamlBlock = rest[..closingIndex];
        var bodyStart = afterClosing;
        if (bodyStart < rest.Length && rest[bodyStart] == '\r') bodyStart++;
        if (bodyStart < rest.Length && rest[bodyStart] == '\n') bodyStart++;

        var body = rest[bodyStart..];
        var frontmatterLineCount = 1 + CountLines(rest[..(closingIndex + 4)]);

        var fm = ParseYaml(yamlBlock);
        return (fm, body, frontmatterLineCount);
    }

    public string Compose(FrontmatterData fm, string body)
    {
        var orderedKeys = OrderKeys(fm);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("---");
        foreach (var key in orderedKeys)
        {
            switch (key)
            {
                case "Title":       AppendString(sb, "Title",       fm.Title); break;
                case "Description": AppendString(sb, "Description", fm.Description); break;
                case "Date":        AppendDate(sb, "Date",         fm.Date); break;
                case "Layout":      AppendString(sb, "Layout",      fm.Layout); break;
                case "Image":       AppendString(sb, "Image",       fm.Image); break;
                case "Category":    AppendString(sb, "Category",    fm.Category); break;
                case "Draft":       AppendBool(sb, "Draft",        fm.Draft); break;
                case "Tags":        AppendTags(sb, "Tags",         fm.Tags); break;
                default:
                    if (fm.Extra.TryGetValue(key, out var extra))
                        sb.AppendLine($"{key}: {FormatExtra(extra)}");
                    break;
            }
        }
        sb.AppendLine("---");
        // Body: ensure it starts with exactly one blank line after the closing fence.
        if (!string.IsNullOrEmpty(body))
        {
            var trimmedBody = body.TrimStart('\r', '\n');
            sb.Append('\n');
            sb.Append(trimmedBody);
            if (!trimmedBody.EndsWith("\n")) sb.Append('\n');
        }
        return sb.ToString();
    }

    public FrontmatterData ParseYaml(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return new FrontmatterData();
        try
        {
            var dict = _yamlDeserializer.Deserialize<Dictionary<string, object>>(yaml) ?? new();
            var fm = new FrontmatterData();
            foreach (var (k, v) in dict)
            {
                switch (k)
                {
                    case "Title":       fm.Title = v?.ToString(); break;
                    case "Description": fm.Description = v?.ToString(); break;
                    case "Date":        fm.Date = ParseDate(v); break;
                    case "Layout":      fm.Layout = v?.ToString(); break;
                    case "Image":       fm.Image = v?.ToString(); break;
                    case "Category":    fm.Category = v?.ToString(); break;
                    case "Draft":       fm.Draft = ParseBool(v); break;
                    case "Tags":        fm.Tags = ParseTags(v); break;
                    default:            fm.Extra[k] = v ?? string.Empty; break;
                }
            }
            return fm;
        }
        catch
        {
            return new FrontmatterData();
        }
    }

    // --- helpers ---

    private static List<string> OrderKeys(FrontmatterData fm)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 1. Canonical order, only for keys that have a value
        foreach (var k in CanonicalOrder)
        {
            if (HasValue(fm, k)) { keys.Add(k); seen.Add(k); }
        }
        // 2. Preserve any extra keys in their original insertion order
        foreach (var k in fm.Extra.Keys)
        {
            if (seen.Add(k)) keys.Add(k);
        }
        return keys;
    }

    private static bool HasValue(FrontmatterData fm, string key) => key switch
    {
        // Every canonical key is ALWAYS emitted, even when blank.
        // Reason: the user expects to see the same set of keys on every
        // new post — they shouldn't have to add a missing key just to
        // set its value. Blank values render as `Field: ""` (or
        // `Draft: false`). The `Tags` key still omits itself when the
        // list is empty so we don't write `Tags: []`, which Statiq
        // would treat as "tagged with nothing" — slightly noisier.
        "Title"       => true,
        "Description" => true,
        "Date"        => true,
        "Layout"      => true,
        "Image"       => true,
        "Category"    => true,
        "Tags"        => fm.Tags.Count > 0,
        "Draft"       => true,
        _ => fm.Extra.ContainsKey(key),
    };

    private static void AppendString(System.Text.StringBuilder sb, string key, string? value)
    {
        // Even when the value is blank we still emit the key (with an
        // empty quoted string) so the field is always present in the
        // file. This makes later in-file edits predictable — the user
        // doesn't have to add the line first.
        sb.Append(key).Append(": \"").Append(EscapeQuotes(value ?? string.Empty)).Append('"').Append('\n');
    }

    private static void AppendBool(System.Text.StringBuilder sb, string key, bool value)
    {
        // Always emit (see HasValue for Draft). True → "true", false → "false"
        // (no quotes — bare booleans are conventional in YAML).
        sb.Append(key).Append(": ").Append(value ? "true" : "false").Append('\n');
    }

    private static void AppendDate(System.Text.StringBuilder sb, string key, DateTime? value)
    {
        if (!value.HasValue) return;
        sb.Append(key).Append(": ").Append(value.Value.ToString("yyyy-MM-dd")).Append('\n');
    }

    private static void AppendTags(System.Text.StringBuilder sb, string key, List<string> tags)
    {
        if (tags is null || tags.Count == 0) return;
        sb.Append(key).Append(": [");
        for (int i = 0; i < tags.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(tags[i]);
        }
        sb.Append(']').Append('\n');
    }

    private static string EscapeQuotes(string s) => s.Replace("\"", "\\\"");

    private static bool ParseBool(object? v)
    {
        if (v is null) return false;
        if (v is bool b) return b;
        var s = v.ToString()?.Trim();
        if (string.IsNullOrEmpty(s)) return false;
        return s.Equals("true", StringComparison.OrdinalIgnoreCase)
            || s == "1" || s == "yes" || s == "on";
    }

    private static DateTime? ParseDate(object? v)
    {
        if (v is null) return null;
        if (v is DateTime dt) return dt;
        if (DateTime.TryParse(v.ToString(), out var parsed)) return parsed;
        return null;
    }

    private static List<string> ParseTags(object? v)
    {
        var tags = new List<string>();
        if (v is null) return tags;
        // YamlDotNet maps flow sequence -> List<object>
        if (v is IEnumerable<object> seq)
        {
            foreach (var item in seq)
            {
                var s = item?.ToString();
                if (!string.IsNullOrWhiteSpace(s)) tags.Add(s.Trim());
            }
        }
        else
        {
            // Fallback: comma-separated string
            var s = v.ToString() ?? string.Empty;
            foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                tags.Add(part);
        }
        return tags;
    }

    private static int CountLines(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int n = 1;
        for (int i = 0; i < s.Length; i++) if (s[i] == '\n') n++;
        return n;
    }

    private static string FormatExtra(object v) => v switch
    {
        string s => s,
        bool b => b ? "true" : "false",
        null => "null",
        _ => v.ToString() ?? string.Empty,
    };
}
