# `Statiq/` — 共享自定义 pipeline 模块

3 个 statiq 网站项目共用的 pipeline 模块，从原本各自的 `Program.cs` 里抽出，参数化后放在这里。

## 文件清单

| 文件 | 说明 |
|---|---|
| `SiteConfig.cs` | 单个 site 的配置类（Theme/Host/FigureStyle/StripMonthFromPostUrls 等） |
| `CustomPipelines.cs` | 进程内 `Bootstrapper` 配置：SetDestination、Tags pipeline、Figure shortcode、RSS/SEO metadata、Draft filter、http→https、Sitemap |
| `TagAutoLinkModule.cs` | 自动给文章里出现的 tag 名加 `<a>` 链接到 `/tag/<slug>.html` |
| `ExternalLinkTargetModule.cs` | 站外链接自动加 `target="_blank" rel="noopener noreferrer"` |

## 接入流程

1. `StatiqRunner.BuildAsync(siteName)` 读取 `sites/<name>/config.json` 得到 `SiteConfig`
2. 用 `cfg.ResolvePaths(editorRoot)` 算出 `SitePaths`（input/output/cache/theme 物理路径）
3. `Bootstrapper.Factory.CreateWeb().SetOutputPath(...).SetCachePath(...).ApplyAll(cfg, paths).RunAsync()`
4. `ApplyAll` 是扩展方法，封装了所有 shared pipeline 注册

## 与现有项目的差异

| 旧（独立项目） | 新（编辑器内置） |
|---|---|
| `Program.cs` 每个项目一份 | `CustomPipelines.cs` 共用一份 |
| `appsettings.json` 配置 | `sites/<name>/config.json` |
| `dotnet run` 跑 statiq | 进程内 `Bootstrapper.RunAsync()` |
| 模板在项目 `themes/` 下 | 共享 `themes/`（编辑器根） |
| 每个项目独立 `output/` | `sites/<name>/output/` |
| Figure shortcode 写死 inline style | `FigureStyle` 字段控制（custom-figure / inline-styled） |
| TagAutoLinkModule 在项目根 | `Statiq/TagAutoLinkModule.cs` |

## 加新自定义模块的流程

例如要加 `CodeHighlightModule`：

```csharp
// Statiq/CodeHighlightModule.cs
public class CodeHighlightModule : ParallelModule
{
    protected override async Task<IEnumerable<IDocument>> ExecuteInputAsync(
        IDocument input, IExecutionContext context)
    {
        // ... 你的逻辑 ...
    }
}
```

然后在 `CustomPipelines.cs` 的 `ApplyAll` 末尾加上：

```csharp
pipeline.ProcessModules.Add(new CodeHighlightModule());
```

## 状态

- ✅ `SiteConfig` + `CustomPipelines` + 两个 Module 全部抽完
- ⏳ 现有 3 个网站的 Program.cs 里的 site-specific 行为未做 per-site override（目前是共用一份，如果某个 site 需要不同行为加 cfg 字段即可）