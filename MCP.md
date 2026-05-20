# MCP Server / MCP 服务器

## Install / 安装

Add in the configuration of MCP clients (such as Claude Desktop, VS Code Copilot, etc.):

在 MCP 客户端（如 Claude Desktop、VS Code Copilot 等）的配置中添加：

```json
{
  "mcpServers": {
    "undertale-mod": {
      "command": "dotnet",
      "args": ["run", "--project", "C:\\path\\to\\UndertaleModMcp"]
    }
  }
}
```

## MCP tools provided / 提供的 MCP 工具

- load_data_file / save_data_file / get_game_info / create_new_data_file
- decompile_code / disassemble_code / list_code_entries / replace_code / find_replace_code
- list_sprites / list_sounds / list_rooms / list_game_objects / get_sprite_info / get_room_info / export_texture
- search_strings / find_resource_by_name / search_code_text

## MCP resources provided / 提供的 MCP 资源

- gamedata://info — Basic Game Information 游戏基本信息 (JSON)
- gamedata://strings — String table 字符串表
- gamedata://code/{name} — Decompiled GML code 反编译的 GML 代码