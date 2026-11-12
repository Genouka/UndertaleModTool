# 快速开始

## 1. 选择版本

本分支提供两类 GUI 和一类 CLI：

| 版本 | 适用平台 | 说明 |
|:--|:--|:--|
| **WPF GUI** | Windows 10/11（x64/x86） | 功能最完整，包含完整的 Butterscotch 调试器 |
| **Avalonia GUI** | Windows、Linux、macOS、Android | 跨平台；Android 支持内嵌运行游戏 |
| **CLI** | Windows、Linux、macOS | 无界面，适合批处理与 CI |

从 [Releases](https://github.com/Genouka/UndertaleModTool/releases) 下载：

- **Stable**：稳定版；
- **Nightly**：每次 master 更新自动构建，包含最新功能，可能不稳定。

各平台的具体安装方式见[桌面端与系统要求](docs/platforms.md)。

## 2. 打开数据文件

启动 GUI 后：

1. `文件 → 打开`，选择游戏目录中的 `data.win`（Linux/macOS 可能是 `data.unx`，Android 可能是 `data.droid`，部分游戏是 `.wad`）；
2. 左侧资源树会列出游戏对象、房间、精灵、代码等资源；
3. 双击资源即可编辑。

> 第一次打开大型游戏时，反编译和资源加载可能需要几十秒。请等待状态栏完成，不要在加载中强制退出。

## 3. 保存与备份

- `文件 → 保存` 覆盖当前文件；
- `文件 → 另存为` 输出到新文件；
- 建议始终保留一份原始数据文件，修改前先复制。

本分支的代码编辑器会跟踪未保存的修改，并在编辑器里显示修改标记；保存前请检查这些标记。

## 4. 常用入口

| 想做什么 | 去哪里 |
|:--|:--|
| 搜索代码 | `编辑 → 查找`（Ctrl+F）或 `替换`（Ctrl+H） |
| 看内置函数文档 | 把鼠标悬停在函数/常量上，或在设置中开启“悬浮信息” |
| 批量导入图片 | `文件` 菜单中的“导入图像”，见[批量图片导入](docs/tools/batch-image-import.md) |
| 从别的游戏导入资源 | `文件 → 从文件导入`，见[从其他数据文件导入](docs/tools/import-datafile.md) |
| 导入 yymps | `文件 → 从 YYMPs 包导入`，见[导入 yymps 包](docs/tools/yymps.md) |
| 直接运行游戏 | F6（Butterscotch），见[Butterscotch](docs/runtime/butterscotch.md) |
| 让 AI 客户端操作数据 | 启动 MCP 服务器，见[MCP](docs/mcp.md) |

> 菜单名称可能因界面语言而异。本文档以简体中文界面为准。

## 5. 语言与界面

- `设置 → 语言` 可以切换界面语言；
- 语言资源通过 Weblate 维护，缺失的翻译会回退到英文；
- 代码编辑器的主题等选项在设置中；自动换行/空白字符在编辑器顶部切换。

## 6. 下一步

- 想了解所有新增功能：[功能一览](README.md?id=功能一览)
- 想配置开发环境：[桌面端与系统要求](docs/platforms.md)
- 遇到问题：[常见问题](docs/faq.md)
