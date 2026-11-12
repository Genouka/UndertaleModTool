# 桌面端与系统要求

本分支同时维护 WPF 与 Avalonia 两套 GUI。它们共享大部分数据层能力，但界面和功能覆盖不同。

## 该选哪个版本

| 你的需求 | 推荐版本 |
|:--|:--|
| Windows 10/11，需要完整功能与 Butterscotch 调试器 | **WPF GUI** |
| Windows 7 或 32 位 Windows | **WPF x86** |
| Linux / macOS | **Avalonia GUI** |
| Android 手机/平板 | **Avalonia Android** |
| 批处理、CI、无界面 | **CLI** |
| 让 AI 客户端操作数据 | **MCP 服务器** |

## 下载

从 [Releases](https://github.com/Genouka/UndertaleModTool/releases) 下载：

- **Stable（稳定版）**：正式发布，适合日常使用；
- **Nightly（夜间版）**：master 每次更新自动构建，包含最新功能，可能不稳定。

发布包通常包含：

| 产物 | 内容 |
|:--|:--|
| `UndertaleModTool_<version>-Windows.zip` | WPF x64 |
| `UndertaleModTool_<version>-Windows-SingleFile.zip` | WPF 单文件 |
| `UndertaleModToolAvalonia_<version>-Windows.zip` | Avalonia Windows |
| `...-Linux.zip` | Avalonia Linux x64 |
| `...-macOS.zip` / `...-macOS-x86.zip` | Avalonia macOS ARM64 / Intel |
| `...-Android-arm64.zip` / `...-Android-x64.zip` | Android APK |
| `...-Android-<arch>-Preinstall-Signed.apk` | Android 10 存储助手 |
| CLI 产物 | Windows / Linux / macOS |

> 实际文件名以对应 Release 为准。Nightly 使用 `nightly` 前缀。

## Windows（WPF）

### 要求

- Windows 10/11 x64；
- 解压后运行 `UndertaleModTool.exe`；
- 发布包已自带 .NET 运行时，无需单独安装 .NET。

<a id="win7-32bit"></a>

### 32 位与 Windows 7

- 发布目录中同时包含 `UndertaleModTool.exe`（x64）和 `UndertaleModTool_x86.exe`（x86 单文件）；
- 32 位系统请运行 `UndertaleModTool_x86.exe`；
- 项目通过 YY-Thunks 和 `W7Compat.dll` 修补了部分 Windows 7 缺失 API（例如 `GetSystemTimePreciseAsFileTime`），用于兼容 Magick.NET 原生库；
- **注意**：.NET 10 官方不支持 Windows 7，仓库只针对已知问题做了兼容处理。能否在特定 Win7 环境运行仍以实际测试为准；如失败，请使用较新的 Windows 或 Avalonia/CLI 方案。

### 功能覆盖

WPF 是功能最完整的前端，包含：

- 完整的 Butterscotch 调试器（断点、变量、实例、调用栈、输出、热重载）；
- 从其他数据文件导入、yymps 导入、批量图片导入、纹理页迁移；
- 最近打开文件、标签页分离、独立代码编辑器主题；
- 重构、包围模板、GML 代码片段；
- 数据修改跟踪（资源树 + 代码行）。

## Linux / macOS（Avalonia）

### 要求

- Linux x64，或 macOS Intel / Apple Silicon；
- 解压后运行 `UndertaleModToolAvalonia_Desktop`（Linux 文件名不带 `.Desktop` 后缀）；
- 发布包为自包含单文件；首次启动会解包内容到临时目录，因此启动略慢。

### 功能差异

Avalonia 具备：

- 数据文件打开/保存、资源编辑；
- 自动补全、实时诊断、悬浮信息；
- 搜索替换、自动换行、空白字符；
- 反汇编断点、停止行高亮；
- 代码行修改高亮；
- 自动更新、贡献者对话框、多语言。

Avalonia **没有**：

- 从数据文件/yymps 导入、批量图片导入、纹理页迁移；
- 重构、包围模板、代码片段；
- 最近打开文件、标签页分离、独立编辑器主题；
- 资源树修改高亮；
- WPF 风格的完整调试窗口（桌面端没有调试控制条）。

### Butterscotch 原生库

Linux/macOS 发布包**不保证**包含 `libbsdbg`。如果运行游戏时提示内置运行器不可用：

```bash
tools/build-bsdbg.sh --backend sdl2
```

构建后把产物放到 `UndertaleModRunner/native/linux-<arch>/` 或 `native/osx-<arch>/`，再重新启动应用。

<a id="android"></a>

## Android（Avalonia）

### 要求

- Android 9（API 28）或更高；
- 与设备架构匹配的 APK：`arm64`（绝大多数手机）或 `x64`（模拟器/部分平板）；
- 应用名：**QiuUTMTv5**。

### 普通安装

1. 下载对应架构的 APK；
2. 允许“安装未知应用”；
3. 安装并打开；
4. 授予文件访问权限；
5. 打开 `data.win` / `data.droid` 等数据文件。

<a id="android-10-storage"></a>

### Android 10 额外步骤

Android 10 的存储模型需要前置包：

1. 下载并安装 **存储权限助手 Preinstall APK**（与正式版同包名、同版本号）；
2. 打开助手，允许其申请的存储权限；
3. **直接覆盖安装**正式版 APK，中途不要卸载；
4. 重新打开应用。

前置包只用于把 Android 10 切回旧版存储模型；覆盖安装正式版后它会被替换。

### Android 上的 Butterscotch

- “显示 Butterscotch 调试选项”默认关闭，需要在设置中打开；
- 游戏画面显示在应用内的 **Butterscotch** 标签页；
- 支持触摸映射、虚拟手柄、虚拟键盘、全屏；
- 详见 [Avalonia 与 Android 内嵌运行](docs/runtime/butterscotch-avalonia.md)。

## 从源码构建

### 通用要求

- .NET 10 SDK；
- Git 子模块：`git submodule update --init --recursive`；
- Windows 构建 Butterscotch 还需要 CMake/Ninja/VS C++ 工具链（或用仓库内预编译 DLL）。

### WPF（Windows）

```powershell
dotnet publish UndertaleModTool/UndertaleModTool.csproj -c Release -r win-x64 --self-contained true
```

或使用发布脚本同时生成 x64/x86：

```powershell
tools/publish-gui.ps1 -Output build/publish-final -SelfContained -SingleFile
```

### Avalonia 桌面

```bash
dotnet publish UndertaleModToolAvalonia.Desktop -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -o out
```

RID 可选：`win-x64`、`linux-x64`、`osx-arm64`、`osx-x64`。

### Android

```bash
dotnet workload install android
dotnet publish UndertaleModToolAvalonia.Android -c Release -r android-arm64 \
  --self-contained true -p:AndroidPreinstallChannel=stable
```

需要 JDK 17 和 Android SDK。低内存机器可加 `-m:1`；AOT 构建可能占用数 GB 内存。

### CLI

```bash
dotnet publish UndertaleModCli -c Release -r win-x64 --self-contained true -o out
```

RID 可选：`win-x64`、`linux-x64`、`osx-arm64`、`osx-x64`。

### Butterscotch 原生库

| 平台 | 命令 |
|:--|:--|
| Windows | `tools/build-bsdbg.ps1`（构建时也会自动触发） |
| Linux/macOS | `tools/build-bsdbg.sh --backend sdl2` |
| Android | `tools/build-bsdbg-android.sh`（NDK r26+/r27c） |

## 常见问题

| 现象 | 处理 |
|:--|:--|
| 双击没有反应 | 确认解压完整；Windows 可能需要解除文件锁定；从终端运行看错误 |
| 打开数据文件很慢 | 大型游戏首次反编译需要时间；等待状态栏完成 |
| 32 位版内存不足 | 大游戏建议使用 x64；32 位进程可用内存有限 |
| Linux 上无法执行 | `chmod +x UndertaleModToolAvalonia_Desktop` |
| macOS 提示未验证开发者 | 右键 → 打开，或在系统设置中允许 |
| Android 无法访问文件 | 按 Android 10 步骤安装前置包，或授予“所有文件访问”权限 |
| Android 安装包解析失败 | 下载与设备架构匹配的 APK；确认系统版本 ≥ 9 |

## 相关页面

- [快速开始](docs/getting-started.md)
- [命令行 CLI](docs/cli.md)
- [Butterscotch 运行与调试](docs/runtime/butterscotch.md)
- [常见问题](docs/faq.md)
