using System.Linq;
using UndertaleModLib;
using UndertaleModLib.Diff;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
using Xunit;

namespace UndertaleModLibTests.Diff;

public class AssetPropertyDiffTests
{
    [Fact]
    public void Compare_ReportsTextureImageAndMaskDifferences_ThroughReferences()
    {
        UndertaleData baseData = UndertaleData.CreateNew();
        UndertaleData compareData = UndertaleData.CreateNew();
        foreach (UndertaleData data in new[] { baseData, compareData })
        {
            data.GeneralInfo.Major = 2024;
            data.GeneralInfo.Minor = 8;
        }

        AddSprite(baseData, imageMarker: 0x11, maskByte: 0b11000000);
        AddSprite(compareData, imageMarker: 0x22, maskByte: 0b10110000);

        PropertyDiffResult diff = AssetPropertyDiff.Compare(baseData.Sprites[0], compareData.Sprites[0]);

        // Differences inside referenced positional resources (texture page item -> texture ->
        // image, and the collision mask bytes) must be visible as property rows.
        Assert.Contains(diff.Differences, d => d.Path.Contains("Image", System.StringComparison.Ordinal));
        Assert.Contains(diff.Differences, d => d.Path.Contains("Data", System.StringComparison.Ordinal));
        Assert.All(diff.Differences, d => Assert.DoesNotContain("Name", d.Path, System.StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_ReturnsNoDifferences_ForIdenticalSprites()
    {
        UndertaleData baseData = UndertaleData.CreateNew();
        UndertaleData compareData = UndertaleData.CreateNew();
        foreach (UndertaleData data in new[] { baseData, compareData })
        {
            data.GeneralInfo.Major = 2024;
            data.GeneralInfo.Minor = 8;
        }

        AddSprite(baseData, imageMarker: 0x11, maskByte: 0b11000000);
        AddSprite(compareData, imageMarker: 0x11, maskByte: 0b11000000);

        PropertyDiffResult diff = AssetPropertyDiff.Compare(baseData.Sprites[0], compareData.Sprites[0]);
        Assert.Empty(diff.Differences);
    }

    private static void AddSprite(UndertaleData data, byte imageMarker, byte maskByte)
    {
        GMImage image = new(2, 1);
        image.GetRawImageData()[0] = imageMarker;
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
            Name = data.Strings.MakeString("spr_test"),
            Width = 2,
            Height = 1,
        };
        sprite.Textures.Add(new UndertaleSprite.TextureEntry { Texture = pageItem });
        sprite.CollisionMasks.Add(sprite.NewMaskEntry(data));
        sprite.CollisionMasks[^1].Data = [maskByte];
        data.Sprites.Add(sprite);
    }
}
