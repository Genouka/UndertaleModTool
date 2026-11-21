# QiuForms

A toolkit-neutral, declarative form model for UndertaleModTool scripts (`.csx`).

A script builds a `QiuForm` once; each host renders it with its own UI toolkit:

| Host | Renderer |
|---|---|
| `UndertaleModTool` (WPF, Windows) | `WpfQiuFormsHost` |
| `UndertaleModToolAvalonia` (desktop) | `AvaloniaQiuFormsHost` |
| `UndertaleModToolAvalonia.Android` | the same Avalonia host, shown as a modal page by the simulated window stack |
| `UndertaleModCli` | `ConsoleQiuFormsHost` |
| Android cross-app API, project builds, tests | no UI: `SupportsQiuForms` is `false` |

This assembly has **no UI toolkit dependency**: it only describes controls and values. Everything
that touches WPF, Avalonia, or the console lives in the host.

## Example

```csharp
using QiuForms;

QiuForm form = new("Import options");
form.Message = "These settings apply to every sprite in this import.";

QiuCheckBox special = form.Check("Special sprite", false);
QiuNumberBox version = form.Number("Special version", 1, min: 1, max: 9999, integer: true);
QiuNumberBox speed = form.Number("Animation speed", 1, min: 0, max: 100);
QiuChoice playback = form.Choice("Playback", ["Frames per second", "Frames per game frame"], 1);

version.EnabledWhen(() => special.Value);
speed.EnabledWhen(() => special.Value);
speed.Validate(_ => speed.Value > 0 ? null : "Animation speed must be greater than zero.");

if (ShowQiuForm(form) != QiuDialogResult.Submit)
    return;

float selectedSpeed = (float)speed.Value;
```

## Controls

Fields (validated on submit, may be conditionally enabled/visible):

`QiuTextBox`, `QiuNumberBox`, `QiuCheckBox`, `QiuChoice` (drop-down or radio group), `QiuSlider`.

Elements:

`QiuLabel`, `QiuSeparator`, `QiuButton`, `QiuImage`, `QiuListBox`, `QiuTreeView`, `QiuCanvas`.

Containers:

`QiuPanel` (vertical/horizontal flow), `QiuGroup` (titled box).

`QiuCanvas` is the escape hatch for custom-drawn UIs (pixel editors, previews). Scripts implement
`Paint` with the toolkit-neutral drawing primitives of `IQiuCanvasContext` and react to
`PointerPressed` / `PointerMoved` / `PointerReleased`; hosts translate pointer positions into canvas
coordinates. The CLI host has no canvas and ignores it.

## Rules

- **Layout is flow-based.** There are no absolute positions, so the same form works on a desktop
  window and on a phone screen.
- **`ShowQiuForm` blocks** until the form closes and may be called from any thread. The host is
  responsible for creating the window on its UI thread and for waking the script afterwards.
- **Field events run on the host's UI thread**, while the script is blocked inside `ShowQiuForm`.
  Do not block inside them.
- **The model is not thread-safe.** Build it before showing it; mutate it only from field events.
- **Validation runs on submit.** `QiuForm.Validate()` fills `QiuField.Error`; a host keeps the form
  open and focuses `FirstInvalid` when validation fails.
- **Hosts without a UI** must leave `IQiuFormsHost.SupportsQiuForms` at `false`. Calling
  `ShowQiuForm` there throws `QiuFormUnavailableException`.

## License

QiuForms is licensed under the **Mozilla Public License 2.0** (`LICENSE` in this folder), separately
from the GPLv3-licensed UndertaleModTool. MPL-2.0 is explicitly compatible with the GNU GPL
(MPL-2.0 §3.3): the tool as a whole can be distributed under GPLv3 while this library's source files
stay under MPL-2.0. Every source file carries the Exhibit A notice.
