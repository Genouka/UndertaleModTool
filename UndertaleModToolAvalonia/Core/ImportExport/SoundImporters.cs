using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
using static UndertaleModLib.Models.UndertaleSound;

namespace UndertaleModToolAvalonia;

public partial class ImportExportService
{
    /// <summary>Imports a folder of sounds, with options for embedding/audiogroups.</summary>
    public async void ImportSounds()
    {
        EnsureDataLoaded();

        int maxCount = 1;
        int audioID = -1;
        int audioGroupID = -1;
        int embAudioID = -1;
        bool usesAGRP = (Data.AudioGroups.Count > 0);

        if (!usesAGRP)
            ScriptWarning("This game doesn't use audiogroups.\nImporting to external audiogroups is disabled.");

        string importFolder = PromptChooseDirectory();
        if (importFolder is null)
            throw new Exception("The import folder was not set.");

        string[] dirFiles = Directory.GetFiles(importFolder);
        string folderName = new DirectoryInfo(importFolder).Name;

        bool emergencyCancel = !ScriptQuestion("This script imports sounds in bulk. Do you wish to continue?");
        if (emergencyCancel)
            return;

        bool replaceSoundPropertiesCheck = ScriptQuestion("If a sound already exists in the game, it will be replaced instead of added. Would you like to replace the sound properties as well?");

        bool GeneralSound_embedSound = false;
        bool GeneralSound_decodeLoad = false;
        bool GeneralSound_needAGRP = false;
        bool manuallySpecifyEverySound = !ScriptQuestion(
            "Would you like to automatically specify the characteristics of all sounds?\n" +
            "If you select no, you will have to manually specify each sound.");
        if (!manuallySpecifyEverySound)
        {
            GeneralSound_embedSound = ScriptQuestion("Do you want to keep your OGG files external or internal?\nNo - keep it external\nYes - embed sound into the game (use responsibly!)");
            if (GeneralSound_embedSound)
            {
                GeneralSound_decodeLoad = ScriptQuestion("Do you want to Uncompress sounds on load? (Higher Memory, low CPU)");
            }
            if (GeneralSound_embedSound && Data.AudioGroups.Count > 0)
            {
                GeneralSound_needAGRP = ScriptQuestion($"Your last folder name is \"{folderName}\".\nDo you want to treat it as the audiogroup name?\n(Answer No to use \"audiogroup_default\" instead)");
            }
            ScriptMessage("If a sound already exists in the game, it will be replaced instead of added.");
        }

        maxCount = dirFiles.Length;
        SetProgressBar(null, "Importing sounds", 0, maxCount);

        foreach (string file in dirFiles)
        {
            IncProgressLocal(maxCount);

            string filename = Path.GetFileName(file);
            if (!(filename.EndsWith(".ogg", StringComparison.InvariantCultureIgnoreCase) || filename.EndsWith(".wav", StringComparison.InvariantCultureIgnoreCase)))
            {
                // Ignore invalid file extensions.
                continue;
            }
            string soundName = Path.GetFileNameWithoutExtension(file);
            bool isOGG = Path.GetExtension(filename).ToLower() == ".ogg";
            bool embedSound = false;
            bool decodeLoad = false;
            if (isOGG && manuallySpecifyEverySound)
            {
                embedSound = ScriptQuestion("Your sound appears to be an OGG.\nNo - keep it external\nYes - embed sound into the game (use responsibly!)");
                decodeLoad = false;
                if (embedSound)
                {
                    decodeLoad = ScriptQuestion("Do you want to Uncompress this sound on load? (Higher Memory, low CPU)");
                }
            }
            else if (isOGG && !manuallySpecifyEverySound)
            {
                embedSound = GeneralSound_embedSound;
                decodeLoad = GeneralSound_decodeLoad;
            }
            else
            {
                // WAV cannot be external
                embedSound = true;
                decodeLoad = false;
            }
            string audioGroupName = "";
            bool needAGRP = false;
            audioGroupID = -1;
            embAudioID = -1;

            // Search for an existing sound with the given name.
            UndertaleSound existingSound = null;
            for (var i = 0; i < Data.Sounds.Count; i++)
            {
                if (Data.Sounds[i]?.Name?.Content == soundName)
                {
                    existingSound = Data.Sounds[i];
                    if (manuallySpecifyEverySound)
                    {
                        ScriptMessage($"Sound \"{existingSound.Name.Content}\" already exists in the game; it will be replaced instead of added.");
                    }
                    break;
                }
            }

            // Try to find an audiogroup, if needed.
            if (embedSound && usesAGRP)
            {
                if (manuallySpecifyEverySound)
                {
                    needAGRP = ScriptQuestion($"Your last folder name is \"{folderName}\".\nDo you want to treat it as the audiogroup name?\n(Answer No to use \"audiogroup_default\" instead)");
                }
                else
                {
                    needAGRP = GeneralSound_needAGRP;
                }
                if (needAGRP)
                {
                    audioGroupName = folderName;

                    if (audioGroupID == -1)
                    {
                        // Find the audio group we need.
                        for (int i = 0; i < Data.AudioGroups.Count; i++)
                        {
                            if (Data.AudioGroups[i]?.Name?.Content == audioGroupName)
                            {
                                audioGroupID = i;
                                break;
                            }
                        }

                        // Still -1? Create a new one...
                        if (audioGroupID == -1)
                        {
                            audioGroupID = Data.AudioGroups.Count;
                            string dataDir = Path.GetDirectoryName(FilePath) ?? "";
                            File.WriteAllBytes(Paths.JoinVerifyWithinDirectory(dataDir, $"audiogroup{audioGroupID}.dat"), Convert.FromBase64String("Rk9STQwAAABBVURPBAAAAAAAAAA="));

                            MainThreadAction(() =>
                            {
                                Data.AudioGroups.Add(new UndertaleAudioGroup()
                                {
                                    Name = Data.Strings.MakeString(audioGroupName)
                                });
                            });
                        }
                    }
                }
            }

            // If the audiogroup ID is for the builtin audiogroup ID, it's embedded in the main data file and doesn't need to be loaded.
            if (audioGroupID == Data.GetBuiltinSoundGroupID())
            {
                needAGRP = false;
            }

            // Create embedded audio entry if required.
            if (embedSound)
            {
                UndertaleEmbeddedAudio soundData = new() { Data = File.ReadAllBytes(file) };

                // Update data file with new embedded audio, or update the sound's external audio group file if needed.
                if (needAGRP)
                {
                    // Load audiogroup into memory.
                    UndertaleData audioGroupDat;
                    string relativeAudioGroupPath;
                    if (audioGroupID < Data.AudioGroups.Count && Data.AudioGroups[audioGroupID] is UndertaleAudioGroup { Path.Content: string customRelativePath })
                    {
                        relativeAudioGroupPath = customRelativePath;
                    }
                    else
                    {
                        relativeAudioGroupPath = $"audiogroup{audioGroupID}.dat";
                    }
                    string audioGroupPath = Paths.JoinVerifyWithinDirectory(Path.GetDirectoryName(FilePath), relativeAudioGroupPath);
                    UndertaleData audioGroupDatInner;
                    using (FileStream audioGroupReadStream = new(audioGroupPath, FileMode.Open, FileAccess.Read))
                    {
                        audioGroupDatInner = UndertaleIO.Read(audioGroupReadStream);
                    }
                    audioGroupDat = audioGroupDatInner;

                    MainThreadAction(() =>
                    {
                        soundData.Name = new UndertaleString("EmbeddedSound " + audioGroupDat.EmbeddedAudio.Count.ToString());
                        audioGroupDat.EmbeddedAudio.Add(soundData);
                        if (existingSound is not null)
                        {
                            audioGroupDat.EmbeddedAudio.Remove(existingSound.AudioFile);
                        }
                    });
                    audioID = audioGroupDat.EmbeddedAudio.Count - 1;

                    // Write audio group back to disk.
                    using FileStream audioGroupWriteStream = new(audioGroupPath, FileMode.Create);
                    UndertaleIO.Write(audioGroupWriteStream, audioGroupDat);
                }
                else
                {
                    // Update data file's embedded audio.
                    MainThreadAction(() =>
                    {
                        soundData.Name = new UndertaleString("EmbeddedSound " + Data.EmbeddedAudio.Count.ToString());
                        Data.EmbeddedAudio.Add(soundData);
                        if (existingSound is not null)
                        {
                            Data.EmbeddedAudio.Remove(existingSound.AudioFile);
                        }
                    });
                    embAudioID = Data.EmbeddedAudio.Count - 1;
                }
            }

            // Determine sound flags.
            AudioEntryFlags flags = AudioEntryFlags.Regular;
            if (isOGG && embedSound && decodeLoad)
            {
                flags = AudioEntryFlags.IsEmbedded | AudioEntryFlags.IsCompressed | AudioEntryFlags.Regular;
            }
            if (isOGG && embedSound && !decodeLoad)
            {
                flags = AudioEntryFlags.IsCompressed | AudioEntryFlags.Regular;
            }
            if (!isOGG)
            {
                flags = AudioEntryFlags.IsEmbedded | AudioEntryFlags.Regular;
            }
            if (isOGG && !embedSound)
            {
                flags = AudioEntryFlags.Regular;
                audioID = -1;
            }

            // Determine final embedded audio reference (or null).
            UndertaleEmbeddedAudio finalAudioReference = null;
            if (!embedSound)
            {
                finalAudioReference = null;
            }
            if (embedSound && !needAGRP)
            {
                finalAudioReference = Data.EmbeddedAudio[embAudioID];
            }
            if (embedSound && needAGRP)
            {
                finalAudioReference = null;
            }

            // Determine final audio group reference (or null).
            UndertaleAudioGroup finalGroupReference = null;
            if (!usesAGRP)
            {
                finalGroupReference = null;
            }
            else
            {
                finalGroupReference = needAGRP ? Data.AudioGroups[audioGroupID] : Data.AudioGroups[Data.GetBuiltinSoundGroupID()];
            }

            // Update/create actual sound asset.
            MainThreadAction(() =>
            {
                if (existingSound is null)
                {
                    UndertaleSound newSound = new()
                    {
                        Name = Data.Strings.MakeString(soundName),
                        Flags = flags,
                        Type = isOGG ? Data.Strings.MakeString(".ogg") : Data.Strings.MakeString(".wav"),
                        File = Data.Strings.MakeString(filename),
                        Effects = 0,
                        Volume = 1.0f,
                        Pitch = 1.0f,
                        AudioID = audioID,
                        AudioFile = finalAudioReference,
                        AudioGroup = finalGroupReference,
                        GroupID = needAGRP ? audioGroupID : Data.GetBuiltinSoundGroupID()
                    };
                    Data.Sounds.Add(newSound);
                    Project?.MarkAssetForExport(newSound);
                }
                else if (replaceSoundPropertiesCheck)
                {
                    existingSound.Flags = flags;
                    existingSound.Type = isOGG ? Data.Strings.MakeString(".ogg") : Data.Strings.MakeString(".wav");
                    existingSound.File = Data.Strings.MakeString(filename);
                    existingSound.Effects = 0;
                    existingSound.Volume = 1.0f;
                    existingSound.Pitch = 1.0f;
                    existingSound.AudioID = audioID;
                    existingSound.AudioFile = finalAudioReference;
                    existingSound.AudioGroup = finalGroupReference;
                    existingSound.GroupID = needAGRP ? audioGroupID : Data.GetBuiltinSoundGroupID();
                    Project?.MarkAssetForExport(existingSound);
                }
                else
                {
                    existingSound.AudioFile = finalAudioReference;
                    existingSound.AudioID = audioID;
                    Project?.MarkAssetForExport(existingSound);
                }
            });
        }

        HideProgressBar();
        ScriptMessage("Sounds added successfully!");
    }

