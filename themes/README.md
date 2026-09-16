# `themes/` — 共享模板目录（多网站共用）

所有站点的 Razor 模板（`_Layout.cshtml`, `_PostLayout.cshtml`, `_Sidebar.cshtml` 等）都放在这里。
Statiq.Web Bootstrapper 会把每个 site 的 `Theme` 字段指向的子目录加入 InputPaths，
模板里的 `@model` 和 `IExecutionContext.Settings` 都能正常拿到内容。

## 目录结构

```
themes/
├── README.md
├── _demo/                 ← 示例主题（用于 _demo site）
│   ├── _Layout.cshtml
│   ├── _PostLayout.cshtml
│   └── _Sidebar.cshtml
├── coderblog/             ← CoderBlog 主题（迁移后）
│   ├── _Layout.cshtml
│   ├── _PostLayout.cshtml
│   └── ...
├── editorial/             ← Tableware 主题（迁移后）
│   └── ...
└── default/               ← 通用默认主题
    └── ...
```

## 主题约定

每个主题目录应该至少包含：
- `_Layout.cshtml` — 全站主模板
- `_PostLayout.cshtml` — 文章页模板（被 `posts/...md` 用）
- `_Sidebar.cshtml` — 侧栏（被 `_Layout.cshtml` 部分引用）
- `_TagLayout.cshtml` — Tag 聚合页模板（被 Tags pipeline 用，可选）

## 主题 + Input 的关系

每个 site 的 inputPath 是 `sites/<site>/input/`，但 themePath 也是 inputPath。
Statiq 在解析 `_Layout.cshtml` 引用时会先找 site input 目录、再找 theme 目录。

所以：
- 主题里通用的 layout 放 `themes/<theme>/`
- 某个 site 独有的 layout 放 `sites/<site>/input/` 覆盖

## 从现有 statiq 项目迁移

| 源 | 目标 |
|---|---|
| `Coderblog.in/coderblog.statiq/themes/coderblog/` | `themes/coderblog/` |
| `Tableware.com/tableware.statiq/tableware.statiq.site/themes/editorial/` | `themes/editorial/` |
| `Tableware.com/tableware.statiq/tableware.statiq.site/themes/default/` | `themes/default/` |

⚠️ 迁移时各项目里 themes 子目录如果有重名（如 coderblog 项目里有 `themes/coderblog/` 和 `themes/default/`），
按主题功能命名整理到统一目录。Statiq.Web 模板引用建议用相对路径，不要假设 themes/ 嵌套层数。

## 状态

- ✅ 目录骨架已建好
- ⏳ 现有 3 个网站的 themes/ 未迁移（待用户手动迁移测试）