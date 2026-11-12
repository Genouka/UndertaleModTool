# 命令行 CLI

`UndertaleModCli` 提供无界面的批处理能力，适合 CI、批量导出和脚本化修改。它与 GUI 共用 `UndertaleModLib` 和脚本引擎，因此 GUI 能跑的 `.csx` 脚本通常也能在 CLI 中运行。

## 下载与运行

CLI 随稳定版/nightly 发布包提供，支持 Windows、Linux、macOS（x64）。也可以从源码运行：

```bash
dotnet run --project UndertaleModCli -- <命令> [参数]
```

查看帮助：

```bash
UndertaleModCli --help
UndertaleModCli <命令> --help
```

> Windows 上直接双击无参数的 `UndertaleModCli.exe` 会提示改用 GUI；CLI 需要在终端中带参数运行。

## 命令一览

| 命令 | 作用 |
|:--|:--|
| `new` | 生成空白数据文件 |
| `load` | 加载数据文件并执行脚本/C# 代码/交互菜单 |
| `info` | 输出游戏名、GMS2/YYC、字节码版本和资源计数 |
| `dump` | 导出代码、字符串、纹理或精灵 |
| `replace` | 批量替换代码或纹理 |
| `project build` | 按 `project.json` 构建项目 |
| `project export` | 把资源导出到项目 |

通用参数：`-v/--verbose` 输出详细日志。

## new：新建空白数据文件

```bash
UndertaleModCli new -o blank.win
UndertaleModCli new -o blank.win -f          # 覆盖已存在文件
UndertaleModCli new - --stdout > blank.win   # 写到标准输出
```

| 参数 | 说明 |
|:--|:--|
| `-o, --output` | 输出路径 |
| `-f, --overwrite` | 覆盖目标文件 |
| `-` / `--stdout` | 把内容写到标准输出 |

## load：执行脚本或 C# 代码

```bash
UndertaleModCli load game.win -s patch1.csx patch2.csx -o modded.win
UndertaleModCli load game.win -l "Console.WriteLine(Data.GeneralInfo.Name);"
UndertaleModCli load game.win -i
```

| 参数 | 说明 |
|:--|:--|
| `<datafile>` | 输入数据文件 |
| `-s, --scripts` | 依次执行的 `.csx` 脚本 |
| `-l, --line` | 在脚本之后执行的一行 C# 代码 |
| `-o, --output` | 保存到指定路径 |
| `-f, --overwrite` | 覆盖目标文件 |
| `-i, --interactive` | 进入交互菜单 |

脚本宿主实现了与 GUI 相同的 `IScriptInterface`，可以使用 `Data`、`ScriptPath` 等对象。交互菜单提供：

1. 运行脚本；
2. 运行 C# 代码；
3. 原地覆盖保存；
4. 另存为；
5. 快速信息；
6. 不保存退出。

保存采用“写临时文件 + 原子替换”，中途失败不会留下半个文件。

## info：查看基本信息

```bash
UndertaleModCli info game.win
```

输出包括游戏名、是否 GMS2、是否 YYC、字节码版本以及精灵/声音/对象/房间/代码等资源数量。

## dump：导出资源

```bash
# 导出全部反编译代码到 CodeEntries/*.gml
UndertaleModCli dump game.win -c UMT_DUMP_ALL

# 只导出指定代码条目
UndertaleModCli dump game.win -c gml_Script_init_map gml_Script_reset_map

# 导出字符串、嵌入纹理、精灵
UndertaleModCli dump game.win -s -t --sprites

# 指定输出目录
UndertaleModCli dump game.win -c UMT_DUMP_ALL -o ./out
```

| 参数 | 说明 |
|:--|:--|
| `-o, --output` | 输出目录，默认是数据文件所在目录 |
| `-c, --code` | 代码条目名；`UMT_DUMP_ALL` 表示全部 |
| `-s, --strings` | 导出 `strings.txt` |
| `-t, --textures` | 导出嵌入纹理到 `EmbeddedTextures/*.png` |
| `--sprites` | 导出精灵到 `Sprites/*.png`（保留 padding） |

YYC 游戏没有可反编译的 GML，`-c` 会直接提示无代码可导出。全量反编译会并行执行，大游戏可能占用较多内存。

## replace：批量替换

```bash
# 替换单个代码条目
UndertaleModCli replace game.win -c "gml_Script_init_map=./newCode.gml" -o modded.win

# 用目录中的同名 .gml 替换全部代码
UndertaleModCli replace game.win -c "UMT_REPLACE_ALL=./code" -o modded.win

# 替换纹理页
UndertaleModCli replace game.win -t "Texture 0=./newTexture.png" -o modded.win
UndertaleModCli replace game.win -t "UMT_REPLACE_ALL=./textures" -o modded.win
```

| 参数 | 说明 |
|:--|:--|
| `-c, --code` | `代码名=文件路径`，可重复；`UMT_REPLACE_ALL=目录` |
| `-t, --textures` | `纹理名=PNG 路径`，可重复；`UMT_REPLACE_ALL=目录` |
| `-o, --output` | 保存路径 |

替换代码时会重新编译 GML；编译错误会阻止保存。碰撞事件名会自动创建/关联对象。

## project：项目构建与导出

```bash
# 构建
UndertaleModCli project build project.json -s base.win -d modded.win

# 导出资源到项目（支持 * 通配符）
UndertaleModCli project export project.json -s base.win -d work.win -a "Sprites/*" -a "Code/*"
```

`project export` 支持的资源类别包括：

- GameObjects
- Paths
- Code
- Scripts
- Sounds
- Rooms
- Backgrounds
- Sprites
- Sequences
- AnimationCurves
- Fonts
- Shaders

## 与 GUI 的差异

CLI **没有**：

- 资源编辑界面；
- Butterscotch 运行/调试；
- 多语言 GUI；
- 自动更新。

它专注于脚本执行、代码/纹理导入导出和项目构建。需要交互式编辑时请使用 GUI。

## 相关页面

- [桌面端与系统要求](docs/platforms.md)
- [快速开始](docs/getting-started.md)
- [常见问题](docs/faq.md)
