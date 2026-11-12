<div class="hero">
  <span class="hero-kicker">Genouka Fork · 非官方分支</span>
  <h1>UndertaleModTool 分支文档</h1>
  <p>面向 Windows、Linux、macOS 与 Android 的 GameMaker 数据文件编辑工具。这里记录本分支相对官方版新增的全部功能，以及每项功能的详细用法。</p>
  <div class="hero-actions">
    <a href="#/docs/getting-started.md">开始使用</a>
    <a class="ghost" href="#/docs/editor/intellisense.md">浏览功能</a>
    <a class="ghost" href="https://github.com/Genouka/UndertaleModTool/releases/latest">下载最新版</a>
  </div>
</div>

## 这个分支是什么

UndertaleModTool 是用于查看、编辑和反编译 GameMaker 游戏数据文件（`data.win`、`data.unx`、`.wad` 等）的工具。本仓库是 [UnderminersTeam/UndertaleModTool](https://github.com/UnderminersTeam/UndertaleModTool) 的非官方分支，在保留原版能力的基础上：

- 同时维护 **Windows / Linux / macOS / Android** 的 GUI（Avalonia）和 **Windows / Linux / macOS** 的 CLI；
- 新增大量编辑器、资源导入、纹理页与调试工具；
- 提供面向 AI 客户端的 **MCP 服务器**；
- 支持用 **Butterscotch** 运行时直接运行和调试游戏；
- 持续同步上游代码。

> 本文档只描述本分支新增或明显增强的功能。原版已有的资源编辑器、脚本系统等基础能力，请参阅[官方文档](https://github.com/UnderminersTeam/UndertaleModTool/wiki)。

## 功能一览

<div class="feature-grid">
  <div class="feature-card">
    <h3><a href="#/docs/editor/intellisense.md">智能代码编辑</a></h3>
    <p>自动补全、实时语法与参数检查、内置函数文档、精灵与数字悬浮预览。</p>
  </div>
  <div class="feature-card">
    <h3><a href="#/docs/editor/search-and-options.md">搜索、替换与编辑体验</a></h3>
    <p>全字匹配与全局替换、自动换行、空白字符显示、最近文件、双色主题和增强右键菜单。</p>
  </div>
  <div class="feature-card">
    <h3><a href="#/docs/editor/refactor-snippets.md">重构与代码片段</a></h3>
    <p>重命名资源/变量/函数、if 与 switch 互转、包围模板，以及可自定义的 GML 代码片段。</p>
  </div>
  <div class="feature-card">
    <h3><a href="#/docs/editor/tabs-and-tracking.md">多窗口与修改跟踪</a></h3>
    <p>把标签页分离为独立窗口，并直观看到哪些代码行被修改过。</p>
  </div>
  <div class="feature-card">
    <h3><a href="#/docs/tools/import-datafile.md">跨数据文件导入</a></h3>
    <p>从另一份 data 文件导入资源，支持选择资源类型与处理重名冲突。</p>
  </div>
  <div class="feature-card">
    <h3><a href="#/docs/tools/yymps.md">yymps 包导入</a></h3>
    <p>直接导入 GameMaker 本地资源包，把精灵、对象、房间等资源合并进当前项目。</p>
  </div>
  <div class="feature-card">
    <h3><a href="#/docs/tools/batch-image-import.md">批量图片导入</a></h3>
    <p>按命名规则批量替换精灵、背景和字体图像，自动处理纹理页。</p>
  </div>
  <div class="feature-card">
    <h3><a href="#/docs/tools/texture-migration.md">纹理页迁移</a></h3>
    <p>重新打包纹理页、合并或拆分贴图，解决纹理页数量与体积问题。</p>
  </div>
  <div class="feature-card">
    <h3><a href="#/docs/runtime/butterscotch.md">Butterscotch 运行与调试</a></h3>
    <p>不离开编辑器直接运行游戏，支持断点、单步、变量编辑、调用栈与热重载。</p>
  </div>
  <div class="feature-card">
    <h3><a href="#/docs/runtime/butterscotch-avalonia.md">Avalonia 与 Android 内嵌运行</a></h3>
    <p>在桌面和 Android 应用内嵌游戏画面，Android 提供虚拟手柄、键盘与触摸映射。</p>
  </div>
  <div class="feature-card">
    <h3><a href="#/docs/mcp.md">MCP 服务器</a></h3>
    <p>让 Claude Desktop、VS Code Copilot 等 AI 客户端读取、搜索和修改游戏数据。</p>
  </div>
  <div class="feature-card">
    <h3><a href="#/docs/code-analysis.md">更强的代码分析器</a></h3>
    <p>类型传播与常量折叠带来更准确的反编译结果，并支持 GMRT VM。</p>
  </div>
</div>

## 支持的平台

| 平台 | GUI | CLI | 说明 |
|:--|:--:|:--:|:--|
| Windows 10/11 x64 | ✅ | ✅ | WPF 与 Avalonia 两种前端 |
| Windows 7 / 32 位 | ✅ | — | WPF x86 单文件包，详见[系统要求](docs/platforms.md?id=win7-32bit) |
| Linux x64 | ✅ | ✅ | Avalonia 前端 |
| macOS Intel / Apple Silicon | ✅ | ✅ | Avalonia 前端 |
| Android 9+（API 28+） | ✅ | — | 内嵌 Butterscotch 运行时，详见 [Android](docs/platforms.md?id=android) |

## 怎么读这套文档

1. 第一次使用：从[快速开始](docs/getting-started.md)下载对应平台的包并打开数据文件。
2. 想找某个功能：用左上角搜索，或从上面的功能卡片进入。
3. 遇到问题：先看[常见问题](docs/faq.md)，再对照具体功能页的“限制与排查”一节。

<p class="doc-meta">本分支与本文档均为非官方内容。UndertaleModTool 原项目及 Undertale 相关素材版权归各自作者所有。</p>
