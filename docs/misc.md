# 更新、本地化与致谢

## 自动检查更新

### 设置

打开 **设置**：

| 设置 | 默认 | 说明 |
|:--|:--|:--|
| 启动时自动检查更新 | 开 | 启动后异步检查；发现新版本才询问 |
| 更新通道 | 夜间版 | 稳定版 / 夜间版 |
| 下载源 | GitHub | GitHub / Mirror酱 |
| Mirror酱 CDK | 空 | 使用 Mirror酱时需要 |

### 通道

| 通道 | 来源 | 说明 |
|:--|:--|:--|
| **稳定版** | GitHub Releases 或 Mirror酱 | 正式发布，适合日常使用 |
| **夜间版** | GitHub Actions 最新成功构建 | master 每次更新，可能不稳定 |

- 夜间版按本地构建时间比较，只从 GitHub 下载；
- 选择 Mirror酱会强制切换到稳定版；
- 切换回夜间版会强制使用 GitHub；
- Mirror酱下载失败时会回退 GitHub 并提示；
- 更新由 `UndertaleModToolUpdater` 执行，应用重启后生效。

### 关闭

取消勾选 **启动时自动检查更新**。已经打开的更新提示可以直接关闭，不会强制更新。

### 手动检查

- WPF：`帮助 → 检查更新`（如果当前版本提供）；
- Avalonia：`帮助 → 检查更新`。

## 多语言与本地化

### 切换语言

**设置 → 语言** 下拉框：

| 选项 | 资源 |
|:--|:--|
| English | `Strings.resx` |
| 简体中文（zh-CN） | `Strings.zh-Hans.resx` |
| 日本語 | `Strings.ja.resx` |
| Português | `Strings.pt.resx` |
| 한국어 | `Strings.ko.resx` |
| Türkçe | `Strings.tr.resx` |
| العربية (ar-EG) | `Strings.ar-EG.resx` |

> 仓库中还有 `ru`、`fr` 资源，但当前语言下拉框未列出；是否启用未验证。

### 生效时机

- 设置窗口和大部分菜单会立即刷新；
- **已打开的标签页**需要重启应用后才能完全切换语言；
- 缺失的翻译会回退到英文；如果英文也没有，会直接显示资源键名。

### 参与翻译

翻译在 Weblate 上维护：

<https://hosted.weblate.org/engage/qiuutmtv4/>

可以在 Weblate 上直接建议或提交翻译，无需修改代码。翻译文件位于 `UndertaleModToolLocalization/Localization/`。

## 贡献者与翻译者

入口：`帮助 → 贡献者和翻译者`。

对话框提供三个链接：

| 按钮 | 目标 |
|:--|:--|
| 本分支代码贡献者 | `github.com/Genouka/UndertaleModTool/graphs/contributors` |
| QiuUTMTv4 上游贡献者 | `github.com/QiumingOrg/QiuUTMTv4/graphs/contributors` |
| 翻译者（Weblate） | `hosted.weblate.org/engage/qiuutmtv4/` |

链接使用系统默认浏览器打开。

## 上游同步

本分支会定期合并 [UnderminersTeam/UndertaleModTool](https://github.com/UnderminersTeam/UndertaleModTool) 的更新，通常不超过一周。同步内容包括：

- 数据格式支持；
- 反编译器/分析器修复；
- 原版资源编辑器改进；
- 依赖升级。

如果你发现某个原版功能在本分支缺失，先确认是否已同步到最新 nightly。

## 性能

性能优化、代码分析器和 GMRT 支持见[代码分析与 GMRT](docs/code-analysis.md)。

## 致谢

本分支建立在这些项目之上：

- [UndertaleModTool (UnderminersTeam)](https://github.com/UnderminersTeam/UndertaleModTool/) — 原项目
- [UndertaleModTool (luizzeroxis)](https://github.com/luizzeroxis/UndertaleModTool/) — Avalonia 桌面版分支
- [GUTMT4A (Genouka)](https://github.com/QiumingOrg/GUTMT4A) — 旧 MAUI Android 版
- [QiuUTMTv4 (Genouka)](https://github.com/QiumingOrg/QiuUTMTv4) — 旧 Avalonia Android 版
- [QiuMagickNet](https://github.com/orgs/QiuMagickNet/repositories) — Android 用 Magick.NET
- [SDL3-CS](https://github.com/edwardgushchin/SDL3-CS) — SDL3 C# 绑定

UndertaleModTool 原项目及 Undertale 相关素材版权归各自作者所有。本分支与本文档均为非官方内容。

## 相关页面

- [桌面端与系统要求](docs/platforms.md)
- [常见问题](docs/faq.md)
- [MCP 服务器](docs/mcp.md)
