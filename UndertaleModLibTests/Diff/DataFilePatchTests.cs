using System;
using System.IO;
using System.Linq;
using UndertaleModLib;
using UndertaleModLib.Diff;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
using Xunit;

namespace UndertaleModLibTests.Diff;

public class DataFilePatchTests
{
    [Fact]
    public void Apply_ReconstructsCompareFile_FromSpriteCodeObjectAndSoundChanges()
    {
        (UndertaleData baseData, UndertaleData compareData) = BuildDataPair();

        string patchPath = Path.Combine(Path.GetTempPath(), $"qiuumtdiff-test-{Guid.NewGuid():N}.qiuumtdiff");
        try
        {
            DataFileDiffResult diff = new DataFileDiffer(baseData, compareData).Compare();
            // Added: obj_added plus its Create code and the extra Destroy code of obj_test.
            // Removed: obj_removed plus its Create code. Changed: sprite, sound, path, object,
            // and the changed GML code entry.
            System.Text.StringBuilder diffText = new();
            foreach (AssetCategoryDiff category in diff.Categories)
            {
                foreach (AssetDiffEntry entry in category.Entries)
                {
                    if (entry.Change != AssetChangeKind.Unchanged)
                    {
                        diffText.AppendLine($"{category.Id}/{entry.Name}: {entry.Change}");
                    }
                }
            }
            Assert.True(diff.AddedCount == 5 && diff.RemovedCount == 2 && diff.ChangedCount == 11,
                $"expected +5 -2 ~11, got +{diff.AddedCount} -{diff.RemovedCount} ~{diff.ChangedCount}\n{diffText}");

            new DataFilePatchBuilder(baseData, compareData).Build(diff, patchPath, "base", "compare");
            Assert.True(File.Exists(patchPath));

            // Apply to a fresh copy of the base data.
            UndertaleData patched = Reload(baseData);
            DataFilePatchApplyResult applyResult = new DataFilePatchApplier(patched)
                .Apply(patchPath, new DataFilePatchApplyOptions { DeleteRemoved = true });

            Assert.True(applyResult.Errors.Count == 0, string.Join(Environment.NewLine, applyResult.Errors));

            // Write the patched data and reload it, so the verification covers what ends up on disk.
            byte[] bytes = WriteData(patched);
            UndertaleData reloaded = ReadData(bytes);

            DataFileDiffResult recheck = new DataFileDiffer(reloaded, compareData).Compare();
            Assert.True(recheck.AddedCount == 0 && recheck.RemovedCount == 0 && recheck.ChangedCount == 0,
                DescribeDiff(recheck));
            Assert.Equal(0, recheck.AddedCount);
            Assert.Equal(0, recheck.RemovedCount);
            Assert.Equal(0, recheck.ChangedCount);

            Assert.True(applyResult.VerifiedCount > 0, "at least one entry should have been verified");
            Assert.Equal(0, applyResult.MismatchedCount);
        }
        finally
        {
            File.Delete(patchPath);
        }
    }

    [Fact]
    public void Apply_KeepsRemovedResources_WhenDeletionDisabled()
    {
        (UndertaleData baseData, UndertaleData compareData) = BuildDataPair();
        string patchPath = Path.Combine(Path.GetTempPath(), $"qiuumtdiff-test-{Guid.NewGuid():N}.qiuumtdiff");
        try
        {
            DataFileDiffResult diff = new DataFileDiffer(baseData, compareData).Compare();
            new DataFilePatchBuilder(baseData, compareData).Build(diff, patchPath, "base", "compare");

            UndertaleData patched = Reload(baseData);
            DataFilePatchApplyResult applyResult = new DataFilePatchApplier(patched)
                .Apply(patchPath, new DataFilePatchApplyOptions { DeleteRemoved = false });

            // obj_removed stays, everything else resolves; no errors expected.
            Assert.True(applyResult.Errors.Count == 0, string.Join(Environment.NewLine, applyResult.Errors));
            Assert.NotNull(patched.GameObjects.ByName("obj_removed"));

            DataFileDiffResult recheck = new DataFileDiffer(patched, compareData).Compare();
            Assert.Equal(0, recheck.AddedCount);
            Assert.Equal(2, recheck.RemovedCount); // obj_removed and its code entry are still present
            Assert.True(recheck.ChangedCount == 0, DescribeDiff(recheck));
        }
        finally
        {
            File.Delete(patchPath);
        }
    }

    [Fact]
    public void Apply_DetectsBaseDrift()
    {
        (UndertaleData baseData, UndertaleData compareData) = BuildDataPair();
        string patchPath = Path.Combine(Path.GetTempPath(), $"qiuumtdiff-test-{Guid.NewGuid():N}.qiuumtdiff");
        try
        {
            DataFileDiffResult diff = new DataFileDiffer(baseData, compareData).Compare();
            new DataFilePatchBuilder(baseData, compareData).Build(diff, patchPath, "base", "compare");

            // Drift the base: change the depth of a sprite-backed object before applying.
            UndertaleData drifted = Reload(baseData);
            drifted.GameObjects.ByName("obj_test").Depth = 777;

            DataFilePatchApplyResult applyResult = new DataFilePatchApplier(drifted)
                .Apply(patchPath, new DataFilePatchApplyOptions { DeleteRemoved = true });

            Assert.Contains(applyResult.BaseDriftWarnings, w => w.StartsWith("GameObjects/obj_test:"));
        }
        finally
        {
            File.Delete(patchPath);
        }
    }

