# MCP 服务器

`UndertaleModMcp` 是一个基于 Model Context Protocol 的 stdio 服务器，让 Claude Desktop、VS Code Copilot 等 AI 客户端读取、搜索和修改 GameMaker 数据文件。

> MCP 服务器直接读写磁盘文件，**没有路径白名单或操作确认机制**。只连接你信任的客户端，并在操作前备份数据文件。

## 构建与运行

要求 .NET 10 SDK。仓库没有为 MCP 提供单独的发布工作流，需要自行构建：

```bash
dotnet build UndertaleModMcp
dotnet run --project UndertaleModMcp
```

服务器通过 **stdout** 传输 MCP 协议，日志全部写入 **stderr**。不要在前面插入会写 stdout 的包装脚本。

发布为独立可执行文件（可选）：

```bash
dotnet publish UndertaleModMcp -c Release -r win-x64 --self-contained true -o out
```

## 客户端配置

在 MCP 客户端配置中添加：

```json
{
  "mcpServers": {
    "undertale-mod": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "C:\\path\\to\\UndertaleModTool\\UndertaleModMcp"
      ]
    }
  }
}
```

如果使用发布后的可执行文件，把 `command` 指向它，并去掉 `args` 中的 `run --project`。

### Claude Desktop

编辑 `claude_desktop_config.json`，加入上面的 `mcpServers` 配置，重启 Claude Desktop。

### VS Code Copilot

在 MCP 设置或 `.vscode/mcp.json` 中加入同一段配置。具体字段名以当前 VS Code 版本为准。

## 使用流程

1. 客户端调用 `load_data_file`，传入数据文件的**完整路径**；
2. 用 `get_game_info`、`list_*`、`read_decompiled_code`、`search_*` 等工具查看内容；
3. 用 `write_code`、`find_replace_code` 修改内存中的代码；
4. 用 `save_data_file` 落盘；
5. 需要图片时用 `export_texture` 导出 PNG。

一次会话只持有一个数据文件；再次 `load_data_file` 会释放旧文件。

## 工具

> 工具名由 SDK 按 snake_case 生成。`MCP.md` 中的部分名称已过时：`decompile_code` 实际为 `read_decompiled_code`，`replace_code` 实际为 `write_code`，`search_code_text` 实际为 `search_text_in_code`；`disassemble_code` 已被注释掉，不可用。

### 数据文件

| 工具 | 参数 | 用途 |
|:--|:--|:--|
| `load_data_file` | `filePath` | 加载 `data.win` / `.unx` / `.ios` / `.droid` / `.wad`，返回资源计数摘要 |
| `save_data_file` | `outputPath?` | 保存；省略时写回原路径。先写 `.temp` 再原子替换 |
| `get_game_info` | — | 返回游戏名、GMS2/YYC、字节码版本、各资源数量等 JSON |
| `create_new_data_file` | — | 在内存中新建空白数据文件 |

### 代码

| 工具 | 参数 | 用途 |
|:--|:--|:--|
| `read_decompiled_code` | `codeName` | 读取完整反编译 GML |
| `read_part_of_decompiled_code` | `codeName`, `startLine`, `lineCount` | 按行读取；越界返回空串 |
| `list_code_entries` | `filter?` | 列出代码条目名，支持不区分大小写的包含过滤 |
| `write_code` | `codeEntryName`, `gmlCode` | 编译并替换代码条目，返回 `{Success, Errors[]}` |
| `find_replace_code` | `codeEntryName`, `search`, `replacement`, `caseSensitive` | 在反编译文本中查找替换后重新编译 |

### 资源

| 工具 | 参数 | 用途 |
|:--|:--|:--|
| `list_sprites` | `filter?` | 精灵名与帧数 |
| `list_sounds` | — | 声音名 |
| `list_rooms` | — | 房间名 |
| `list_game_objects` | `filter?` | 游戏对象名 |
| `get_sprite_info` | `name` | 宽高、锚点、遮罩、帧数、播放速度 |
| `get_room_info` | `name` | 尺寸、Flags、持久、创建代码、对象/层/瓦片计数 |
| `export_texture` | `textureName`, `outputPath` | 导出 PNG，自动创建目录 |

### 搜索

| 工具 | 参数 | 用途 |
|:--|:--|:--|
| `search_strings` | `query` | 搜索字符串表，最多返回 100 条 |
| `find_resource_by_name` | `name` | 精确查找资源名，返回名称或 null |
| `search_text_in_code` | `query`, `maxResults` | 在全部代码中搜索，默认/上限 50 条；大游戏较慢 |

## 资源 URI

| URI | 内容 |
|:--|:--|
| `gamedata://info` | 游戏基本信息 JSON |
| `gamedata://strings` | 全部字符串（纯文本，大游戏可能很大） |
| `gamedata://code/{name}` | 指定代码条目的反编译 GML |

## 示例对话

> “加载 `C:\Games\MyGame\data.win`，列出所有以 `obj_` 开头的对象，并告诉我 `obj_player` 的事件代码。”

AI 客户端的调用顺序通常是：

1. `load_data_file`；
2. `list_game_objects({ filter: "obj_" })`；
3. `list_code_entries({ filter: "obj_player" })`；
4. `read_decompiled_code`。

修改代码时：

1. `read_decompiled_code` 确认原文；
2. `write_code` 写入新代码；
3. 检查返回的 `Errors`；
4. `save_data_file`。

## 安全与限制

- **无路径白名单**：`save_data_file` 和 `export_texture` 可以写客户端指定的任意可访问路径；
- **无确认机制**：AI 客户端可以直接覆盖数据文件；
- **单文件单会话**：所有操作串行化，一次只加载一个数据文件；
- **修改在内存中**：`write_code` / `find_replace_code` 不会立即落盘；
- **大文件性能**：`search_text_in_code` 会反编译全部代码，超大游戏可能很慢；
- **无 CI 发布**：需要自行构建；没有官方签名二进制；
- **Windows 7/32 位**：未验证；MCP 项目引用了 YY-Thunks，但不保证所有依赖在 Win7 可用。

## 故障排查

| 现象 | 处理 |
|:--|:--|
| 客户端看不到工具 | 确认命令路径正确；先手动运行 `dotnet run --project UndertaleModMcp` 看 stderr |
| 工具报 “No data file loaded” | 先调用 `load_data_file` |
| 保存失败 | 检查输出路径权限；新建数据必须提供路径 |
| 中文/路径含空格 | 使用 JSON 字符串并正确转义反斜杠 |
| 客户端卡住 | 确保没有其他进程占用 stdout；日志应在 stderr |

## 相关页面

- [命令行 CLI](docs/cli.md)
- [快速开始](docs/getting-started.md)
- [常见问题](docs/faq.md)
