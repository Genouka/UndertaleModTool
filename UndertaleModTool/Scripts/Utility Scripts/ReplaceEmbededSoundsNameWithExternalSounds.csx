using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Scripting;

EnsureDataLoaded();

string musicFolder = PromptChooseDirectory();
if (musicFolder is null)
{
    throw new ScriptCancelledException("The music folder was not selected.");
}

string[] audioExtensions = { ".mp3", ".flac", ".ogg", ".wav", ".opus", ".wma", ".aac", ".m4a", ".mid", ".midi" };

HashSet<string> externalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (string file in Directory.GetFiles(musicFolder))
{
    string ext = Path.GetExtension(file).ToLowerInvariant();
    if (audioExtensions.Contains(ext))
    {
        string nameWithoutExt = Path.GetFileNameWithoutExtension(file);
        if (!string.IsNullOrEmpty(nameWithoutExt))
            externalNames.Add(nameWithoutExt);
    }
}

if (externalNames.Count == 0)
{
    ScriptMessage("No audio files found in the selected folder.");
    return;
}

List<(UndertaleSound sound, string oldName, string newName)> toRename = new List<(UndertaleSound, string, string)>();

foreach (UndertaleSound sound in Data.Sounds)
{
    if (sound?.Name?.Content == null)
        continue;

    string soundName = sound.Name.Content;

    if (!externalNames.Contains(soundName))
        continue;

    string newName = "qm" + soundName;

    if (Data.Sounds.Any(s => s?.Name?.Content == newName))
    {
        ScriptWarning($"Sound '{newName}' already exists, skipping '{soundName}'.");
        continue;
    }

    toRename.Add((sound, soundName, newName));
}

if (toRename.Count == 0)
{
    ScriptMessage("No matching sounds found to rename.");
    return;
}

string preview = "The following sounds will be renamed:\n\n";
foreach (var item in toRename)
{
    preview += $"  {item.oldName}  ->  {item.newName}\n";
}
preview += $"\nTotal: {toRename.Count} sound(s)";

if (!ScriptQuestion(preview + "\n\nProceed?"))
    return;

SetProgressBar(null, "Renaming sounds...", 0, toRename.Count);
StartProgressBarUpdater();

int renamedCount = 0;
foreach (var (sound, oldName, newName) in toRename)
{
    sound.Name = Data.Strings.MakeString(newName);

    if (sound.File?.Content != null && sound.File.Content.Contains(oldName))
    {
        sound.File = Data.Strings.MakeString(sound.File.Content.Replace(oldName, newName));
    }

    renamedCount++;
    IncrementProgress();
}

await StopProgressBarUpdater();
HideProgressBar();

ScriptMessage($"Done! Renamed {renamedCount} sound(s).");
