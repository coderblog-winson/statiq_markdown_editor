// =====================================================
//  TagAutoLinkModule.cs
//
//  Auto-link occurrences of tag names to /tag/<slug>.html.
//  Slug rules match the Tags pipeline AND the link templates
//  in _PostLayout.cshtml / _Sidebar.cshtml:
//    lowercase, " "→"-", "."→"", "/"→"-"
//
//  Defensive: only keep tags whose /tag/<slug>.html page
//  actually got emitted on disk (otherwise we'd link to 404s).
//
//  Sort tags by length DESC so "ASP.NET Core" matches before
//  "ASP" or "Core" (longest-first prevents partial matches).
// =====================================================
using System.Text.RegularExpressions;
using Statiq.Common;

namespace SME.Statiq;

public class TagAutoLinkModule : ParallelModule
{
    protected override async Task<IEnumerable<IDocument>> ExecuteInputAsync(
        IDocument input, IExecutionContext context)
    {
        // 1. Collect all unique tag names from all docs' frontmatter.
        var allTags = context.Outputs
            .SelectMany(doc => doc.GetList<string>("Tags") ?? Enumerable.Empty<string>())
            .Distinct()
            .ToDictionary(
                tag => tag,
                tag => $"/tag/{tag.ToLower().Replace(" ", "-").Replace(".", "").Replace("/", "-")}.html"
            );

        // 2. Defensive: only keep tags whose page actually exists on disk.
        var validTags = new Dictionary<string, string>();
        var outRoot = context.FileSystem.OutputPath.FullPath;
        foreach (var kv in allTags)
        {
            var pagePath = Path.Combine(outRoot, kv.Value.TrimStart('/'));
            if (File.Exists(pagePath)) validTags[kv.Key] = kv.Value;
        }

        string content = await input.GetContentStringAsync();

        // 3. Auto-link whole-word matches (case-insensitive), skipping tags
        //    already inside an <a> tag.
        foreach (var tag in validTags.OrderByDescending(t => t.Key.Length))
        {
            string pattern = $@"\b({Regex.Escape(tag.Key)})\b(?![^<]*>)(?![^<]*</a>)";
            content = Regex.Replace(
                content, pattern,
                $"<a href=\"{tag.Value}\" class=\"internal-tag-link\">$1</a>",
                RegexOptions.IgnoreCase);
        }

        return input.Clone(context.GetContentProvider(content, "text/html")).Yield();
    }
}