    private void IncProgressLocal(int maxCount)
    {
        if (GetProgress() < maxCount)
        {
            IncrementProgress();
        }
    }

    /// <summary>Imports a single sound file (WAV/OGG).</summary>
    public void ImportSingleSound()
    {
        EnsureDataLoaded();

        int audioID = -1;
        int audioGroupID = -1;
        int embAudioID = -1;
        bool usesAGRP = (Data.AudioGroups.Count > 0);

        if (!usesAGRP)
            ScriptWarning("This game doesn't use audiogroups.\nImporting to external audiogroups is disabled.");

        string soundPath = PromptLoadFile("");
        if (string.IsNullOrEmpty(soundPath))
            return;

        // Determine basic sound name properties.
        string filename = Path.GetFileName(soundPath);
        string soundName = Path.GetFileNameWithoutExtension(soundPath);
        bool isOGG = Path.GetExtension(soundPath).ToLower() == ".ogg";
        bool embedSound = false;
        bool decodeLoad = false;
        if (isOGG)
        {
            embedSound = ScriptQuestion("Your sound appears to be an OGG.\nNo - keep it external\nYes - embed sound into the game (use responsibly!)");
            decodeLoad = false;
            if (embedSound)
            {
                decodeLoad = ScriptQuestion("Do you want to Uncompress this sound on load? (Higher Memory, low CPU)");
            }
        }
        else
        {
            // How can a .wav be external?
            embedSound = true;
            decodeLoad = false;
        }
        string audioGroupName = "";
        string folderName = Path.GetFileName(Path.GetDirectoryName(soundPath));
        bool needAGRP = false;

        // Search for an existing sound with the given name.
        UndertaleSound existingSound = null;
        bool replaceSoundPropertiesCheck = false;
        for (int i = 0; i < Data.Sounds.Count; i++)
        {
            if (Data.Sounds[i]?.Name?.Content == soundName)
            {
                existingSound = Data.Sounds[i];
                replaceSoundPropertiesCheck = ScriptQuestion($"Sound \"{existingSound.Name.Content}\" already exists in the game; it will be replaced instead of added. Would you like to replace the sound properties as well?");
                break;
            }
        }

        // Try to find an audiogroup, when not updating an existing sound.
        if (embedSound && usesAGRP && existingSound is null)
        {
            needAGRP = ScriptQuestion($"Your last folder name is \"{folderName}\".\nDo you want to treat it as the name of the sound's audiogroup?\n(Answer No to use \"audiogroup_default\" instead)");
        }
        if (needAGRP && usesAGRP && embedSound)
        {
            audioGroupName = folderName;

            // Find the audio group we need.
            for (int i = 0; i < Data.AudioGroups.Count; i++)
            {
                if (Data.AudioGroups[i]?.Name?.Content == audioGroupName)
                {
                    audioGroupID = i;
                    break;
                }
            }
            if (audioGroupID == -1)
            {
                // Still -1? Create a new one...
                string dataDir = Path.GetDirectoryName(FilePath) ?? "";
                File.WriteAllBytes(Paths.JoinVerifyWithinDirectory(dataDir, $"audiogroup{Data.AudioGroups.Count}.dat"), Convert.FromBase64String("Rk9STQwAAABBVURPBAAAAAAAAAA="));
                UndertaleAudioGroup newAudioGroup = new()
                {
                    Name = Data.Strings.MakeString(audioGroupName),
                };
                Data.AudioGroups.Add(newAudioGroup);
                audioGroupID = Data.AudioGroups.Count - 1;
            }
        }

        // If this is an existing sound, use its audio group ID.
        if (existingSound is not null)
        {
            audioGroupID = existingSound.GroupID;
        }

        // If the audiogroup ID is for the builtin audiogroup ID, it's embedded in the main data file and doesn't need to be loaded.
        if (audioGroupID == Data.GetBuiltinSoundGroupID())
        {
            needAGRP = false;
        }

        // Create embedded audio entry if required.
        UndertaleEmbeddedAudio soundData = null;
        if ((embedSound && !needAGRP) || needAGRP)
        {
            soundData = new UndertaleEmbeddedAudio() { Data = File.ReadAllBytes(soundPath) };
            Data.EmbeddedAudio.Add(soundData);
            if (existingSound is not null)
            {
                Data.EmbeddedAudio.Remove(existingSound.AudioFile);
            }
            embAudioID = Data.EmbeddedAudio.Count - 1;
        }

        // Update external audio group file if required.
        if (needAGRP)
        {
            // Load audiogroup into memory.
            UndertaleData audioGroupDat;
            string relativeAudioGroupPath;
            if (audioGroupID < Data.AudioGroups.Count && Data.AudioGroups[audioGroupID] is UndertaleAudioGroup { Path.Content: string customRelativePath })
            {
                relativeAudioGroupPath = customRelativePath;
            }
            else
            {
                relativeAudioGroupPath = $"audiogroup{audioGroupID}.dat";
            }
            string audioGroupPath = Paths.JoinVerifyWithinDirectory(Path.GetDirectoryName(FilePath), relativeAudioGroupPath);
            using (FileStream audioGroupReadStream = new(audioGroupPath, FileMode.Open, FileAccess.Read))
            {
                audioGroupDat = UndertaleIO.Read(audioGroupReadStream);
            }

            // Add the EmbeddedAudio entry to the audiogroup data.
            audioGroupDat.EmbeddedAudio.Add(soundData);
            if (existingSound is not null)
            {
                audioGroupDat.EmbeddedAudio.Remove(existingSound.AudioFile);
            }
            audioID = audioGroupDat.EmbeddedAudio.Count - 1;

            // Write audio group back to disk.
            using FileStream audioGroupWriteStream = new(audioGroupPath, FileMode.Create);
            UndertaleIO.Write(audioGroupWriteStream, audioGroupDat);
        }

        // Determine sound flags.
        AudioEntryFlags flags = AudioEntryFlags.Regular;
        if (isOGG && embedSound && decodeLoad)
        {
            flags = AudioEntryFlags.IsEmbedded | AudioEntryFlags.IsCompressed | AudioEntryFlags.Regular;
        }
        if (isOGG && embedSound && !decodeLoad)
        {
            flags = AudioEntryFlags.IsCompressed | AudioEntryFlags.Regular;
        }
        if (!isOGG)
        {
            flags = AudioEntryFlags.IsEmbedded | AudioEntryFlags.Regular;
        }
        if (isOGG && !embedSound)
        {
            flags = AudioEntryFlags.Regular;
            audioID = -1;
        }

        // Determine final embedded audio reference (or null).
        UndertaleEmbeddedAudio finalAudioReference = null;
        if (!embedSound)
        {
            finalAudioReference = null;
        }
        if (embedSound && !needAGRP)
        {
            finalAudioReference = Data.EmbeddedAudio[embAudioID];
        }
        if (embedSound && needAGRP)
        {
            finalAudioReference = null;
        }

        // Determine final audio group reference (or null).
        UndertaleAudioGroup finalGroupReference = null;
        if (!usesAGRP)
        {
            finalGroupReference = null;
        }
        else
        {
            finalGroupReference = needAGRP ? Data.AudioGroups[audioGroupID] : Data.AudioGroups[Data.GetBuiltinSoundGroupID()];
        }

        // Update/create actual sound asset.
        if (existingSound is null)
        {
            UndertaleSound newSound = new()
            {
                Name = Data.Strings.MakeString(soundName),
                Flags = flags,
                Type = isOGG ? Data.Strings.MakeString(".ogg") : Data.Strings.MakeString(".wav"),
                File = Data.Strings.MakeString(filename),
                Effects = 0,
                Volume = 1.0f,
                Pitch = 1.0f,
                AudioID = audioID,
                AudioFile = finalAudioReference,
                AudioGroup = finalGroupReference,
                GroupID = needAGRP ? audioGroupID : Data.GetBuiltinSoundGroupID()
            };
            Data.Sounds.Add(newSound);
            Project?.MarkAssetForExport(newSound);
            ChangeSelection(newSound);
        }
        else if (replaceSoundPropertiesCheck)
        {
            existingSound.Flags = flags;
            existingSound.Type = isOGG ? Data.Strings.MakeString(".ogg") : Data.Strings.MakeString(".wav");
            existingSound.File = Data.Strings.MakeString(filename);
            existingSound.Effects = 0;
            existingSound.Volume = 1.0f;
            existingSound.Pitch = 1.0f;
            existingSound.AudioID = audioID;
            existingSound.AudioFile = finalAudioReference;
            existingSound.AudioGroup = finalGroupReference;
            existingSound.GroupID = needAGRP ? audioGroupID : Data.GetBuiltinSoundGroupID();
            Project?.MarkAssetForExport(existingSound);
            ChangeSelection(existingSound);
        }
        else
        {
            existingSound.AudioFile = finalAudioReference;
            existingSound.AudioID = audioID;
            Project?.MarkAssetForExport(existingSound);
            ChangeSelection(existingSound);
        }

        ScriptMessage("Sound added successfully!");
    }
}