    private static (UndertaleData, UndertaleData) BuildDataPair()
    {
        UndertaleData baseData = UndertaleData.CreateNew();
        UndertaleData compareData = UndertaleData.CreateNew();

        // GameMaker 2024.8 data: the builtin sound group resolves to 0 consistently on write
        // and read, the particle system / filter / UI chunks are serializable, and the FUNC
        // chunk no longer contains code locals (avoiding a version-detection heuristic).
        foreach (UndertaleData data in new[] { baseData, compareData })
        {
            data.GeneralInfo.Major = 2024;
            data.GeneralInfo.Minor = 8;
            data.AudioGroups.Add(new UndertaleAudioGroup { Name = data.Strings.MakeString("audio_group0") });
        }

        // Align the bookkeeping fields of the general info, which are random per CreateNew call
        // and would otherwise show up as spurious differences (and cannot be meaningfully patched).
        AlignBookkeeping(baseData, compareData);

        // The remaining categories live in chunks CreateNew does not create.
        foreach (UndertaleData data in new[] { baseData, compareData })
        {
            data.FORM.Chunks["FEDS"] = new UndertaleChunkFEDS();
            data.FORM.Chunks["PSYS"] = new UndertaleChunkPSYS();
            data.FORM.Chunks["PSEM"] = new UndertaleChunkPSEM();
            data.FORM.Chunks["UILR"] = new UndertaleChunkUILR();
            data.FORM.ChunksTypeDict[typeof(UndertaleChunkFEDS)] = data.FORM.Chunks["FEDS"];
            data.FORM.ChunksTypeDict[typeof(UndertaleChunkPSYS)] = data.FORM.Chunks["PSYS"];
            data.FORM.ChunksTypeDict[typeof(UndertaleChunkPSEM)] = data.FORM.Chunks["PSEM"];
            data.FORM.ChunksTypeDict[typeof(UndertaleChunkUILR)] = data.FORM.Chunks["UILR"];

            MoveStringsChunkLast(data);
        }

        // Sprite-backed resources are added after the sprite exists (see below); snapshot
        // categories that do not reference sprites come first.
        AddAudioGroups(baseData, compareData);
        AddTimeline(baseData, "tl_test", moments: 1);
        AddTimeline(compareData, "tl_test", moments: 2);
        AddExtension(baseData, "ext_test", functionId: 7, optionValue: "a");
        AddExtension(compareData, "ext_test", functionId: 8, optionValue: "b");
        AddFilterEffect(baseData, "fe_test", "1");
        AddFilterEffect(compareData, "fe_test", "2");

        // Anonymous sub-code entry whose body changes without touching the parent's segment.
        AddSubCode(baseData, "gml_Object_obj_sub_Create_0", childConstant: 2, extraChild: false);
        AddSubCode(compareData, "gml_Object_obj_sub_Create_0", childConstant: 3, extraChild: true);

        // Sprite with texture + collision mask; the compared copy has different pixels and mask.
        AddSprite(baseData, "spr_test", changed: false);
        AddSprite(compareData, "spr_test", changed: true);

        // Sprite-referencing snapshot categories (emitters, UI sprite instances).
        AddParticleSystem(baseData, "ps_test", emitterSpeed: 1f);
        AddParticleSystem(compareData, "ps_test", emitterSpeed: 2f);
        AddUIRoot(baseData, "ui_root", padding: 1f);
        AddUIRoot(compareData, "ui_root", padding: 2f);

        // Code entry, decompiled and recompiled through the patch; constant differs.
        AddCompiledCode(baseData, "gml_Object_obj_test_Draw_0", "a = 1 + 2;");
        AddCompiledCode(compareData, "gml_Object_obj_test_Draw_0", "a = 2 + 2;");

        // Game object with multi-action event; the compared copy has an extra action.
        AddGameObject(baseData, "obj_test", extraAction: false);
        AddGameObject(compareData, "obj_test", extraAction: true);

        // Embedded sound; the compared copy has different audio bytes.
        AddSound(baseData, "snd_test", changed: false);
        AddSound(compareData, "snd_test", changed: true);

        // Path (fully lossless JSON).
        AddPath(baseData, "path_test", pointCount: 2);
        AddPath(compareData, "path_test", pointCount: 3);

        // Object only in base (removed) and only in compare (added).
        AddGameObject(baseData, "obj_removed", extraAction: false);
        AddGameObject(compareData, "obj_added", extraAction: false);

        // Round-trip both through write+read, exactly like data files loaded from disk; this
        // normalizes derived state (room background color alpha, sound group resolution, ...)
        // so the comparison reflects file content rather than fresh in-memory defaults.
        return (Reload(baseData), Reload(compareData));
    }

    private static void AddSprite(UndertaleData data, string name, bool changed)
    {
        GMImage image = new(2, 1);
        image.GetRawImageData()[0] = changed ? (byte)0x40 : (byte)0x20;
        image = image.ConvertToQoi();

        UndertaleEmbeddedTexture texture = new()
        {
            Name = data.Strings.MakeString($"Texture {data.EmbeddedTextures.Count}"),
            Scaled = 1,
            TextureData = new UndertaleEmbeddedTexture.TexData { Image = image },
        };
        data.EmbeddedTextures.Add(texture);

        UndertaleTexturePageItem pageItem = new()
        {
            Name = data.Strings.MakeString($"PageItem {data.TexturePageItems.Count}"),
            SourceX = 0,
            SourceY = 0,
            SourceWidth = 2,
            SourceHeight = 1,
            TargetX = 0,
            TargetY = 0,
            TargetWidth = 2,
            TargetHeight = 1,
            BoundingWidth = 2,
            BoundingHeight = 1,
            TexturePage = texture,
        };
        data.TexturePageItems.Add(pageItem);

        UndertaleSprite sprite = new()
        {
            Name = data.Strings.MakeString(name),
            Width = 2u,
            Height = 1u,
        };
        sprite.Textures.Add(new UndertaleSprite.TextureEntry() { Texture = pageItem });
        sprite.CollisionMasks.Add(sprite.NewMaskEntry(data));
        sprite.CollisionMasks[^1].Data = changed ? [0b10110000] : [0b11000000];
        data.Sprites.Add(sprite);
    }

    private static void AddCompiledCode(UndertaleData data, string name, string gml)
    {
        UndertaleCode code = new()
        {
            Name = data.Strings.MakeString(name),
        };
        data.Code.Add(code);
        if (data.CodeLocals is not null)
        {
            UndertaleCodeLocals.CreateEmptyEntry(data, code.Name);
        }

        UndertaleModLib.Compiler.CodeImportGroup group = new(data);
        group.QueueReplace(code, gml);
        group.Import(true);
    }

