# 把现有 3 个独立 statiq 项目迁到编辑器内置的 sites/

编辑器的内置 Statiq.Web 已经能跑通了（`_demo` site build exit=0，所有 pipeline 通过）。
接下来你可以手动把现有的 3 个独立项目搬过来 —— 这份文档告诉你要怎么放。

## TL;DR

```
/Volumes/Software/MyWebSites/Coderblog.in/coderblog.statiq/        →   /Volumes/Software/MyWebSites/statiq_markdown_editor/sites/coderblog/
/Volumes/Software/MyWebSites/Coderblog.in/coderblog.statiq/themes/ →   /Volumes/Software/MyWebSites/statiq_markdown_editor/themes/coderblog/
（其他两个项目同上）
```

**3 个文件改名 + 1 个新字段**：appsettings.json → config.json，2 字段重命名，2 字段新增。

## 完整迁移步骤

### 1. 复制主题（themes/ 是共享的）

```bash
# Coderblog 主题
cp -R /Volumes/Software/MyWebSites/Coderblog.in/coderblog.statiq/themes/coderblog \
      /Volumes/Software/MyWebSites/statiq_markdown_editor/themes/coderblog

# Tableware 主题（双主题：editorial + default）
cp -R /Volumes/Software/MyWebSites/Tableware.com/tableware.statiq/tableware.statiq.site/themes/editorial \
      /Volumes/Software/MyWebSites/statiq_markdown_editor/themes/editorial
cp -R /Volumes/Software/MyWebSites/Tableware.com/tableware.statiq/tableware.statiq.site/themes/default \
      /Volumes/Software/MyWebSites/statiq_markdown_editor/themes/default

# WinsonInvest 主题（如有）
# 类似处理
```

### 2. 创建 site 目录 + 复制 input

```bash
# Coderblog site
mkdir -p /Volumes/Software/MyWebSites/statiq_markdown_editor/sites/coderblog
cp -R /Volumes/Software/MyWebSites/Coderblog.in/coderblog.statiq/input \
      /Volumes/Software/MyWebSites/statiq_markdown_editor/sites/coderblog/input
```

### 3. 写 config.json（替代 appsettings.json）

**CoderBlog** (`sites/coderblog/config.json`)：

```json
{
  "Theme": "themes/coderblog",
  "Host": "coderblog.in",
  "StripMonthFromPostUrls": true,
  "FigureStyle": "custom-figure",
  "GoogleAnalyticsId": "G-EGJWR1M53K",
  "AdSenseId": "",
  "UmamiSiteId": "0199868d-e5d0-437c-8f59-255d39754643",
  "UmamiUrl": "https://analysis.tableware.com",
  "UseHtmlExtensions": true,
  "LinkHideExtensions": false
}
```

**Tableware** (`sites/tableware/config.json`)：

```json
{
  "Theme": "themes/editorial",
  "Host": "www.tableware.com",
  "StripMonthFromPostUrls": false,
  "FigureStyle": "inline-styled",
  "GoogleAnalyticsId": "G-FW9TL14GX9",
  "AdSenseId": "ca-pub-6528553816796071",
  "UseHtmlExtensions": true,
  "LinkHideExtensions": false
}
```

### 4. 字段映射对照表

| appsettings.json (旧) | config.json (新) | 备注 |
|---|---|---|
| `Theme` | `Theme` | 路径改成相对编辑器根 |
| `Host` | `Host` | 同名 |
| `GoogleAnalyticsId` | `GoogleAnalyticsId` | 同名 |
| `GoogleAdSenseId` | `AdSenseId` | **重命名**（去掉 Google 前缀） |
| `UseExtensions` | `UseHtmlExtensions` | **重命名**（更明确） |
| `LinkHideExtensions` | `LinkHideExtensions` | 同名 |
| `UmamiSiteId` / `UmamiUrl` | `UmamiSiteId` / `UmamiUrl` | 同名 |
| — | `StripMonthFromPostUrls` | **新增**：`true` for CoderBlog/WinStock，`false` for Tableware |
| — | `FigureStyle` | **新增**：`custom-figure` for CoderBlog/WinStock，`inline-styled` for Tableware |

### 5. 检查主题里的相对路径

Coderblog 主题用了相对路径引用其他主题文件（`@RenderPartial("_Sidebar.cshtml")` 等）。
这些相对路径在搬过来后保持工作，因为主题还是同一目录结构。

### 6. 测试 build

启动编辑器，跑 build：
```
POST /api/sites/coderblog/build
```
看 `sites/coderblog/output/` 是否生成了网站。

### 7. 跑通后切换 Settings 项目

Settings UI 暂时还在用"Legacy project root"指向旧的 statiq 项目。
迁移完成后，我把 Settings 加 "Editor-managed sites" 选项，让你能从列表里切换。
在那之前先用 API 或直接改 `appsettings.json` 验证。

## 已知差异（不影响功能）

1. **Statiq 默认 cache 路径** —— 不再放到 `sites/<name>/cache/`，会放到编辑器根的 `cache/`。可以改但默认行为可接受。
2. **Temp 路径** —— 同样放到编辑器根的 `temp/`（每次 build 自动清理）。
3. **Statiq CLI 命令行参数** —— 旧项目里 `dotnet run -- -l Debug` 这种参数没用上。CustomPipelines 全在进程内配置。
4. **Sitemap URL 不完全同步** —— 已知 bug：sitemap 里 `posts/2026-09/foo.html` 但实际写入 `posts/foo.html`（StripMonth 只影响 PostProcess，没影响 Sitemap pipeline 的 URL 计算）。属于 polish 阶段。

## 时间预算

每个网站：
- 复制 themes + input：5 分钟
- 写 config.json：2 分钟
- 测 build + fix 配置：5 分钟
- 总计 ~15 分钟/site

3 个网站共 ~45 分钟。