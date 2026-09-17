# `Statiq/` — shared custom pipeline module

The pipeline module shared by all three statiq sites, extracted from the
per-site `Program.cs` files and parameterised here.

## File inventory

| File | Purpose |
|---|---|
| `SiteConfig.cs` | Per-site config class (`Theme` / `Host` / `FigureStyle` / `StripMonthFromPostUrls` etc.) |
| `CustomPipelines.cs` | In-process `Bootstrapper` config: `SetDestination`, Tags pipeline, Figure shortcode, RSS / SEO metadata, Draft filter, http → https, Sitemap |
| `TagAutoLinkModule.cs` | Auto-wrap tag names that appear in posts with `<a>` links to `/tag/<slug>.html` |
| `ExternalLinkTargetModule.cs` | Auto-add `target="_blank" rel="noopener noreferrer"` to off-site links |

## Integration flow

1. `StatiqRunner.BuildAsync(siteName)` reads `sites/<name>/config.json` and produces a `SiteConfig`
2. `cfg.ResolvePaths(editorRoot)` resolves `SitePaths` (input / output / cache / theme physical paths)
3. `Bootstrapper.Factory.CreateWeb().SetOutputPath(...).SetCachePath(...).ApplyAll(cfg, paths).RunAsync()`
4. `ApplyAll` is an extension method that wraps every shared pipeline registration

## Differences vs the old layout

| Old (standalone projects) | New (editor-integrated) |
|---|---|
| One `Program.cs` per project | Single shared `CustomPipelines.cs` |
| `appsettings.json` for config | `sites/<name>/config.json` |
| `dotnet run` per statiq project | In-process `Bootstrapper.RunAsync()` |
| Templates under each project's `themes/` | Shared `themes/` at editor root |
| Each project has its own `output/` | `sites/<name>/output/` |
| Figure shortcode hardcodes inline style | `FigureStyle` field controls it (`custom-figure` / `inline-styled`) |
| `TagAutoLinkModule` lived at project root | `Statiq/TagAutoLinkModule.cs` |

## Adding a new custom module

Example — adding a `CodeHighlightModule`:

```csharp
// Statiq/CodeHighlightModule.cs
public class CodeHighlightModule : ParallelModule
{
    protected override async Task<IEnumerable<IDocument>> ExecuteInputAsync(
        IDocument input, IExecutionContext context)
    {
        // ... your logic ...
    }
}
```

Then append to `ApplyAll` in `CustomPipelines.cs`:

```csharp
pipeline.ProcessModules.Add(new CodeHighlightModule());
```

## Status

- ✅ `SiteConfig` + `CustomPipelines` + the two modules all extracted
- ⏳ Site-specific behaviour from the three sites' old `Program.cs` files is not yet overridden per site — everything is shared for now; add a `cfg` field when a site needs different behaviour