    private static void AddGameObject(UndertaleData data, string name, bool extraAction)
    {
        UndertaleGameObject obj = new()
        {
            Name = data.Strings.MakeString(name),
            Depth = 5,
        };

        UndertaleCode createCode = new()
        {
            Name = data.Strings.MakeString($"gml_Object_{name}_Create_0"),
        };
        data.Code.Add(createCode);
        UndertaleModLib.Compiler.CodeImportGroup group = new(data);
        group.QueueReplace(createCode, "b = 7;");
        group.Import(true);

        UndertaleGameObject.Event ev = new()
        {
            EventSubtype = (uint)EventType.Create,
        };
        ev.Actions.Add(new UndertaleGameObject.EventAction()
        {
            LibID = 1,
            ID = 603,
            Kind = 7,
            UseApplyTo = true,
            ExeType = 2,
            ActionName = data.Strings.MakeString(""),
            CodeId = createCode,
            ArgumentCount = 1,
            Who = -1,
        });
        if (extraAction)
        {
            UndertaleCode destroyCode = new()
            {
                Name = data.Strings.MakeString($"gml_Object_{name}_Destroy_0"),
            };
            data.Code.Add(destroyCode);
            group.QueueReplace(destroyCode, "c = 8;");
            group.Import(true);

            ev.Actions.Add(new UndertaleGameObject.EventAction()
            {
                LibID = 1,
                ID = 603,
                Kind = 7,
                UseApplyTo = true,
                ExeType = 2,
                ActionName = data.Strings.MakeString(""),
                CodeId = destroyCode,
                ArgumentCount = 1,
                Who = -1,
            });
        }
        obj.Events[(int)EventType.Create].Add(ev);
        data.GameObjects.Add(obj);
    }

    private static void AddSound(UndertaleData data, string name, bool changed)
    {
        byte[] audio = changed ? [1, 2, 3, 4, 5, 6, 7, 8] : [8, 7, 6, 5, 4, 3, 2, 1];
        UndertaleEmbeddedAudio embedded = new()
        {
            Name = data.Strings.MakeString($"EmbeddedSound {data.EmbeddedAudio.Count}"),
            Data = audio,
        };
        data.EmbeddedAudio.Add(embedded);

        UndertaleSound sound = new()
        {
            Name = data.Strings.MakeString(name),
            Flags = UndertaleSound.AudioEntryFlags.IsEmbedded | UndertaleSound.AudioEntryFlags.Regular,
            File = data.Strings.MakeString($"{name}.wav"),
            Type = data.Strings.MakeString("wav"),
            Volume = 1f,
            AudioFile = embedded,
            GroupID = data.GetBuiltinSoundGroupID(),
        };
        data.Sounds.Add(sound);
    }

    private static void AddPath(UndertaleData data, string name, int pointCount)
    {
        UndertalePath path = new()
        {
            Name = data.Strings.MakeString(name),
            IsSmooth = false,
            IsClosed = false,
            Precision = 4,
        };
        for (int i = 0; i < pointCount; i++)
        {
            path.Points.Add(new UndertalePath.PathPoint()
            {
                X = i * 10,
                Y = i * 5,
                Speed = 100,
            });
        }
        data.Paths.Add(path);
    }

