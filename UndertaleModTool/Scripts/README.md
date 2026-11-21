# Script metadata

Scripts (.csx files in this folder and its subfolders) can start with a metadata comment block, in
the style of userscript headers. The Scripts menu uses it to show script names (optionally
localized), and the tooltip shows the description.

```csharp
// ==UndertaleModTool==
// @name        Import GML
// @name:zh-Hans 导入 GML
// @name:zh-CN  导入 GML
// @description Imports all .gml files from a folder into code entries.
// @description:zh-Hans 将文件夹中的所有 .gml 文件批量导入为代码条目。
// @author      Jockeholm, Samuel Roy
// ==/UndertaleModTool==

// (script code continues here)
```

## Rules

- The block must be the first non-empty content of the file, and it is closed by the matching
  `// ==/UndertaleModTool==` line.
- `// ==UserScript==` / `// ==/UserScript==` are accepted as an alias for the header tags.
- Keys are case-insensitive; the last occurrence of a key/locale wins.
- Locale tags use BCP-47 style (`zh-Hans`, `zh-CN`, `ja`, `pt-BR`, ...); `_` is accepted instead of `-`.
- Keys without a locale are the English/default values.

## Name resolution

The "Script name display" setting in the settings window picks one of:

- **File name** - the `.csx` file name (default; the original behavior).
- **English name** - the `@name` value, or the file name when the script has no metadata.
- **Localized name** - the `@name:<locale>` value matching the current UI language (exact locale,
  then parent cultures, then the plain language, then any same-language variant), falling back to
  `@name` and then the file name.

Other keys (such as `@author` or `@version`) are stored and available to tooling but are not
currently shown in the menu.

## Cross-platform forms (QiuForms)

Scripts that need more than `ScriptMessage` / `SimpleTextInput` can build a `QiuForm` instead of
creating WinForms or WPF windows. The same script then renders on the WPF tool, Avalonia desktop,
Android, and the CLI:

```csharp
using QiuForms;

QiuForm form = new("Import options");
QiuCheckBox special = form.Check("Special type", false);
QiuNumberBox speed = form.Number("Animation speed", 1, minimum: 0, maximum: 100);
speed.EnabledWhen(() => special.Value);
speed.Validate(_ => speed.Value > 0 ? null : "Animation speed must be greater than zero.");

if (ShowQiuForm(form) != QiuDialogResult.Submit)
    return;

float value = (float)speed.Value;
```

- `ShowQiuForm` blocks until the window closes and may be called from any thread.
- Hosts without a UI report `SupportsQiuForms == false`; check it if a script must work there.
- The model is toolkit-neutral: see `QiuForms/README.md` for the full list of controls, layout rules
  and threading rules.

See `Special Scripts/QiuForms Demo.csx` for a runnable example.
