# 常见问题

## 通用

### 这个分支和官方版有什么区别？

本分支在官方 UndertaleModTool 的基础上增加了：

- Windows / Linux / macOS / Android 的 Avalonia GUI；
- 更完整的代码编辑器（补全、诊断、悬浮文档、重构、代码片段）；
- 资源导入工具（其他数据文件、yymps、批量图片、纹理页合并）；
- Butterscotch 运行与调试；
- MCP 服务器；
- 多语言、自动更新、Windows 7/32 位支持等。

完整列表见[首页](README.md?id=功能一览)。

### 应该用 WPF 还是 Avalonia？

- Windows 上要完整功能（尤其 Butterscotch 调试、导入工具、重构）→ **WPF**；
- Linux/macOS/Android → **Avalonia**；
- 无界面/批处理 → **CLI**。

### 为什么 Avalonia 没有某个菜单？

很多功能目前只在 WPF 实现。本文档每页顶部都会标注支持的前端。Avalonia 缺少的功能包括：

- 从数据文件/yymps 导入、批量图片导入、纹理页迁移；
- 重构、包围模板、代码片段；
- 最近打开文件、标签页分离、独立编辑器主题；
- 资源树修改高亮；
- WPF 风格的独立调试窗口。

### 修改后游戏打不开/崩溃怎么办？

1. 立即用备份恢复；
2. 检查是否修改了字节码版本、纹理页尺寸、对象事件等关键字段；
3. 用官方 runner 或 Butterscotch 对比；
4. 逐项回滚最近的修改，定位问题。

**始终在副本上工作，并保留原始 `data.win`。**

## 安装与启动

### Windows 提示缺少 .NET

发布包是自包含的，通常不需要安装 .NET。如果提示缺少运行时，说明下载的是框架依赖版或解压不完整；重新下载对应发布包。

### Windows 7 能运行吗？

项目通过 YY-Thunks 和 `W7Compat.dll` 修补了已知的 Win7 缺失 API，但 .NET 10 官方不支持 Windows 7。能否在特定环境运行以实际测试为准；失败时请升级系统或使用其他平台。

### 32 位版在哪里？

WPF 发布目录中的 `UndertaleModTool_x86.exe`。Avalonia 桌面没有 x86 产物。

### Linux 上无法执行

```bash
chmod +x UndertaleModToolAvalonia_Desktop
```

macOS 如提示未验证开发者，右键 → 打开，或在系统设置中允许。

### Android 无法打开/保存文件

Android 10 需要先安装“存储权限助手”前置 APK，授权后再覆盖安装正式版。步骤见[Android 安装](docs/platforms.md?id=android-10-storage)。

## 编辑器

### 自动补全不弹出

- 确认当前是 GML 反编译页，且不是只读；
- 输入字母/数字/`_`，或输入 `.` 触发成员补全；
- 行内 `//` 注释后不触发；
- 没有 `Ctrl+Space` 手动触发。

### 诊断波浪线不出现

- 确认“自动检查错误”已开启；
- 只对顶层代码条目生效；
- 停止输入约 600ms 后才会分析；
- 解析上下文构建失败时不会产生诊断。

### 参数数量检查为什么没有？

参数数量检查只在 WPF 提供；Avalonia 只有“自动检查错误”。

### 悬浮信息不显示

- 确认“悬浮信息”已开启；
- 鼠标停留约 250ms；
- 只有语法高亮能识别为数字/标识符/函数时才显示；
- 数字没有对应资源或常量时不弹。

### 自动换行/空白字符的默认值在哪里设置？

当前设置窗口没有这两个控件。在编辑器顶部复选框或 WPF 编辑菜单中切换后，会写回 `settings.json`，作为后续编辑器的默认值。

### 最近打开的文件在哪里？

仅 WPF：`文件 → 最近打开`。最多 10 条，点击不存在的文件会提示并移除。

### 标签页能分离成窗口吗？

