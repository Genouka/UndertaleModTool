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

        string patchPath = Path.Combine(Path.GetTempPath(), $"utmt-patch-test-{Guid.NewGuid():N}.utmt-patch");
        try
        {
            DataFileDiffResult diff = new DataFileDiffer(baseData, compareData).Compare();
            // Added: obj_added plus its Create code and the extra Destroy code of obj_test.
            // Removed: obj_removed plus its Create code. Changed: sprite, sound, path, object,
            // and the changed GML code entry.
            Assert.Equal(3, diff.AddedCount);
            Assert.Equal(2, diff.RemovedCount);
            Assert.Equal(5, diff.ChangedCount);

            new DataFilePatchBuilder(baseData, compareData).Build(diff, patchPath, "base", "compare");
            Assert.True(File.Exists(patchPath));

            // Apply to a fresh copy of the base data.
            UndertaleData patched = Reload(baseData);
            DataFilePatchApplyResult applyResult = new DataFilePatchApplier(patched)
                .Apply(patchPath, new DataFilePatchApplyOptions { DeleteRemoved = true });

            Assert.Empty(applyResult.Errors);

            // Write the patched data and reload it, so the verification covers what ends up on disk.
            byte[] bytes = WriteData(patched);
            UndertaleData reloaded = ReadData(bytes);

            DataFileDiffResult recheck = new DataFileDiffer(reloaded, compareData).Compare();
            Assert.True(recheck.AddedCount == 0, DescribeDiff(recheck));
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
        string patchPath = Path.Combine(Path.GetTempPath(), $"utmt-patch-test-{Guid.NewGuid():N}.utmt-patch");
        try
        {
            DataFileDiffResult diff = new DataFileDiffer(baseData, compareData).Compare();
            new DataFilePatchBuilder(baseData, compareData).Build(diff, patchPath, "base", "compare");

            UndertaleData patched = Reload(baseData);
            DataFilePatchApplyResult applyResult = new DataFilePatchApplier(patched)
                .Apply(patchPath, new DataFilePatchApplyOptions { DeleteRemoved = false });

            // obj_removed stays, everything else resolves; no errors expected.
            Assert.Empty(applyResult.Errors);
            Assert.NotNull(patched.GameObjects.ByName("obj_removed"));

            DataFileDiffResult recheck = new DataFileDiffer(patched, compareData).Compare();
            Assert.Equal(0, recheck.AddedCount);
            Assert.Equal(2, recheck.RemovedCount); // obj_removed and its code entry are still present
            Assert.Equal(0, recheck.ChangedCount);
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
        string patchPath = Path.Combine(Path.GetTempPath(), $"utmt-patch-test-{Guid.NewGuid():N}.utmt-patch");
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

        // GMS2 data (the builtin sound group resolves to 0 consistently on write and read) with
        // one audio group, so embedded sound references round-trip through the data file.
        foreach (UndertaleData data in new[] { baseData, compareData })
        {
            data.GeneralInfo.Major = 2;
            data.AudioGroups.Add(new UndertaleAudioGroup { Name = data.Strings.MakeString("audio_group0") });
        }

        // Align the bookkeeping fields of the general info, which are random per CreateNew call
        // and would otherwise show up as spurious differences (and cannot be meaningfully patched).
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

        // Sprite with texture + collision mask; the compared copy has different pixels and mask.
        AddSprite(baseData, "spr_test", changed: false);
        AddSprite(compareData, "spr_test", changed: true);

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
        return UndertaleIO.Read(ms, null, _ => { });
    }
}
