using System.Linq;
using UndertaleModLib;
using UndertaleModLib.Diff;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
using Xunit;

namespace UndertaleModLibTests.Diff;

public class DataFileDifferTests
{
    [Fact]
    public void Compare_ReportsAddedRemovedAndChangedResources()
    {
        UndertaleData baseData = UndertaleData.CreateNew();
        UndertaleData compareData = UndertaleData.CreateNew();

        AddObject(baseData, "obj_changed", depth: 1);
        AddObject(compareData, "obj_changed", depth: 2);
        AddObject(baseData, "obj_removed", depth: 0);
        AddObject(compareData, "obj_added", depth: 0);

        DataFileDiffResult result = new DataFileDiffer(baseData, compareData).Compare();
        AssetCategoryDiff objects = result.Categories.Single(c => c.Id == "GameObjects");

        Assert.Equal(AssetChangeKind.Changed, Find(objects, "obj_changed").Change);
        Assert.Equal(AssetChangeKind.Removed, Find(objects, "obj_removed").Change);
        Assert.Equal(AssetChangeKind.Added, Find(objects, "obj_added").Change);
        Assert.Equal(1, objects.AddedCount);
        Assert.Equal(1, objects.RemovedCount);
        Assert.Equal(1, objects.ChangedCount);
    }

    [Fact]
    public void Compare_IgnoresResourceListOrderAndIds()
    {
        UndertaleData baseData = UndertaleData.CreateNew();
        UndertaleData compareData = UndertaleData.CreateNew();

        UndertaleGameObject baseB = AddObject(baseData, "obj_b", depth: 3);
        UndertaleGameObject baseA = AddObject(baseData, "obj_a", depth: 4);
        baseA.ParentId = baseB;

        UndertaleGameObject compareA = AddObject(compareData, "obj_a", depth: 4);
        UndertaleGameObject compareB = AddObject(compareData, "obj_b", depth: 3);
        compareA.ParentId = compareB;

        DataFileDiffResult result = new DataFileDiffer(baseData, compareData).Compare();
        AssetCategoryDiff objects = result.Categories.Single(c => c.Id == "GameObjects");

        Assert.Equal(0, objects.AddedCount);
        Assert.Equal(0, objects.RemovedCount);
        Assert.Equal(0, objects.ChangedCount);
    }

    [Fact]
    public void Compare_DetectsTextureDataChangeThroughSprite()
    {
        UndertaleData baseData = UndertaleData.CreateNew();
        UndertaleData compareData = UndertaleData.CreateNew();

        AddSpriteWithTexture(baseData, "spr_x", pixelValue: 0);
        AddSpriteWithTexture(compareData, "spr_x", pixelValue: 255);

        DataFileDiffResult result = new DataFileDiffer(baseData, compareData).Compare();
        AssetCategoryDiff sprites = result.Categories.Single(c => c.Id == "Sprites");

        Assert.Equal(AssetChangeKind.Changed, Find(sprites, "spr_x").Change);
    }

    [Fact]
    public void PropertyDiff_ReportsPathAndValues()
    {
        UndertaleData data = UndertaleData.CreateNew();
        UndertaleGameObject baseObject = AddObject(data, "obj_a", depth: 1);
        UndertaleGameObject compareObject = AddObject(data, "obj_a", depth: 2);

        PropertyDiffResult result = AssetPropertyDiff.Compare(baseObject, compareObject);

        Assert.Contains(result.Differences, d => d.Path == "Depth" && d.BaseValue == "1" && d.CompareValue == "2");
    }

    [Fact]
    public void CodeFingerprint_IgnoresAssetIdRenumbering()
    {
        UndertaleData baseData = UndertaleData.CreateNew();
        UndertaleData compareData = UndertaleData.CreateNew();

        AddSprite(baseData, "spr_a");
        AddSprite(baseData, "spr_b");
        AddSprite(compareData, "spr_b");
        AddSprite(compareData, "spr_a");

        UndertaleCode baseCode = AddCode(baseData, "gml_test", assetType: 1, assetId: 0);
        UndertaleCode compareCode = AddCode(compareData, "gml_test", assetType: 1, assetId: 1);

        Assert.Equal(CodeFingerprint.Compute(baseCode, baseData), CodeFingerprint.Compute(compareCode, compareData));
    }

    [Fact]
    public void CodeFingerprint_DetectsConstantChange()
    {
        UndertaleData baseData = UndertaleData.CreateNew();
        UndertaleData compareData = UndertaleData.CreateNew();

        UndertaleCode baseCode = AddCode(baseData, "gml_test", constant: 5);
        UndertaleCode compareCode = AddCode(compareData, "gml_test", constant: 6);

        Assert.NotEqual(CodeFingerprint.Compute(baseCode, baseData), CodeFingerprint.Compute(compareCode, compareData));
    }

    [Fact]
    public void TextLineDiff_ProducesUnifiedHunks()
    {
        var lines = TextLineDiff.Diff("line one\nline two\nline three", "line one\nline three");
        string unified = TextLineDiff.FormatUnified(lines);

        Assert.Contains("-line two", unified);
        Assert.DoesNotContain("+line two", unified);
        Assert.Contains(" line one", unified);
    }

    private static AssetDiffEntry Find(AssetCategoryDiff category, string name)
    {
        return category.Entries.Single(e => e.Name == name);
    }

    private static UndertaleGameObject AddObject(UndertaleData data, string name, int depth)
    {
        UndertaleGameObject obj = new()
        {
            Name = data.Strings.MakeString(name),
            Depth = depth
        };
        data.GameObjects.Add(obj);
        return obj;
    }

    private static UndertaleSprite AddSprite(UndertaleData data, string name)
    {
        UndertaleSprite sprite = new() { Name = data.Strings.MakeString(name) };
        data.Sprites.Add(sprite);
        return sprite;
    }

    private static void AddSpriteWithTexture(UndertaleData data, string name, byte pixelValue)
    {
        GMImage image = new(2, 2);
        image.GetRawImageData()[0] = pixelValue;

        UndertaleEmbeddedTexture embedded = new();
        embedded.TextureData = new UndertaleEmbeddedTexture.TexData { Image = image };
        data.EmbeddedTextures.Add(embedded);

        UndertaleTexturePageItem item = new()
        {
            Name = data.Strings.MakeString(""),
            TexturePage = embedded,
            SourceWidth = 2,
            SourceHeight = 2,
            TargetWidth = 2,
            TargetHeight = 2
        };
        data.TexturePageItems.Add(item);

        UndertaleSprite sprite = AddSprite(data, name);
        sprite.Width = 2;
        sprite.Height = 2;
        sprite.Textures.Add(new UndertaleSprite.TextureEntry { Texture = item });
    }

    private static UndertaleCode AddCode(UndertaleData data, string name, int assetType = 0, int assetId = 0, int constant = 0)
    {
        UndertaleCode code = new() { Name = data.Strings.MakeString(name) };
        if (assetType != 0)
        {
            code.Instructions.Add(new UndertaleInstruction
            {
                Kind = UndertaleInstruction.Opcode.Push,
                Type1 = UndertaleInstruction.DataType.Int32,
                IntArgument = (assetType << 24) | assetId
            });
        }
        else
        {
            code.Instructions.Add(new UndertaleInstruction
            {
                Kind = UndertaleInstruction.Opcode.Push,
                Type1 = UndertaleInstruction.DataType.Int32,
                IntArgument = constant
            });
        }
        code.Instructions.Add(new UndertaleInstruction
        {
            Kind = UndertaleInstruction.Opcode.Popz
        });
        data.Code.Add(code);
        return code;
    }
}