仅 WPF：右键标签 → **分离到新窗口**，或把标签拖出标签条。关闭子窗口后可用 `Ctrl+Shift+T` 恢复。说明页等 AutoClose 标签不能分离。

### 重构/代码片段在哪里？

仅 WPF：GML 反编译页右键 → **重构** / **用...包围** / **代码片段**。重命名只修改当前文档，不会自动更新其他代码条目中的引用。

## 数据与资源

### 从文件导入支持哪些资源？

声音、精灵、背景、路径、脚本、着色器、字体、时间线、游戏对象、房间、扩展、动画曲线、序列、粒子系统。代码条目不在列表中，但会作为对象事件的依赖被导入。

### yymps 为什么只导入了部分资源？

yymps 只支持精灵、声音、对象、脚本、字体。背景、房间、路径、着色器、序列等会被跳过，并在提示中列出数量。

### 批量图片导入没有识别到图片

- 只扫描 `.png` 和 `.gif`；
- 文件夹名需为 `sprites`/`backgrounds`/`fonts` 等，否则需勾选“将未知类型的图像作为精灵导入”；
- 扫描后需要点击“扫描”按钮。

### 纹理页迁移后贴图错位

完整重排会移动目标页原有条目；依赖硬编码坐标的 mod 可能受影响。从备份恢复，改用新建纹理页并手动调整。详见[纹理页迁移](docs/tools/texture-migration.md)。

## Butterscotch

### 菜单里没有 Butterscotch

- WPF 默认显示；
- Avalonia 桌面默认显示；
- Android 默认隐藏，需要在设置中勾选“显示 Butterscotch 调试选项”。

### 提示“内置 Butterscotch 调试器需要 bsdbg.dll”

缺少与进程架构匹配的原生库：

- Windows：检查 `native\win-x64` / `native\win-x86`；
- Linux/macOS：用 `tools/build-bsdbg.sh --backend sdl2` 构建；
- Android：换用包含对应 ABI `libbsdbg.so` 的 APK。

### 为什么不能调试外部架构？

只有“内置调试器（当前进程）”支持断点和单步；x64/x86/ARM64 外部运行器仅运行。切换到内置模式，或改用“运行”。

### 游戏启动后黑屏/立刻退出

可能使用了 YYC、GMRT、pre-WAD8 或不支持的扩展。Butterscotch 不是官方 runner 的完整替代品；先确认游戏能在官方 runner 中运行，再查看调试输出。

### 热重载没有生效

- 当前标签必须是代码条目；
- 会话必须处于暂停；
- 热重载只下发代码字节码和字符串字面量，不处理对象/事件/资源改动。

### Android 触摸没有反应

检查“触摸作为鼠标左键”是否开启；部分游戏只读取键盘/手柄。虚拟手柄和虚拟键盘可在设置中开关。

## MCP

### 客户端看不到工具

- 确认 `command`/`args` 路径正确；
- 手动运行 `dotnet run --project UndertaleModMcp` 查看 stderr；
- 确保没有其他进程占用 stdout。

### 工具报 “No data file loaded”

先调用 `load_data_file`，并传入完整路径。

### MCP 会直接修改我的文件吗？

`write_code` / `find_replace_code` 只改内存；`save_data_file` 才会落盘。但 MCP 没有路径白名单或确认机制，请只连接可信客户端并备份数据。

## 文档

### 文档里的功能在我的版本里没有

本文档对应当前 master/nightly。稳定版可能较旧；请更新到最新 nightly，或查看对应版本的发布说明。

### 如何参与翻译/贡献？

- 翻译：<https://hosted.weblate.org/engage/qiuutmtv4/>
- 代码：提交 PR 到 [Genouka/UndertaleModTool](https://github.com/Genouka/UndertaleModTool)。

## 相关页面

- [快速开始](docs/getting-started.md)
- [桌面端与系统要求](docs/platforms.md)
- [Butterscotch 运行与调试](docs/runtime/butterscotch.md)