    [Fact]
    public void Apply_HandlesExternalTextureAndAudioGroupSound()
    {
        // This scenario needs real files on disk: external texture and audio group content is
        // resolved relative to the data file's directory.
        string root = Path.Combine(Path.GetTempPath(), $"qiuumtdiff-ext-{Guid.NewGuid():N}");
        string baseDir = Path.Join(root, "base");
        string compareDir = Path.Join(root, "compare");
        string outDir = Path.Join(root, "out");
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(compareDir);
        Directory.CreateDirectory(outDir);
        string patchPath = Path.Join(root, "ext.qiuumtdiff");

        try
        {
            (UndertaleData a, UndertaleData b) = BuildExternalPair();

            // External content lives next to the data files; auxiliary files are the only ones
            // written here. The data files themselves stay in memory, because synthetic data
            // files do not survive the library's version-detection heuristics.
            Directory.CreateDirectory(Path.Join(baseDir, "tex"));
            Directory.CreateDirectory(Path.Join(compareDir, "tex"));
            WriteExternalTextureFile(a, Path.Join(baseDir, "tex", "texgroup_0.png"), 0x11);
            WriteExternalTextureFile(b, Path.Join(compareDir, "tex", "texgroup_0.png"), 0x22);
            WriteGroupFile(baseDir, audioByte: 0x33);
            WriteGroupFile(compareDir, audioByte: 0x44);
            LinkExternalTextureGroup(a);
            LinkExternalTextureGroup(b);

            // The data files themselves are only written so the recorded MD5s can be verified;
            // they are never read back here (synthetic data file version detection is out of scope).
            WriteDataFile(a, Path.Join(baseDir, "main.win"));
            WriteDataFile(b, Path.Join(compareDir, "main.win"));

            DataFileDiffResult diff = new DataFileDiffer(a, b).Compare();
            Assert.Contains(diff.Categories.Single(c => c.Id == "Sprites").Entries,
                e => e.Change == AssetChangeKind.Changed);
            Assert.Contains(diff.Categories.Single(c => c.Id == "Sounds").Entries,
                e => e.Name == "snd_group" && e.Change == AssetChangeKind.Changed);

            new DataFilePatchBuilder(a, b, Path.Join(compareDir, "main.win"), Path.Join(baseDir, "main.win"))
                .Build(diff, patchPath, "base", "compare");

            // The manifest identifies the format and records both file MD5s for compatibility checks.
            using (System.IO.Compression.ZipArchive archive = System.IO.Compression.ZipFile.OpenRead(patchPath))
            using (Stream manifestStream = archive.GetEntry("patch.json")!.Open())
            {
                DataFilePatchManifest manifest =
                    System.Text.Json.JsonSerializer.Deserialize<DataFilePatchManifest>(manifestStream)!;
                Assert.Equal(DataFilePatch.FormatName, manifest.FormatName);
                Assert.Equal(DataFilePatch.FormatVersion, manifest.FormatVersion);
                Assert.Equal(FileMd5(Path.Join(baseDir, "main.win")), manifest.BaseFileMd5);
                Assert.Equal(FileMd5(Path.Join(compareDir, "main.win")), manifest.ResultFileMd5);
            }

            UndertaleData patched = a;
            DataFilePatchApplyResult applyResult = new DataFilePatchApplier(patched).Apply(patchPath,
                new DataFilePatchApplyOptions
                {
                    DeleteRemoved = true,
                    BaseDataDirectory = baseDir,
                    BaseFilePath = Path.Join(baseDir, "main.win"),
                    OutputDirectory = outDir,
                });
            Assert.True(applyResult.Errors.Count == 0, string.Join(Environment.NewLine, applyResult.Errors));
            Assert.Equal(0, applyResult.MismatchedCount);
            Assert.Empty(applyResult.BaseDriftWarnings);
            Assert.Equal(FileMd5(Path.Join(compareDir, "main.win")), applyResult.ExpectedResultMd5);
            Assert.Contains(applyResult.ExtraOutputFiles, f => f.EndsWith("texgroup_0.png", StringComparison.Ordinal));
            Assert.Contains(applyResult.ExtraOutputFiles, f => f.EndsWith("audiogroup1.dat", StringComparison.Ordinal));

            // The reconstructed auxiliary files must carry the compared content.
            Assert.Equal(0x22, ReadExternalTextureByte(Path.Join(outDir, "tex", "texgroup_0.png")));
            Assert.Contains((byte)0x44, File.ReadAllBytes(Path.Join(outDir, "audiogroup1.dat")));

            DataFileDiffResult recheck = new DataFileDiffer(patched, b).Compare();
            Assert.True(recheck.AddedCount == 0 && recheck.RemovedCount == 0 && recheck.ChangedCount == 0,
                DescribeDiff(recheck));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private static (UndertaleData, UndertaleData) BuildExternalPair()
    {
        UndertaleData baseData = UndertaleData.CreateNew();
        UndertaleData compareData = UndertaleData.CreateNew();
        foreach (UndertaleData data in new[] { baseData, compareData })
        {
            data.GeneralInfo.Major = 2024;
            data.GeneralInfo.Minor = 8;
            // An empty ROOM chunk keeps version-detection heuristics from misreading synthetic rooms.
            data.Rooms.Clear();
            data.AudioGroups.Add(new UndertaleAudioGroup { Name = data.Strings.MakeString("audio_group0") });
            data.AudioGroups.Add(new UndertaleAudioGroup { Name = data.Strings.MakeString("audio_group1") });
        }
        AlignBookkeeping(baseData, compareData);

        foreach (UndertaleData data in new[] { baseData, compareData })
        {
            UndertaleEmbeddedTexture texture = new()
            {
                Name = data.Strings.MakeString("Texture 0"),
                TextureExternal = true,
                TextureWidth = data == compareData ? 4 : 2,
                TextureHeight = 1,
                IndexInGroup = 0,
                TextureData = null,
            };
            data.EmbeddedTextures.Add(texture);

            UndertaleTexturePageItem pageItem = new()
            {
                Name = data.Strings.MakeString("PageItem 0"),
                SourceWidth = 2,
                SourceHeight = 1,
                TargetWidth = 2,
                TargetHeight = 1,
                BoundingWidth = 2,
                BoundingHeight = 1,
                TexturePage = texture,
            };
            data.TexturePageItems.Add(pageItem);

            UndertaleSprite sprite = new()
            {
                Name = data.Strings.MakeString("spr_ext"),
                Width = data == compareData ? 3u : 2u,
                Height = 1,
            };
            sprite.Textures.Add(new UndertaleSprite.TextureEntry { Texture = pageItem });
            sprite.CollisionMasks.Add(sprite.NewMaskEntry(data));
            data.Sprites.Add(sprite);
        }

        // Group sound: audio lives in audiogroup0.dat. The compared copy also changes the volume,
        // because external audio bytes are outside the data file and not part of the comparison.
        foreach (UndertaleData data in new[] { baseData, compareData })
        {
            data.Sounds.Add(new UndertaleSound
            {
                Name = data.Strings.MakeString("snd_group"),
                Flags = UndertaleSound.AudioEntryFlags.IsEmbedded | UndertaleSound.AudioEntryFlags.Regular,
                File = data.Strings.MakeString("snd_group.wav"),
                Type = data.Strings.MakeString("wav"),
                Volume = data == compareData ? 0.5f : 1f,
                GroupID = 1,
                AudioID = 0,
                Preload = true,
            });
            data.Sounds[^1].AudioGroup = data.AudioGroups[1];
        }

        return (baseData, compareData);
    }

    private static void LinkExternalTextureGroup(UndertaleData data)
    {
        UndertaleEmbeddedTexture texture = data.EmbeddedTextures[0];
        UndertaleTextureGroupInfo group = new()
        {
            Name = data.Strings.MakeString("texgroup"),
            Directory = data.Strings.MakeString("tex"),
            Extension = data.Strings.MakeString(".png"),
        };
        group.TexturePages.Add(new UndertaleResourceById<UndertaleEmbeddedTexture, UndertaleChunkTXTR>
        {
            Resource = texture,
        });
        texture.TextureInfo = group;
    }

    private static void WriteExternalTextureFile(UndertaleData data, string path, byte marker)
    {
        UndertaleEmbeddedTexture texture = data.EmbeddedTextures[0];
        GMImage image = new(2, 1);
        image.GetRawImageData()[0] = marker;
        image = image.ConvertToQoi();
        using FileStream fs = new(path, FileMode.Create, FileAccess.Write);
        using FileBinaryWriter writer = new(fs);
        new UndertaleEmbeddedTexture.TexData { Image = image }.Serialize(writer, true);
    }

    private static byte ReadExternalTextureByte(string path)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read);
        using FileBinaryReader reader = new(fs);
        UndertaleEmbeddedTexture.TexData texData = new();
        texData.Unserialize(reader, fs.Length, true);
        return texData.Image!.ConvertToRawBgra().GetRawImageData()[0];
    }

    private static void WriteGroupFile(string directory, byte audioByte)
    {
        UndertaleData group = UndertaleData.CreateNew();
        group.GeneralInfo.Major = 2024;
        group.GeneralInfo.Minor = 8;
        group.EmbeddedAudio.Add(new UndertaleEmbeddedAudio
        {
            Name = group.Strings.MakeString("EmbeddedSound 0"),
            Data = [audioByte, audioByte, audioByte, audioByte],
        });
        WriteDataFile(group, Path.Join(directory, "audiogroup1.dat"));
    }

