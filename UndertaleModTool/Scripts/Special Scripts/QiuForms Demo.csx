// ==UndertaleModTool==
// @name        QiuForms Demo
// @name:zh-Hans QiuForms 演示
// @name:zh-CN  QiuForms 演示
// @description Exercises the cross-platform QiuForms API on every host.
// @description:zh-CN 在所有宿主上演示跨平台 QiuForms 接口。
// ==/UndertaleModTool==

using QiuForms;

QiuForm form = new("QiuForms Demo");
form.Message = "This window is generated from the same script on WPF, Avalonia, Android and the CLI.";

form.Label("Basic fields", heading: true);

QiuTextBox name = form.Text("Name", "spr_demo", id: "name");
name.Validate(_ => name.Value.Length >= 3 ? null : "The name must be at least 3 characters long.");

QiuCheckBox advanced = form.Check("Show advanced options", false, id: "advanced");

QiuNumberBox speed = form.Number("Animation speed", 1, minimum: 0, maximum: 100, decimals: 2, id: "speed");
speed.EnabledWhen(() => advanced.Value);

QiuChoice playback = form.Choice("Playback", ["Frames per second", "Frames per game frame"], 1, id: "playback");
playback.VisibleWhen(() => advanced.Value);

form.Separator();

form.Button("Fill defaults", _ =>
{
    name.SetValueFromHost("spr_default");
    advanced.SetValueFromHost(true);
    speed.SetValueFromHost(2);
}, id: "defaults");

if (ShowQiuForm(form) != QiuDialogResult.Submit)
{
    ScriptMessage("QiuForms demo cancelled.");
    return;
}

ScriptMessage(
    $"QiuForms demo submitted:\n" +
    $"  Name: {name.Value}\n" +
    $"  Advanced: {advanced.Value}\n" +
    $"  Speed: {speed.Value}\n" +
    $"  Playback: {playback.SelectedItem}");