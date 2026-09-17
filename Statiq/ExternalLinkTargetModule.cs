// =====================================================
//  ExternalLinkTargetModule.cs
//
//  Auto-add target="_blank" rel="noopener noreferrer" to
//  all off-site <a href="https?://..."> links.
//
//  Internal domains are read from the Statiq setting
//  "InternalDomains" (a comma-joined string), which the
//  custom pipelines set from SiteConfig.Host + "www.<host>".
//
//  Skips:
//    - Links that already have a target= attribute
//    - Links pointing to internal domains
//    - Relative URLs (no scheme)
// =====================================================
using System.Text.RegularExpressions;
using Statiq.Common;

namespace SME.Statiq;

public class ExternalLinkTargetModule : ParallelModule
{
    private static readonly Regex AnchorRegex = new(
        @"<a\s+([^>]*?)href=""(https?://[^""]+)""([^>]*?)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Set in BeforeExecution (single-threaded); ExecuteInputAsync only reads.
    // (HashSet modification in ExecuteInputAsync races with parallel docs.)
    private HashSet<string> _internalDomains = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task BeforeExecutionAsync(IExecutionContext context)
    {
        // Read internal-domain list from settings (set by CustomPipelines from SiteConfig.Host).
        // BeforeExecutionAsync is single-threaded, so it's safe to populate the set here.
        var setting = context.Settings.GetString("InternalDomains");
        if (!string.IsNullOrEmpty(setting))
        {
            foreach (var d in setting.Split(',', StringSplitOptions.RemoveEmptyEntries))
                _internalDomains.Add(d.Trim());
        }
        await Task.CompletedTask;
    }

    protected override async Task<IEnumerable<IDocument>> ExecuteInputAsync(
        IDocument input, IExecutionContext context)
    {
        string content = await input.GetContentStringAsync();

        content = AnchorRegex.Replace(content, match =>
        {
            string beforeHref = match.Groups[1].Value;
            string href       = match.Groups[2].Value;
            string afterHref  = match.Groups[3].Value;
            string fullAttrs  = beforeHref + afterHref;

            if (Regex.IsMatch(fullAttrs, @"\btarget\s*=", RegexOptions.IgnoreCase))
                return match.Value;

            if (IsInternal(href))
                return match.Value;

            return $"<a {beforeHref}href=\"{href}\"{afterHref} target=\"_blank\" rel=\"noopener noreferrer\">";
        });

        return input.Clone(context.GetContentProvider(content, "text/html")).Yield();
    }

    private bool IsInternal(string href)
    {
        if (_internalDomains.Count == 0) return false;
        if (Uri.TryCreate(href, UriKind.Absolute, out var uri))
            return _internalDomains.Contains(uri.Host);
        return false;
    }
}