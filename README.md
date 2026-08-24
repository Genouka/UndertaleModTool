# UndertaleModTool (Genouka Fork)

![GitHub Release](https://img.shields.io/github/v/release/genouka/UndertaleModTool?style=flat) [![GitHub](https://img.shields.io/github/license/genouka/UndertaleModTool?logo=github)](https://github.com/UnderminersTeam/UndertaleModTool/blob/master/LICENSE.txt)
![GitHub Repo stars](https://img.shields.io/github/stars/genouka/UndertaleModTool?style=flat) [![翻译状态](https://hosted.weblate.org/widget/qiuutmtv4/svg-badge.svg)](https://hosted.weblate.org/engage/qiuutmtv4/) [![Static Badge](https://img.shields.io/badge/Bilibili-%E7%A7%8B%E5%86%A5%E6%95%A3%E9%9B%A8__GenOuka-purple?style=flat-square)](https://space.bilibili.com/3493116076100126) [![Static Badge](https://img.shields.io/badge/Discord-qiuming__official-purple?style=flat-square)](https://discord.com/users/1124397340627845200)

**This is an unofficial fork of UndertaleModTool!**

**这是一个非官方的UndertaleModTool分支。**

This repository maintains versions for four platforms: Windows, Linux, MacOS, and Android. 

本仓库同时维护 Windows、Linux、MacOS、Android 四个平台的版本。

## Download / 下载

|  Releases (发布包)   | Link / State (链接/状态) 	                                                                                                                                                       |
|:-----------------:|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
|   Stable (稳定版)    | [![Latest Stable Release](https://img.shields.io/github/downloads/genouka/UndertaleModTool/latest/total)](https://github.com/genouka/UndertaleModTool/releases/latest)       |
|   Nightly (每夜版)   | [![Latest Stable Release](https://img.shields.io/github/downloads/genouka/UndertaleModTool/nightly/total)](https://github.com/genouka/UndertaleModTool/releases/tag/nightly) |
| Test Build (测试构建) | [下载(Download)](https://github.com/Genouka/UndertaleModTool/actions/workflows/build_test_apk.yml)                                                                             |

If you are looking for the official version instead of the version I forked, please go to [here](https://github.com/UnderminersTeam/UndertaleModTool/)

如果你在找官方的版本而不是我Fork的版本，请前往[这里](https://github.com/UnderminersTeam/UndertaleModTool/)

[QQ群](https://qm.qq.com/q/V1LyuIu3IY) |  [哔哩哔哩](https://space.bilibili.com/3493116076100126)

## What do I change?/我做了什么修改？

- Avalonia App for Windows/Linux/MacOS/Android 跨平台支持

- Add the support of multi-language and localization. 添加多语言和本地化支持
  
  [Participate in localizing translations on Weblate!](https://hosted.weblate.org/engage/qiuutmtv4/)
  
  ![Preview](images/preview/image-2.png)

- Allow to import resources from other datafiles. 允许从其它数据文件导入资源
  
  ![Preview](images/preview/image-3.png)

  ![Preview](images/preview/image-4.png)

- Add floating information panel documentation for built-in functions, constants, and variables. 添加悬浮信息面板可以显示内置的函数、变量、常量的简易文档
  
  ![Preview](images/preview/image.png)

  ![Preview](images/preview/image-1.png)

- Add floating information panel documents for sprites, numbers, etc. 添加悬浮面板快速预览所悬停内容（如精灵图、数字字面量）的信息
  
  ![Preview](images/preview/image-5.png)
  
  ![Preview](images/preview/image-6.png)

- Add control options for automatic line wrapping and displaying whitespace characters to the code editor. They can also be configured with default values from the settings. 为代码编辑器添加自动换行和显示空白字符的控制选项，也可以从设置窗口配置默认值。
  
  ![Preview](images/preview/image-7.png)

  ![Preview](images/preview/image-8.png)

- Support displaying recently opened files (automatically excluding invalid files). 支持显示最近打开的文件（自动排除无效文件）
  
  ![Preview](images/preview/image-9.png)
  
- Better search-and-replace panel in code editor (Ctrl+F or Ctrl+H). 更好的代码编辑器搜索替换面板(Ctrl+F 或 Ctrl+H)。

  ![Preview](images/preview/image-10.png)

- Added full word matching, replacement, and global replacement functions to the search code panel. 为搜索代码的面板添加了全字匹配功能、替换和全局替换功能。

  ![Preview](images/preview/image-11.png)

- Support separating tabs into sub windows (drag and drop tab titles outside the window, or right-click on tab titles and click 'Separate to New Window') 支持将标签页分离为子窗口(直接拖拽标签页标题到窗口外，或者右键标签页标题点击分离到新窗口)
  
  ![Preview](images/preview/image-12.png)

- Data modification tracking. 数据修改跟踪
  
  ![Preview](images/preview/image-13.png)

- Add the built-in batch image import tool. 添加内置的图片批量导入工具

  ![Preview](images/preview/image-14.png)

- Add the texture page migration tool. 添加纹理页迁移工具

  ![Preview](images/preview/image-15.png)

  ![Preview](images/preview/image-16.png)

  ![Preview](images/preview/image-17.png)

  ![Preview](images/preview/image-18.png)

- Improved performance, reduced memory usage, optimized UI thread lag. 提高了性能，减少内存占用，优化了UI线程卡顿。

- Support to use as a MCP Server for AI calling. See [this](MCP.md) for detail. 支持作为MCP服务器使用，点[这里](MCP.md)查看文档。

- Auto-complete code 自动补全代码

  ![Preview](images/preview/img.png)

- Real-time static error checking and parameter matching checking 实时静态错误检查和参数匹配检查

  ![Preview](images/preview/img_1.png)

- Code editor dual-color theme and its settings 代码编辑器双色主题及其设置

    ![Preview](images/preview/img_2.png)
    
    ![Preview](images/preview/img_3.png)

- Better context menu 更好的上下文菜单

  ![Preview](images/preview/img_4.png)

  ![Preview](images/preview/img_5.png)

- Auto check for updates (can close) 自动检查更新功能（可以关闭）

- A more detailed and accurate code analyzer (type propagation and constant folding) 更详细准确的代码分析器（类型传播和常量展开）

- Actively synchronize upstream code, usually no longer than a week 积极同步上游代码，通常不会超过一周

## 鸣谢/Thanks
如果没有以下项目作为基础，本项目将永远不会诞生！

Without the following projects as a foundation, this project would never have been born!

- [UndertaleModTool(UnderminersTeam)](https://github.com/UnderminersTeam/UndertaleModTool/) Original version of UndertaleModTool
- [UndertaleModTool(luizzeroxis)](https://github.com/luizzeroxis/UndertaleModTool/) Avalonia version for desktop
- [GUTMT4A(Genouka)](https://github.com/QiumingOrg/GUTMT4A) Android version(v3)
- [QiuUTMTv4(Genouka)](https://github.com/QiumingOrg/QiuUTMTv4) Android version(v4)
- [QiuMagickNet(Genouka)](https://github.com/orgs/QiuMagickNet/repositories) Build `Magick.NET` nupkgs for Android.
  
## 捐赠/Donate

Wechat/微信:
![mm_reward_qrcode](https://github.com/user-attachments/assets/8f442af8-fba5-41fb-ac19-0977744520a0)