    private static string FileMd5(string path)
    {
        return Convert.ToHexString(System.Security.Cryptography.MD5.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }

    private static void WriteDataFile(UndertaleData data, string path)
    {
        using FileStream fs = new(path, FileMode.Create, FileAccess.Write);
        UndertaleIO.Write(fs, data);
    }

    private static UndertaleData ReadDataFile(string path, List<string>? warnings = null)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read);
        return UndertaleIO.Read(fs, (warning, _) =>
        {
            lock (warnings ?? new List<string>())
            {
                warnings?.Add(warning);
            }
        }, _ => { });
    }

    /// <summary>
    /// The strings chunk must be written after every string-referencing chunk; string references
    /// are only patched up when the strings themselves are written. The dictionary is rebuilt
    /// because Dictionary reuses freed slots, so remove+add would keep the original order.
    /// </summary>
    [Fact]
    public void Apply_RestoresGeneralInfoFields()
    {
        UndertaleData baseData = UndertaleData.CreateNew();
        UndertaleData compareData = UndertaleData.CreateNew();
        foreach (UndertaleData data in new[] { baseData, compareData })
        {
            data.GeneralInfo.Major = 2024;
            data.GeneralInfo.Minor = 8;
        }
        AlignBookkeeping(baseData, compareData);

        // Change fingerprinted general info and options fields in the compared file.
        compareData.GeneralInfo.DisplayName = compareData.Strings.MakeString("Patched Game");
        compareData.GeneralInfo.SteamAppID = 480;
        compareData.GeneralInfo.GMS2FPS = 60f;
        compareData.GeneralInfo.Info |= UndertaleGeneralInfo.InfoFlags.ShowCursor;
        compareData.Options.Constants.Add(new UndertaleOptions.Constant
        {
            Name = compareData.Strings.MakeString("@@NewConstant"),
            Value = compareData.Strings.MakeString("42"),
        });

        string patchPath = Path.Combine(Path.GetTempPath(), $"qiuumtdiff-test-{Guid.NewGuid():N}.qiuumtdiff");
        try
        {
            baseData = Reload(baseData);
            compareData = Reload(compareData);

            DataFileDiffResult diff = new DataFileDiffer(baseData, compareData).Compare();
            AssetCategoryDiff generalInfo = diff.Categories.Single(c => c.Id == "GeneralInfo");
            Assert.Contains(generalInfo.Entries, e => e.Change == AssetChangeKind.Changed);

            new DataFilePatchBuilder(baseData, compareData).Build(diff, patchPath, "base", "compare");

            UndertaleData patched = Reload(baseData);
            DataFilePatchApplyResult applyResult = new DataFilePatchApplier(patched)
                .Apply(patchPath, new DataFilePatchApplyOptions { DeleteRemoved = true });
            Assert.True(applyResult.Errors.Count == 0, string.Join(Environment.NewLine, applyResult.Errors));
            Assert.Equal(0, applyResult.MismatchedCount);
            Assert.True(applyResult.VerifiedCount > 0);

            Assert.Equal("Patched Game", patched.GeneralInfo.DisplayName?.Content);
            Assert.Equal(480, patched.GeneralInfo.SteamAppID);
            Assert.Equal(60f, patched.GeneralInfo.GMS2FPS);
            Assert.Contains(patched.Options.Constants, c => c.Name?.Content == "@@NewConstant" && c.Value?.Content == "42");

            DataFileDiffResult recheck = new DataFileDiffer(patched, compareData).Compare();
            Assert.True(recheck.AddedCount == 0 && recheck.RemovedCount == 0 && recheck.ChangedCount == 0,
                DescribeDiff(recheck));
        }
        finally
        {
            File.Delete(patchPath);
        }
    }

    [Fact]
    public void Apply_RestoresSpineSpriteData()
    {
        UndertaleData baseData = UndertaleData.CreateNew();
        UndertaleData compareData = UndertaleData.CreateNew();
        foreach (UndertaleData data in new[] { baseData, compareData })
        {
            data.GeneralInfo.Major = 2024;
            data.GeneralInfo.Minor = 8;
            data.Rooms.Clear();
        }
        AlignBookkeeping(baseData, compareData);

        AddSpineSprite(baseData, spineJson: "{\"bones\":[]}");
        AddSpineSprite(compareData, spineJson: "{\"bones\":[{\"name\":\"root\"}]}");

        string patchPath = Path.Combine(Path.GetTempPath(), $"qiuumtdiff-test-{Guid.NewGuid():N}.qiuumtdiff");
        try
        {
            // Special sprites are exercised in memory: the write path for synthetic Spine data
            // is outside the scope of the patch engine.
            Assert.Equal(UndertaleSprite.SpriteType.Spine, baseData.Sprites.ByName("spr_spine").SSpriteType);

            DataFileDiffResult diff = new DataFileDiffer(baseData, compareData).Compare();
            Assert.Contains(diff.Categories.Single(c => c.Id == "Sprites").Entries,
                e => e.Name == "spr_spine" && e.Change == AssetChangeKind.Changed);

            new DataFilePatchBuilder(baseData, compareData).Build(diff, patchPath, "base", "compare");

            UndertaleData patched = baseData;
            DataFilePatchApplyResult applyResult = new DataFilePatchApplier(patched)
                .Apply(patchPath, new DataFilePatchApplyOptions { DeleteRemoved = true });
            Assert.True(applyResult.Errors.Count == 0, string.Join(Environment.NewLine, applyResult.Errors));
            Assert.Equal(0, applyResult.MismatchedCount);
            Assert.Equal("{\"bones\":[{\"name\":\"root\"}]}", patched.Sprites.ByName("spr_spine").SpineJSON);

            DataFileDiffResult recheck = new DataFileDiffer(patched, compareData).Compare();
            Assert.True(recheck.AddedCount == 0 && recheck.RemovedCount == 0 && recheck.ChangedCount == 0,
                DescribeDiff(recheck));
        }
        finally
        {
            File.Delete(patchPath);
        }
    }

    private static void AddSpineSprite(UndertaleData data, string spineJson)
    {
        GMImage image = new(2, 1);
        image = image.ConvertToQoi();
        UndertaleEmbeddedTexture texture = new()
        {
            Name = data.Strings.MakeString("Texture 0"),
            TextureData = new UndertaleEmbeddedTexture.TexData { Image = image },
        };
        data.EmbeddedTextures.Add(texture);
        UndertaleTexturePageItem pageItem = new()
        {
            Name = data.Strings.MakeString("PageItem 0"),
            SourceWidth = 2,
            SourceHeight = 1,
            TargetWidth = 2,
            TargetHeight = 1,
            BoundingWidth = 2,
            BoundingHeight = 1,
            TexturePage = texture,
        };
        data.TexturePageItems.Add(pageItem);

        UndertaleSprite sprite = new()
        {
            Name = data.Strings.MakeString("spr_spine"),
            Width = 2,
            Height = 1,
            IsSpecialType = true,
            SSpriteType = UndertaleSprite.SpriteType.Spine,
            SpineVersion = 4,
            SpineJSON = spineJson,
            SpineAtlas = "atlas",
            SpineTextures = [],
        };
        sprite.SpineTextures.Add(new UndertaleSpineTextureEntry
        {
            PageWidth = 2,
            PageHeight = 1,
            TextureEntryLength = 1,
        });
        sprite.Textures.Add(new UndertaleSprite.TextureEntry { Texture = pageItem });
        data.Sprites.Add(sprite);
    }

    [Fact]
    public void SortByChain_ChainsByMd5_AndKeepsAlphabeticalForUnmatched()
    {
        List<PatchChainEntry> entries =
        [
            MakeChainEntry("zzz-unrelated.qiuumtdiff", baseMd5: "1111", resultMd5: "2222"),
            MakeChainEntry("second.qiuumtdiff", baseMd5: "BBBB", resultMd5: "CCCC"),
            MakeChainEntry("first.qiuumtdiff", baseMd5: "AAAA", resultMd5: "BBBB"),
            MakeChainEntry("aaa-no-md5.qiuumtdiff", baseMd5: null, resultMd5: null),
        ];

        List<string> ordered = [.. DataFilePatchChain.SortByChain(entries).Select(e => e.FileName)];
        Assert.Equal(
            ["first.qiuumtdiff", "second.qiuumtdiff", "aaa-no-md5.qiuumtdiff", "zzz-unrelated.qiuumtdiff"],
            ordered);

        // Cycles and ambiguous links fall back to alphabetical order.
        List<PatchChainEntry> cyclic =
        [
            MakeChainEntry("b.qiuumtdiff", baseMd5: "BBBB", resultMd5: "AAAA"),
            MakeChainEntry("a.qiuumtdiff", baseMd5: "AAAA", resultMd5: "BBBB"),
        ];
        Assert.Equal(["a.qiuumtdiff", "b.qiuumtdiff"],
            DataFilePatchChain.SortByChain(cyclic).Select(e => e.FileName).ToArray());

        List<PatchChainEntry> ambiguous =
        [
            MakeChainEntry("c.qiuumtdiff", baseMd5: "BBBB", resultMd5: "CCCC"),
            MakeChainEntry("b.qiuumtdiff", baseMd5: "BBBB", resultMd5: "DDDD"),
            MakeChainEntry("a.qiuumtdiff", baseMd5: "AAAA", resultMd5: "BBBB"),
        ];
        List<string> ambiguousOrder = [.. DataFilePatchChain.SortByChain(ambiguous).Select(e => e.FileName)];
        Assert.Equal("a.qiuumtdiff", ambiguousOrder[0]);
        Assert.Equal(["b.qiuumtdiff", "c.qiuumtdiff"], ambiguousOrder.Skip(1).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void ApplyMany_AppliesChainedPatchesInOrder()
    {
        (UndertaleData a, UndertaleData b, UndertaleData c) = BuildTriple();
        string root = Path.Combine(Path.GetTempPath(), $"qiuumtdiff-chain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string aPath = Path.Join(root, "a.win");
        string bPath = Path.Join(root, "b.win");
        string cPath = Path.Join(root, "c.win");
        string p1 = Path.Join(root, "1-a-to-b.qiuumtdiff");
        string p2 = Path.Join(root, "2-b-to-c.qiuumtdiff");
        try
        {
            WriteDataFile(a, aPath);
            WriteDataFile(b, bPath);
            WriteDataFile(c, cPath);
            new DataFilePatchBuilder(a, b, bPath, aPath)
                .Build(new DataFileDiffer(a, b).Compare(), p1, aPath, bPath);
            new DataFilePatchBuilder(b, c, cPath, bPath)
                .Build(new DataFileDiffer(b, c).Compare(), p2, bPath, cPath);

            List<PatchChainEntry> ordered = DataFilePatchChain.SortByChain(
                DataFilePatchChain.ReadManifests([p2, p1]));
            Assert.Equal([p1, p2], ordered.Select(e => e.Path));

            DataFilePatchApplyResult result = DataFilePatchChain.ApplyMany(a, [p1, p2],
                new DataFilePatchApplyOptions { DeleteRemoved = true, BaseFilePath = aPath });
            Assert.Empty(result.Errors);
            Assert.Empty(result.Warnings);
            Assert.Equal(0, result.MismatchedCount);
            Assert.True(result.VerifiedCount > 0);

            DataFileDiffResult recheck = new DataFileDiffer(a, c).Compare();
            Assert.True(recheck.AddedCount == 0 && recheck.RemovedCount == 0 && recheck.ChangedCount == 0,
                DescribeDiff(recheck));

            // Swapped order: the chain link check reports the mismatch instead of applying silently.
            (UndertaleData a2, _, _) = BuildTriple();
            DataFilePatchApplyResult swapped = DataFilePatchChain.ApplyMany(a2, [p2, p1],
                new DataFilePatchApplyOptions { DeleteRemoved = true });
            Assert.Contains(swapped.Warnings, w => w.Contains("expects a base file with MD5", StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private static PatchChainEntry MakeChainEntry(string fileName, string? baseMd5, string? resultMd5)
    {
        return new PatchChainEntry(fileName, new DataFilePatchManifest
        {
            BaseFileMd5 = baseMd5,
            ResultFileMd5 = resultMd5,
        });
    }

    private static (UndertaleData A, UndertaleData B, UndertaleData C) BuildTriple()
    {
        UndertaleData a = BuildCodedData(1);
        UndertaleData b = BuildCodedData(2);
        UndertaleData c = BuildCodedData(3);
        return (a, b, c);
    }

    private static UndertaleData BuildCodedData(int constant)
    {
        UndertaleData data = UndertaleData.CreateNew();
        data.GeneralInfo.Major = 2024;
        data.GeneralInfo.Minor = 8;
        data.Rooms.Clear();
        AddCompiledCode(data, "gml_Object_obj_chain_Draw_0", $"v = {constant};");
        return data;
    }

    private static void MoveStringsChunkLast(UndertaleData data)
    {
        if (!data.FORM.Chunks.ContainsKey("STRG"))
        {
            return;
        }
        List<KeyValuePair<string, UndertaleChunk>> chunks = [.. data.FORM.Chunks.Where(c => c.Key != "STRG")];
        chunks.Add(new("STRG", data.FORM.Chunks["STRG"]));
        data.FORM.Chunks.Clear();
        foreach (KeyValuePair<string, UndertaleChunk> chunk in chunks)
        {
            data.FORM.Chunks.Add(chunk.Key, chunk.Value);
        }
    }

    private static void AlignBookkeeping(UndertaleData baseData, UndertaleData compareData)
    {
        compareData.GeneralInfo.GameID = baseData.GeneralInfo.GameID;
        compareData.GeneralInfo.Timestamp = baseData.GeneralInfo.Timestamp;
        compareData.GeneralInfo.LicenseCRC32 = baseData.GeneralInfo.LicenseCRC32;
        compareData.GeneralInfo.LicenseMD5 = [.. baseData.GeneralInfo.LicenseMD5];
        compareData.GeneralInfo.DirectPlayGuid = baseData.GeneralInfo.DirectPlayGuid;
        compareData.GeneralInfo.GMS2GameGUID = [.. baseData.GeneralInfo.GMS2GameGUID];
        compareData.GeneralInfo.GMS2RandomUID.Clear();
        compareData.GeneralInfo.GMS2RandomUID.AddRange(baseData.GeneralInfo.GMS2RandomUID);
        compareData.Options.Constants.Clear();
        foreach (UndertaleOptions.Constant constant in baseData.Options.Constants)
        {
            compareData.Options.Constants.Add(new UndertaleOptions.Constant
            {
                Name = compareData.Strings.MakeString(constant.Name?.Content ?? ""),
                Value = compareData.Strings.MakeString(constant.Value?.Content ?? ""),
            });
        }
    }

    [Fact]
    public void Apply_RemovesSubCodeEntry_WhenDeletionEnabled()
    {
        UndertaleData baseData = UndertaleData.CreateNew();
        UndertaleData compareData = UndertaleData.CreateNew();
        foreach (UndertaleData data in new[] { baseData, compareData })
        {
            data.GeneralInfo.Major = 2024;
            data.GeneralInfo.Minor = 8;
        }
        AlignBookkeeping(baseData, compareData);

        AddSubCode(baseData, "gml_Object_obj_sub_Create_0", childConstant: 2, extraChild: true);
        AddSubCode(compareData, "gml_Object_obj_sub_Create_0", childConstant: 2, extraChild: false);

        string patchPath = Path.Combine(Path.GetTempPath(), $"qiuumtdiff-test-{Guid.NewGuid():N}.qiuumtdiff");
        try
        {
            baseData = Reload(baseData);
            compareData = Reload(compareData);

            DataFileDiffResult diff = new DataFileDiffer(baseData, compareData).Compare();
            AssetCategoryDiff code = diff.Categories.Single(c => c.Id == "Code");
            Assert.Contains(code.Entries, e => e.Name == "gml_Object_obj_sub_Create_0_child2" && e.Change == AssetChangeKind.Removed);

            new DataFilePatchBuilder(baseData, compareData).Build(diff, patchPath, "base", "compare");

            UndertaleData patched = Reload(baseData);
            DataFilePatchApplyResult applyResult = new DataFilePatchApplier(patched)
                .Apply(patchPath, new DataFilePatchApplyOptions { DeleteRemoved = true });
            Assert.True(applyResult.Errors.Count == 0, string.Join(Environment.NewLine, applyResult.Errors));
            Assert.Null(patched.Code.ByName("gml_Object_obj_sub_Create_0_child2"));

            byte[] bytes = WriteData(patched);
            UndertaleData reloaded = ReadData(bytes);
            DataFileDiffResult recheck = new DataFileDiffer(reloaded, compareData).Compare();
            Assert.True(recheck.AddedCount == 0 && recheck.RemovedCount == 0 && recheck.ChangedCount == 0,
                DescribeDiff(recheck));
        }
        finally
        {
            File.Delete(patchPath);
        }
    }

    private static void AddSubCode(UndertaleData data, string parentName, int childConstant, bool extraChild)
    {
        UndertaleCode parent = new() { Name = data.Strings.MakeString(parentName) };
        UndertaleInstruction MakeConstant(int value) => new()
        {
            Kind = UndertaleInstruction.Opcode.Push,
            Type1 = UndertaleInstruction.DataType.Int32,
            IntArgument = value,
        };
        UndertaleInstruction MakePopz() => new() { Kind = UndertaleInstruction.Opcode.Popz };

        // Root segment: push 1; popz
        parent.Instructions.Add(MakeConstant(1));
        parent.Instructions.Add(MakePopz());

        UndertaleCode child = new() { Name = data.Strings.MakeString($"{parentName}_child") };
        child.ParentEntry = parent;
        child.Offset = 0;
        foreach (UndertaleInstruction instruction in parent.Instructions)
        {
            child.Offset += CodeFingerprint.GetInstructionSize(instruction) * 4;
        }
        parent.ChildEntries.Add(child);

        // Child segment: push <const>; popz
        parent.Instructions.Add(MakeConstant(childConstant));
        parent.Instructions.Add(MakePopz());

        data.Code.Add(parent);
        data.Code.Add(child);

        if (extraChild)
        {
            UndertaleCode extra = new() { Name = data.Strings.MakeString($"{parentName}_child2") };
            extra.ParentEntry = parent;
            extra.Offset = child.Offset + (CodeFingerprint.GetInstructionSize(MakeConstant(childConstant))
                                          + CodeFingerprint.GetInstructionSize(MakePopz())) * 4;
            parent.ChildEntries.Add(extra);
            parent.Instructions.Add(MakeConstant(7));
            parent.Instructions.Add(MakePopz());
            data.Code.Add(extra);
        }
    }

    private static void AddAudioGroups(UndertaleData baseData, UndertaleData compareData)
    {
        // Both already get "audio_group0" (needed for embedded sound round-tripping); the
        // compared file gains a second group.
        compareData.AudioGroups.Add(new UndertaleAudioGroup
        {
            Name = compareData.Strings.MakeString("audio_group1"),
        });
    }

    private static void AddTimeline(UndertaleData data, string name, int moments)
    {
        UndertaleCode code = new()
        {
            Name = data.Strings.MakeString($"gml_Timeline_{name}"),
        };
        data.Code.Add(code);
        UndertaleModLib.Compiler.CodeImportGroup group = new(data);
        group.QueueReplace(code, "t = 1;");
        group.Import(true);

        UndertaleTimeline timeline = new()
        {
            Name = data.Strings.MakeString(name),
        };
        for (int i = 0; i < moments; i++)
        {
            UndertaleTimeline.UndertaleTimelineMoment moment = new()
            {
                Step = (uint)(10 * (i + 1)),
                Event = new(),
            };
            moment.Event.Add(new UndertaleGameObject.EventAction
            {
                LibID = 1,
                ID = 603,
                Kind = 7,
                UseApplyTo = true,
                ExeType = 2,
                ActionName = data.Strings.MakeString(""),
                CodeId = code,
                ArgumentCount = 1,
                Who = -1,
            });
            timeline.Moments.Add(moment);
        }
        data.Timelines.Add(timeline);
    }

    private static void AddExtension(UndertaleData data, string name, uint functionId, string optionValue)
    {
        UndertaleExtension extension = new()
        {
            Name = data.Strings.MakeString(name),
            FolderName = data.Strings.MakeString(name),
            ClassName = data.Strings.MakeString(""),
            Version = data.Strings.MakeString("1.0"),
        };

        UndertaleExtensionFile file = new()
        {
            Filename = data.Strings.MakeString("ext.dll"),
            CleanupScript = data.Strings.MakeString(""),
            InitScript = data.Strings.MakeString(""),
            Kind = UndertaleExtensionKind.Dll,
        };
        UndertaleExtensionFunction function = new()
        {
            Name = data.Strings.MakeString("ext_function"),
            ID = functionId,
            Kind = 11,
            RetType = UndertaleExtensionVarType.Double,
            ExtName = data.Strings.MakeString("ext_function"),
        };
        function.Arguments.Add(new UndertaleExtensionFunctionArg(UndertaleExtensionVarType.Double));
        function.Arguments.Add(new UndertaleExtensionFunctionArg(UndertaleExtensionVarType.String));
        file.Functions.Add(function);
        extension.Files.Add(file);

        extension.Options.Add(new UndertaleExtensionOption
        {
            Name = data.Strings.MakeString("ext_option"),
            Value = data.Strings.MakeString(optionValue),
            Kind = UndertaleExtensionOption.OptionKind.String,
        });
        data.Extensions.Add(extension);
    }

    private static void AddParticleSystem(UndertaleData data, string name, float emitterSpeed)
    {
        UndertaleSprite sprite = data.Sprites.ByName("spr_test") ?? throw new InvalidOperationException("sprite required");
        UndertaleParticleSystemEmitter emitter = new()
        {
            Name = data.Strings.MakeString("pse_test"),
            Enabled = true,
            Mode = UndertaleParticleSystemEmitter.EmitMode.Stream,
            SpeedMin = emitterSpeed,
            SpeedMax = emitterSpeed + 1,
            Sprite = sprite,
        };
        emitter.SpawnOnUpdate = emitter;
        data.ParticleSystemEmitters.Add(emitter);

        UndertaleParticleSystem system = new()
        {
            Name = data.Strings.MakeString(name),
            OriginX = 5,
            OriginY = 6,
            DrawOrder = UndertaleParticleSystem.DrawOrderEnum.OldOnTop,
        };
        system.Emitters.Add(new UndertaleResourceById<UndertaleParticleSystemEmitter, UndertaleChunkPSEM>
        {
            Resource = emitter,
        });
        data.ParticleSystems.Add(system);
    }

    private static void AddFilterEffect(UndertaleData data, string name, string value)
    {
        data.FilterEffects.Add(new UndertaleFilterEffect
        {
            Name = data.Strings.MakeString(name),
            Value = data.Strings.MakeString(value),
        });
    }

    private static void AddUIRoot(UndertaleData data, string name, float padding)
    {
        UndertaleSprite sprite = data.Sprites.ByName("spr_test") ?? throw new InvalidOperationException("sprite required");
        UndertaleUISpriteInstance spriteInstance = new()
        {
            Name = data.Strings.MakeString("ui_spr"),
            Sprite = sprite,
            X = 3,
            Y = 4,
        };
        UndertaleUIFlexPanel panel = new()
        {
            Name = data.Strings.MakeString("ui_panel"),
            PaddingLeft = new UndertaleFlexValue(padding, UndertaleFlexValue.UnitKind.Point),
            Width = new UndertaleFlexValue(100, UndertaleFlexValue.UnitKind.Point),
        };
        panel.Children.Add(new UndertaleUINode { Node = spriteInstance });

        UndertaleUILayer layer = new()
        {
            Name = data.Strings.MakeString(name),
            Visible = true,
        };
        layer.Children.Add(new UndertaleUINode { Node = panel });
        data.UINodes.Add(new UndertaleUIRootNode { Node = layer });
    }

    private static string DescribeDiff(DataFileDiffResult diff)
    {
        System.Text.StringBuilder builder = new();
        foreach (AssetCategoryDiff category in diff.Categories)
        {
            foreach (AssetDiffEntry entry in category.Entries)
            {
                if (entry.Change != AssetChangeKind.Unchanged)
                {
                    builder.AppendLine($"{category.Id}/{entry.Name}: {entry.Change}");
                }
            }
        }
        return builder.ToString();
    }

    private static UndertaleData Reload(UndertaleData data)
    {
        return ReadData(WriteData(data));
    }

    private static byte[] WriteData(UndertaleData data)
    {
        using MemoryStream ms = new();
        UndertaleIO.Write(ms, data);
        return ms.ToArray();
    }

    private static UndertaleData ReadData(byte[] bytes)
    {
        using MemoryStream ms = new(bytes);
        // Warnings are expected for minimal synthetic data (for example the room chunk's
        // version-dependent alignment); they do not affect the patched content.
        return UndertaleIO.Read(ms, (_, _) => { }, _ => { });
    }
}
