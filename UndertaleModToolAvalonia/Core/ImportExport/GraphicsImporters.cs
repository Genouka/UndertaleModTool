using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using ImageMagick;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

namespace UndertaleModToolAvalonia;

public partial class ImportExportService
{
    internal enum SpriteType
    {
        Sprite,
        Background,
        Font,
        Unknown,
    }

    internal static SpriteType GetSpriteType(string path)
    {
        string folderPath = Path.GetDirectoryName(path);
        string folderName = folderPath is null ? "" : new DirectoryInfo(folderPath).Name;
        string lowerName = folderName.ToLower();

        if (lowerName == "backgrounds" || lowerName == "background")
            return SpriteType.Background;
        else if (lowerName == "fonts" || lowerName == "font")
            return SpriteType.Font;
        else if (lowerName == "sprites" || lowerName == "sprite")
            return SpriteType.Sprite;
        return SpriteType.Unknown;
    }

    /// <summary>Imports all sprites/backgrounds in a folder (and subdirectories) into the data file.</summary>
    public void ImportGraphics()
    {
        EnsureDataLoaded();

        bool recursiveCheck = ScriptQuestion(
            "This imports all sprites in all subdirectories recursively.\n" +
            "If an image file is in a folder named \"Backgrounds\", then the image will be imported as a background.\n" +
            "Otherwise, the image will be imported as a sprite.\n" +
            "Do you want to continue?");
        if (!recursiveCheck)
            return;

        string importFolder = PromptChooseDirectory() ?? throw new Exception("The import folder was not set.");

        Regex sprFrameRegex = new(@"^(.+?)(?:_(\d+))$", RegexOptions.Compiled);

        bool importAsSprite = false;
        string currSpriteName = null;
        bool hadMessage = false;

        // Stop the script if there's missing sprite entries or w/e.
        string[] validationFiles = Directory.GetFiles(importFolder, "*.png", SearchOption.AllDirectories);
        foreach (string file in validationFiles)
        {
            string stripped = Path.GetFileNameWithoutExtension(file);
            string fileNameWithExtension = Path.GetFileName(file);

            SpriteType spriteType = GetSpriteType(file);

            if ((spriteType != SpriteType.Sprite) && (spriteType != SpriteType.Background))
            {
                if (!hadMessage)
                {
                    hadMessage = true;
                    importAsSprite = ScriptQuestion(fileNameWithExtension + @" is in an incorrectly-named folder (valid names being ""Sprites"" and ""Backgrounds""). Would you like to import these images as sprites?
Pressing ""No"" will cause the program to ignore these images.");
                }

                if (!importAsSprite)
                {
                    continue;
                }
                else
                {
                    spriteType = SpriteType.Sprite;
                }
            }

            if (spriteType == SpriteType.Background)
                continue;

            var spriteParts = sprFrameRegex.Match(stripped);
            // Allow sprites without underscores
            if (!spriteParts.Groups[2].Success)
                continue;

            string spriteName = spriteParts.Groups[1].Value;

            if (!Int32.TryParse(spriteParts.Groups[2].Value, out int frame))
                throw new Exception($"{spriteName} has an invalid frame index.");
            if (frame < 0)
                throw new Exception($"{spriteName} is using an invalid numbering scheme. The script has stopped for your own protection.");

            // If it's not a first frame of the sprite
            if (spriteName == currSpriteName)
                continue;

            string[][] spriteFrames = Directory.GetFiles(importFolder, $"{spriteName}_*.png", SearchOption.AllDirectories)
                                               .Select(x =>
                                               {
                                                   var match = sprFrameRegex.Match(Path.GetFileNameWithoutExtension(x));
                                                   if (match.Groups[2].Success)
                                                       return new string[] { match.Groups[1].Value, match.Groups[2].Value };
                                                   else
                                                       return null;
                                               })
                                               .OfType<string[]>().ToArray();
            if (spriteFrames.Length == 1)
            {
                currSpriteName = null;
                continue;
            }

            int[] frameIndexes = spriteFrames.Select(x =>
            {
                if (Int32.TryParse(x[1], out int f))
                    return (int?)f;
                else
                    return null;
            }).OfType<int?>().Cast<int>().OrderBy(x => x).ToArray();
            if (frameIndexes.Length == 1)
            {
                currSpriteName = null;
                continue;
            }

            if (frameIndexes is not [0, ..])
                throw new Exception(spriteName + " is missing an index for frame 0.\nMake sure it is named with \"_0\" at the end accordingly.");
            for (int i = 0; i < frameIndexes.Length - 1; i++)
            {
                int num = frameIndexes[i];
                int nextNum = frameIndexes[i + 1];

                if (nextNum - num > 1)
                    throw new Exception(spriteName + " is missing one or more indexes.\nThe detected missing index is: " + (num + 1));
            }

            currSpriteName = spriteName;
        }

        string packDir = Path.Join(ExePath, "Packager");
        Directory.CreateDirectory(packDir);

        string sourcePath = importFolder;
        string searchPattern = "*.png";
        string outName = Path.Join(packDir, "atlas.txt");
        int textureSize = 2048;
        int paddingValue = 2;
        bool debug = false;

        TexturePacker packer = new();
        packer.Process(sourcePath, searchPattern, textureSize, paddingValue, debug, loadImages: true, trimImages: true);
        packer.SaveAtlasses(outName);

        int lastTextPage = Data.EmbeddedTextures.Count - 1;
        int lastTextPageItem = Data.TexturePageItems.Count - 1;

        bool noMasksForBasicRectangles = Data.IsVersionAtLeast(2022, 9);
        bool bboxMasks = Data.IsVersionAtLeast(2024, 6);
        Dictionary<UndertaleSprite, TextureNode> maskNodes = new();

        string prefix = Path.Join(Path.GetDirectoryName(outName), Path.GetFileNameWithoutExtension(outName));
        int atlasCount = 0;
        foreach (TextureAtlas atlas in packer.Atlasses)
        {
            string atlasName = $"{prefix}{atlasCount:000}.png";
            using MagickImage atlasImage = TextureWorker.ReadBGRAImageFromFile(atlasName);
            IPixelCollection<byte> atlasPixels = atlasImage.GetPixels();

            UndertaleEmbeddedTexture texture = new()
            {
                Name = new UndertaleString("Texture " + ++lastTextPage),
            };
            texture.TextureData.Image = GMImage.FromMagickImage(atlasImage).ConvertToPng();
            Data.EmbeddedTextures.Add(texture);
            foreach (TextureNode n in atlas.Nodes)
            {
                if (n.Texture is not null)
                {
                    UndertaleTexturePageItem texturePageItem = new()
                    {
                        Name = new UndertaleString("PageItem " + ++lastTextPageItem),
                        SourceX = (ushort)n.Bounds.X,
                        SourceY = (ushort)n.Bounds.Y,
                        SourceWidth = (ushort)n.Bounds.Width,
                        SourceHeight = (ushort)n.Bounds.Height,
                        TargetX = (ushort)n.Texture.TargetX,
                        TargetY = (ushort)n.Texture.TargetY,
                        TargetWidth = (ushort)n.Bounds.Width,
                        TargetHeight = (ushort)n.Bounds.Height,
                        BoundingWidth = (ushort)n.Texture.BoundingWidth,
                        BoundingHeight = (ushort)n.Texture.BoundingHeight,
                        TexturePage = texture,
                    };
                    Data.TexturePageItems.Add(texturePageItem);

                    string stripped = Path.GetFileNameWithoutExtension(n.Texture.Source);

                    SpriteType spriteType = GetSpriteType(n.Texture.Source);
                    if (importAsSprite && (spriteType == SpriteType.Unknown || spriteType == SpriteType.Font))
                        spriteType = SpriteType.Sprite;

                    if (spriteType == SpriteType.Background)
                    {
                        UndertaleBackground background = Data.Backgrounds.ByName(stripped);
                        if (background is not null)
                            background.Texture = texturePageItem;
                        else
                        {
                            UndertaleString backgroundUTString = Data.Strings.MakeString(stripped);
                            background = new UndertaleBackground()
                            {
                                Name = backgroundUTString,
                                Transparent = false,
                                Preload = false,
                                Texture = texturePageItem,
                            };
                            Data.Backgrounds.Add(background);
                        }
                        Project?.MarkAssetForExport(background);
                    }
                    else if (spriteType == SpriteType.Sprite)
                    {
                        string spriteName;
                        int frame = 0;
                        try
                        {
                            var spriteParts = sprFrameRegex.Match(stripped);
                            spriteName = spriteParts.Groups[1].Value;
                            Int32.TryParse(spriteParts.Groups[2].Value, out frame);

                            if (string.IsNullOrWhiteSpace(spriteName))
                                throw new Exception();
                        }
                        catch
                        {
                            ScriptWarning($"Image {stripped} has an invalid name. Skipping...");
                            continue;
                        }

                        UndertaleSprite.TextureEntry texentry = new() { Texture = texturePageItem };

                        UndertaleSprite sprite = Data.Sprites.ByName(spriteName);
                        if (sprite is null)
                        {
                            UndertaleString spriteUTString = Data.Strings.MakeString(spriteName);
                            UndertaleSprite newSprite = new()
                            {
                                Name = spriteUTString,
                                Width = (uint)n.Texture.BoundingWidth,
                                Height = (uint)n.Texture.BoundingHeight,
                                MarginLeft = n.Texture.TargetX,
                                MarginRight = n.Texture.TargetX + n.Bounds.Width - 1,
                                MarginTop = n.Texture.TargetY,
                                MarginBottom = n.Texture.TargetY + n.Bounds.Height - 1,
                            };
                            if (frame > 0)
                            {
                                for (int i = 0; i < frame; i++)
                                    newSprite.Textures.Add(null);
                            }

                            if (!noMasksForBasicRectangles ||
                                newSprite.SepMasks is not (UndertaleSprite.SepMaskType.AxisAlignedRect or UndertaleSprite.SepMaskType.RotatedRect))
                            {
                                maskNodes.Add(newSprite, n);
                            }

                            newSprite.Textures.Add(texentry);
                            Data.Sprites.Add(newSprite);
                            Project?.MarkAssetForExport(newSprite);
                            continue;
                        }

                        Project?.MarkAssetForExport(sprite);

                        if (frame > sprite.Textures.Count - 1)
                        {
                            while (frame > sprite.Textures.Count - 1)
                                sprite.Textures.Add(texentry);
                            continue;
                        }

                        sprite.Textures[frame] = texentry;

                        uint oldWidth = sprite.Width, oldHeight = sprite.Height;
                        sprite.Width = (uint)n.Texture.BoundingWidth;
                        sprite.Height = (uint)n.Texture.BoundingHeight;
                        bool changedSpriteDimensions = (oldWidth != sprite.Width || oldHeight != sprite.Height);

                        bool grewBoundingBox = false;
                        bool fullImageBbox = sprite.BBoxMode == 1;
                        bool manualBbox = sprite.BBoxMode == 2;
                        if (!manualBbox)
                        {
                            int marginLeft = fullImageBbox ? 0 : n.Texture.TargetX;
                            int marginRight = fullImageBbox ? ((int)sprite.Width - 1) : (n.Texture.TargetX + n.Bounds.Width - 1);
                            int marginTop = fullImageBbox ? 0 : n.Texture.TargetY;
                            int marginBottom = fullImageBbox ? ((int)sprite.Height - 1) : (n.Texture.TargetY + n.Bounds.Height - 1);
                            if (marginLeft < sprite.MarginLeft) { sprite.MarginLeft = marginLeft; grewBoundingBox = true; }
                            if (marginTop < sprite.MarginTop) { sprite.MarginTop = marginTop; grewBoundingBox = true; }
                            if (marginRight > sprite.MarginRight) { sprite.MarginRight = marginRight; grewBoundingBox = true; }
                            if (marginBottom > sprite.MarginBottom) { sprite.MarginBottom = marginBottom; grewBoundingBox = true; }
                        }

                        if (!noMasksForBasicRectangles ||
                            sprite.SepMasks is not (UndertaleSprite.SepMaskType.AxisAlignedRect or UndertaleSprite.SepMaskType.RotatedRect) ||
                            sprite.CollisionMasks.Count > 0)
                        {
                            if ((bboxMasks && grewBoundingBox) ||
                                (sprite.SepMasks is UndertaleSprite.SepMaskType.Precise && sprite.CollisionMasks.Count == 0) ||
                                (!bboxMasks && changedSpriteDimensions))
                            {
                                maskNodes[sprite] = n;
                            }
                        }
                    }
                }
            }

            foreach ((UndertaleSprite maskSpr, TextureNode maskNode) in maskNodes)
            {
                maskSpr.CollisionMasks.Clear();
                maskSpr.CollisionMasks.Add(maskSpr.NewMaskEntry(Data));
                (int maskWidth, int maskHeight) = maskSpr.CalculateMaskDimensions(Data);
                int maskStride = ((maskWidth + 7) / 8) * 8;

                BitArray maskingBitArray = new(maskStride * maskHeight);
                for (int y = 0; y < maskHeight && y < maskNode.Bounds.Height; y++)
                {
                    for (int x = 0; x < maskWidth && x < maskNode.Bounds.Width; x++)
                    {
                        IMagickColor<byte> pixelColor = atlasPixels.GetPixel(x + maskNode.Bounds.X, y + maskNode.Bounds.Y).ToColor();
                        if (bboxMasks)
                            maskingBitArray[(y * maskStride) + x] = (pixelColor.A > 0);
                        else
                            maskingBitArray[((y + maskNode.Texture.TargetY) * maskStride) + x + maskNode.Texture.TargetX] = (pixelColor.A > 0);
                    }
                }
                BitArray tempBitArray = new(maskingBitArray.Length);
                for (int i = 0; i < maskingBitArray.Length; i += 8)
                {
                    for (int j = 0; j < 8; j++)
                    {
                        tempBitArray[j + i] = maskingBitArray[-(j - 7) + i];
                    }
                }

                int numBytes = maskingBitArray.Length / 8;
                byte[] bytes = new byte[numBytes];
                tempBitArray.CopyTo(bytes, 0);
                for (int i = 0; i < bytes.Length; i++)
                    maskSpr.CollisionMasks[0].Data[i] = bytes[i];
            }
            maskNodes.Clear();

            atlasCount++;
        }

        packer.DisposeImages();
        HideProgressBar();
        ScriptMessage("Import Complete!");
    }

    /// <summary>Imports sprites/backgrounds with more options (origin, anim speed, more formats).</summary>
    public async void ImportGraphicsAdvanced()
    {
        EnsureDataLoaded();

        bool recursiveCheck = ScriptQuestion(
            "This script imports all sprites in all subdirectories recursively.\n" +
            "If an image file is in a folder named \"Backgrounds\", then the image will be imported as a background.\n" +
            "Otherwise, the image will be imported as a sprite, and allow you to select its origin point and animation speed (if applicable).\n" +
            "Accepted sprite formats: separate frames starting at 0 or 1 (sprite_N.png), GM-style strip (sprite_stripN.png), animated GIF (sprite.gif), optionally single image (sprite.png).\n" +
            "Accepted background formats: single image (bg.png), single-frame GIF (bg.gif).\n" +
            "Do you want to continue?");
        if (!recursiveCheck)
            return;

        string importFolder = PromptChooseDirectory() ?? throw new Exception("The import folder was not set.");

        bool importAsSprite = true;
        bool importFrameless = false;
        HashSet<string> spritesStartAt1 = new();
        bool hadMessage = false;
        bool hadFramelessMessage = false;

        // Stop the script if there's missing sprite entries or w/e.
        string[] validationFiles = Directory.GetFiles(importFolder, "*", SearchOption.AllDirectories);
        foreach (string file in validationFiles)
        {
            string ext = Path.GetExtension(file).ToLower();
            if (ext != ".png" && ext != ".gif")
                continue;

            string stripped = Path.GetFileNameWithoutExtension(file);

            SpriteType spriteType = GetSpriteType(file);

            if ((spriteType != SpriteType.Sprite) && (spriteType != SpriteType.Background))
            {
                spriteType = SpriteType.Sprite;
                if (!hadMessage)
                {
                    hadMessage = true;
                    // Accept these as sprites.
                }
            }

            if (spriteType == SpriteType.Background)
                continue;

            // Check for duplicate filenames
            string[] dupFiles = Directory.GetFiles(importFolder, Path.GetFileName(file), SearchOption.AllDirectories);
            if (dupFiles.Length > 1)
                throw new Exception("Duplicate file detected. There are " + dupFiles.Length + " files named: " + Path.GetFileName(file));

            Match stripMatch = Regex.Match(stripped, @"(.*)_strip(\d+)");
            if (stripMatch.Success)
            {
                string frameCountStr = stripMatch.Groups[2].Value;
                if (!Int32.TryParse(frameCountStr, out int frames) || frames <= 0)
                    throw new Exception(Path.GetFileName(file) + " has an invalid strip numbering scheme. Script has been stopped.");
                continue;
            }

            int lastUnderscore = stripped.LastIndexOf('_');
            string spriteName = "";
            int frame = 0;
            try
            {
                Int32.Parse(stripped.Substring(lastUnderscore + 1));
                spriteName = stripped.Substring(0, lastUnderscore);
            }
            catch
            {
                if (ext == ".gif")
                {
                    // gif imports as frames; no frame number needed.
                    spriteName = stripped;
                }
                else
                {
                    if (!hadFramelessMessage)
                    {
                        hadFramelessMessage = true;
                        importFrameless = ScriptQuestion(Path.GetFileName(file) + @" does not seem to have a frame number or count. Import this image as a single-frame sprite named " + stripped + @"?
Pressing ""No"" will cause the program to ignore these images.");
                    }
                    if (importFrameless)
                        spriteName = stripped;
                    else
                        continue;
                }
            }

            if (spriteName == stripped && !importFrameless && ext != ".gif")
                continue;

            if (frame == 0 && spriteName != stripped)
            {
                if (!Int32.TryParse(stripped.Substring(lastUnderscore + 1), out frame) || frame < 0)
                    throw new Exception(spriteName + " is using an invalid numbering scheme. The script has stopped for your own protection.");
            }

            if (frame == 0)
                continue;

            string prevFrameName = spriteName + "_" + (frame - 1).ToString() + ".png";
            string[] previousFrameFiles = Directory.GetFiles(importFolder, prevFrameName, SearchOption.AllDirectories);
            if (previousFrameFiles.Length < 1)
            {
                if (frame == 1)
                    spritesStartAt1.Add(spriteName);
                else
                    throw new Exception(spriteName + " is missing one or more indexes. The detected missing index is: " + prevFrameName);
            }
        }

        // Options dialog for sprite parameters.
        bool isSpecial = false;
        uint specialVer = 1;
        float animSpd = 1;
        int playback = 0;
        string offresult = "Top Left";

        SpriteImportOptionsWindow optionsWindow = new(Data.IsGameMaker2());
        Control? view = mainVM.View as Control;
        Window? owner = view is not null ? WindowHost.ResolveOwner(view) : null;
        await WindowHost.ShowDialog(owner, optionsWindow);
        if (!optionsWindow.Succeeded)
            return;

        isSpecial = optionsWindow.IsSpecialType;
        specialVer = optionsWindow.SpecialVersion;
        animSpd = optionsWindow.AnimationSpeed;
        playback = optionsWindow.PlaybackType;
        offresult = optionsWindow.OriginPosition;

        string packDir = Path.Join(ExePath, "Packager");
        Directory.CreateDirectory(packDir);

        bool noMasksForBasicRectangles = Data.IsVersionAtLeast(2022, 9);
        bool bboxMasks = Data.IsVersionAtLeast(2024, 6);
        Dictionary<UndertaleSprite, TextureNode> maskNodes = new();

        int textureSize = 2048;
        int paddingValue = 2;

        TexturePacker packer = new();

        // Scan all files, including GIFs/strips.
        DirectoryInfo di = new(importFolder);
        FileInfo[] files = di.GetFiles("*", SearchOption.AllDirectories);
        foreach (FileInfo fi in files)
        {
            string ext = fi.Extension.ToLower();
            if (ext == ".gif")
            {
                string dirName = fi.DirectoryName;
                string spriteName = Path.GetFileNameWithoutExtension(fi.FullName);
                SpriteType spriteType = GetSpriteType(fi.FullName);
                bool isSprite = (spriteType == SpriteType.Sprite) || (spriteType == SpriteType.Unknown && importAsSprite);

                MagickReadSettings settings = new() { ColorSpace = ColorSpace.sRGB };
                using MagickImageCollection gif = new(fi.FullName, settings);
                int frames = gif.Count;
                if (!isSprite && frames > 1)
                    throw new Exception(fi.FullName + " is a " + spriteType + ", but has more than 1 frame. Script has been stopped.");

                for (int i = frames - 1; i >= 0; i--)
                {
                    packer.AddSource((MagickImage)gif[i],
                        Path.Join(dirName, isSprite ? (spriteName + "_" + i + ".png") : (spriteName + ".png")),
                        trimImages: true);
                    // don't auto-dispose
                    gif.RemoveAt(i);
                }
            }
            else if (ext == ".png")
            {
                Match stripMatch = null;
                if (GetSpriteType(fi.FullName) == SpriteType.Sprite)
                {
                    stripMatch = Regex.Match(Path.GetFileNameWithoutExtension(fi.Name), @"(.*)_strip(\d+)");
                }
                if (stripMatch is not null && stripMatch.Success)
                {
                    string spriteName = stripMatch.Groups[1].Value;
                    string frameCountStr = stripMatch.Groups[2].Value;

                    if (!UInt32.TryParse(frameCountStr, out uint frames) || frames <= 0)
                        throw new Exception(fi.FullName + " has an invalid strip numbering scheme. Script has been stopped.");

                    MagickReadSettings settings = new() { ColorSpace = ColorSpace.sRGB };
                    using MagickImage img = new(fi.FullName, settings);
                    if ((img.Width % frames) > 0)
                        throw new Exception(fi.FullName + " has a width not divisible by the number of frames. Script has been stopped.");

                    string dirName = fi.DirectoryName;
                    uint frameWidth = (uint)img.Width / frames;
                    for (uint i = 0; i < frames; i++)
                    {
                        packer.AddSource(
                            (MagickImage)img.Clone((int)(frameWidth * i), 0, frameWidth, (uint)img.Height),
                            Path.Join(dirName, (spriteName + "_" + i + ".png")),
                            trimImages: true);
                    }
                }
                else
                {
                    MagickReadSettings settings = new() { ColorSpace = ColorSpace.sRGB };
                    MagickImage img = new(fi.FullName, settings);
                    bool isBackground = GetSpriteType(fi.FullName) == SpriteType.Background;
                    packer.AddSource(img, fi.FullName, trimImages: !isBackground);
                }
            }
            // Other formats are ignored for atlas packing.
        }

        // Layout atlases.
        List<TextureInfo> textures = packer.SourceTextures.ToList();
        while (textures.Count > 0)
        {
            TextureAtlas atlas = new() { Width = textureSize, Height = textureSize };
            List<TextureInfo> leftovers = packer.LayoutAtlasPublic(textures, atlas);
            if (leftovers.Count == 0)
            {
                while (leftovers.Count == 0)
                {
                    atlas.Width /= 2;
                    atlas.Height /= 2;
                    leftovers = packer.LayoutAtlasPublic(textures, atlas);
                }
                atlas.Width = (atlas.Width == 0) ? 1 : atlas.Width * 2;
                atlas.Height = (atlas.Height == 0) ? 1 : atlas.Height * 2;
                leftovers = packer.LayoutAtlasPublic(textures, atlas);
            }
            packer.Atlasses.Add(atlas);
            textures = leftovers;
        }

        string outName = Path.Join(packDir, "atlas.txt");
        packer.SaveAtlasses(outName);

        int lastTextPage = Data.EmbeddedTextures.Count - 1;
        int lastTextPageItem = Data.TexturePageItems.Count - 1;

        string prefix = Path.Join(Path.GetDirectoryName(outName), Path.GetFileNameWithoutExtension(outName));
        int atlasCount = 0;
        foreach (TextureAtlas atlas in packer.Atlasses)
        {
            string atlasName = $"{prefix}{atlasCount:000}.png";
            using MagickImage atlasImage = TextureWorker.ReadBGRAImageFromFile(atlasName);
            IPixelCollection<byte> atlasPixels = atlasImage.GetPixels();

            UndertaleEmbeddedTexture texture = new()
            {
                Name = new UndertaleString("Texture " + ++lastTextPage),
            };
            texture.TextureData.Image = GMImage.FromMagickImage(atlasImage).ConvertToPng();
            Data.EmbeddedTextures.Add(texture);
            foreach (TextureNode n in atlas.Nodes)
            {
                if (n.Texture is null)
                    continue;

                UndertaleTexturePageItem texturePageItem = new()
                {
                    Name = new UndertaleString("PageItem " + ++lastTextPageItem),
                    SourceX = (ushort)n.Bounds.X,
                    SourceY = (ushort)n.Bounds.Y,
                    SourceWidth = (ushort)n.Bounds.Width,
                    SourceHeight = (ushort)n.Bounds.Height,
                    TargetX = (ushort)n.Texture.TargetX,
                    TargetY = (ushort)n.Texture.TargetY,
                    TargetWidth = (ushort)n.Bounds.Width,
                    TargetHeight = (ushort)n.Bounds.Height,
                    BoundingWidth = (ushort)n.Texture.BoundingWidth,
                    BoundingHeight = (ushort)n.Texture.BoundingHeight,
                    TexturePage = texture,
                };
                Data.TexturePageItems.Add(texturePageItem);

                string stripped = Path.GetFileNameWithoutExtension(n.Texture.Source);

                SpriteType spriteType = GetSpriteType(n.Texture.Source);
                if (importAsSprite && (spriteType == SpriteType.Unknown || spriteType == SpriteType.Font))
                    spriteType = SpriteType.Sprite;

                if (spriteType == SpriteType.Background)
                {
                    UndertaleBackground background = Data.Backgrounds.ByName(stripped);
                    if (background is not null)
                        background.Texture = texturePageItem;
                    else
                    {
                        background = new UndertaleBackground()
                        {
                            Name = Data.Strings.MakeString(stripped),
                            Transparent = false,
                            Preload = false,
                            Texture = texturePageItem,
                        };
                        Data.Backgrounds.Add(background);
                    }
                    Project?.MarkAssetForExport(background);
                }
                else if (spriteType == SpriteType.Sprite)
                {
                    string spriteName;
                    int frame;
                    try
                    {
                        int lastUnderscore = stripped.LastIndexOf('_');
                        Int32.Parse(stripped.Substring(lastUnderscore + 1));
                        spriteName = stripped.Substring(0, lastUnderscore);
                        frame = Int32.Parse(stripped.Substring(lastUnderscore + 1));
                    }
                    catch
                    {
                        if (!importFrameless)
                            continue;
                        spriteName = stripped;
                        frame = 0;
                    }

                    if (spritesStartAt1.Contains(spriteName))
                        frame--;

                    UndertaleSprite.TextureEntry texentry = new() { Texture = texturePageItem };

                    UndertaleSprite sprite = Data.Sprites.ByName(spriteName);
                    if (sprite is null)
                    {
                        UndertaleString spriteUTString = Data.Strings.MakeString(spriteName);
                        UndertaleSprite newSprite = new()
                        {
                            Name = spriteUTString,
                            Width = (uint)n.Texture.BoundingWidth,
                            Height = (uint)n.Texture.BoundingHeight,
                            MarginLeft = n.Texture.TargetX,
                            MarginRight = n.Texture.TargetX + n.Bounds.Width - 1,
                            MarginTop = n.Texture.TargetY,
                            MarginBottom = n.Texture.TargetY + n.Bounds.Height - 1,
                            GMS2PlaybackSpeedType = (AnimSpeedType)playback,
                            GMS2PlaybackSpeed = animSpd,
                            IsSpecialType = isSpecial,
                            SVersion = specialVer,
                        };
                        switch (offresult)
                        {
                            case ("Top Left"): newSprite.OriginX = 0; newSprite.OriginY = 0; break;
                            case ("Top Center"): newSprite.OriginX = (int)(newSprite.Width / 2); newSprite.OriginY = 0; break;
                            case ("Top Right"): newSprite.OriginX = (int)newSprite.Width; newSprite.OriginY = 0; break;
                            case ("Center Left"): newSprite.OriginX = 0; newSprite.OriginY = (int)(newSprite.Height / 2); break;
                            case ("Center"): newSprite.OriginX = (int)(newSprite.Width / 2); newSprite.OriginY = (int)(newSprite.Height / 2); break;
                            case ("Center Right"): newSprite.OriginX = (int)newSprite.Width; newSprite.OriginY = (int)(newSprite.Height / 2); break;
                            case ("Bottom Left"): newSprite.OriginX = 0; newSprite.OriginY = (int)newSprite.Height; break;
                            case ("Bottom Center"): newSprite.OriginX = (int)(newSprite.Width / 2); newSprite.OriginY = (int)newSprite.Height; break;
                            case ("Bottom Right"): newSprite.OriginX = (int)newSprite.Width; newSprite.OriginY = (int)newSprite.Height; break;
                        }
                        if (frame > 0)
                        {
                            for (int i = 0; i < frame; i++)
                                newSprite.Textures.Add(null);
                        }

                        if (!noMasksForBasicRectangles ||
                            newSprite.SepMasks is not (UndertaleSprite.SepMaskType.AxisAlignedRect or UndertaleSprite.SepMaskType.RotatedRect))
                        {
                            maskNodes.Add(newSprite, n);
                        }

                        newSprite.Textures.Add(texentry);
                        Data.Sprites.Add(newSprite);
                        Project?.MarkAssetForExport(newSprite);
                        continue;
                    }

                    Project?.MarkAssetForExport(sprite);

                    if (frame > sprite.Textures.Count - 1)
                    {
                        while (frame > sprite.Textures.Count - 1)
                            sprite.Textures.Add(texentry);
                        continue;
                    }

                    sprite.Textures[frame] = texentry;
                    sprite.GMS2PlaybackSpeedType = (AnimSpeedType)playback;
                    sprite.GMS2PlaybackSpeed = animSpd;
                    sprite.IsSpecialType = isSpecial;
                    sprite.SVersion = specialVer;

                    uint oldWidth = sprite.Width, oldHeight = sprite.Height;
                    sprite.Width = (uint)n.Texture.BoundingWidth;
                    sprite.Height = (uint)n.Texture.BoundingHeight;
                    bool changedSpriteDimensions = (oldWidth != sprite.Width || oldHeight != sprite.Height);

                    switch (offresult)
                    {
                        case ("Top Left"): sprite.OriginX = 0; sprite.OriginY = 0; break;
                        case ("Top Center"): sprite.OriginX = (int)(sprite.Width / 2); sprite.OriginY = 0; break;
                        case ("Top Right"): sprite.OriginX = (int)sprite.Width; sprite.OriginY = 0; break;
                        case ("Center Left"): sprite.OriginX = 0; sprite.OriginY = (int)(sprite.Height / 2); break;
                        case ("Center"): sprite.OriginX = (int)(sprite.Width / 2); sprite.OriginY = (int)(sprite.Height / 2); break;
                        case ("Center Right"): sprite.OriginX = (int)sprite.Width; sprite.OriginY = (int)(sprite.Height / 2); break;
                        case ("Bottom Left"): sprite.OriginX = 0; sprite.OriginY = (int)sprite.Height; break;
                        case ("Bottom Center"): sprite.OriginX = (int)(sprite.Width / 2); sprite.OriginY = (int)sprite.Height; break;
                        case ("Bottom Right"): sprite.OriginX = (int)sprite.Width; sprite.OriginY = (int)sprite.Height; break;
                    }

                    bool grewBoundingBox = false;
                    bool fullImageBbox = sprite.BBoxMode == 1;
                    bool manualBbox = sprite.BBoxMode == 2;
                    if (!manualBbox)
                    {
                        int marginLeft = fullImageBbox ? 0 : n.Texture.TargetX;
                        int marginRight = fullImageBbox ? ((int)sprite.Width - 1) : (n.Texture.TargetX + n.Bounds.Width - 1);
                        int marginTop = fullImageBbox ? 0 : n.Texture.TargetY;
                        int marginBottom = fullImageBbox ? ((int)sprite.Height - 1) : (n.Texture.TargetY + n.Bounds.Height - 1);
                        if (marginLeft < sprite.MarginLeft) { sprite.MarginLeft = marginLeft; grewBoundingBox = true; }
                        if (marginTop < sprite.MarginTop) { sprite.MarginTop = marginTop; grewBoundingBox = true; }
                        if (marginRight > sprite.MarginRight) { sprite.MarginRight = marginRight; grewBoundingBox = true; }
                        if (marginBottom > sprite.MarginBottom) { sprite.MarginBottom = marginBottom; grewBoundingBox = true; }
                    }

                    if (!noMasksForBasicRectangles ||
                        sprite.SepMasks is not (UndertaleSprite.SepMaskType.AxisAlignedRect or UndertaleSprite.SepMaskType.RotatedRect) ||
                        sprite.CollisionMasks.Count > 0)
                    {
                        if ((bboxMasks && grewBoundingBox) ||
                            (sprite.SepMasks is UndertaleSprite.SepMaskType.Precise && sprite.CollisionMasks.Count == 0) ||
                            (!bboxMasks && changedSpriteDimensions))
                        {
                            maskNodes[sprite] = n;
                        }
                    }
                }
            }

            foreach ((UndertaleSprite maskSpr, TextureNode maskNode) in maskNodes)
            {
                maskSpr.CollisionMasks.Clear();
                maskSpr.CollisionMasks.Add(maskSpr.NewMaskEntry(Data));
                (int maskWidth, int maskHeight) = maskSpr.CalculateMaskDimensions(Data);
                int maskStride = ((maskWidth + 7) / 8) * 8;

                BitArray maskingBitArray = new(maskStride * maskHeight);
                for (int y = 0; y < maskHeight && y < maskNode.Bounds.Height; y++)
                {
                    for (int x = 0; x < maskWidth && x < maskNode.Bounds.Width; x++)
                    {
                        IMagickColor<byte> pixelColor = atlasPixels.GetPixel(x + maskNode.Bounds.X, y + maskNode.Bounds.Y).ToColor();
                        if (bboxMasks)
                            maskingBitArray[(y * maskStride) + x] = (pixelColor.A > 0);
                        else
                            maskingBitArray[((y + maskNode.Texture.TargetY) * maskStride) + x + maskNode.Texture.TargetX] = (pixelColor.A > 0);
                    }
                }
                BitArray tempBitArray = new(maskingBitArray.Length);
                for (int i = 0; i < maskingBitArray.Length; i += 8)
                {
                    for (int j = 0; j < 8; j++)
                    {
                        tempBitArray[j + i] = maskingBitArray[-(j - 7) + i];
                    }
                }

                int numBytes = maskingBitArray.Length / 8;
                byte[] bytes = new byte[numBytes];
                tempBitArray.CopyTo(bytes, 0);
                for (int i = 0; i < bytes.Length; i++)
                    maskSpr.CollisionMasks[0].Data[i] = bytes[i];
            }
            maskNodes.Clear();

            atlasCount++;
        }

        packer.DisposeImages();
        HideProgressBar();
        ScriptMessage("Import Complete!");
    }

    /// <summary>ApplyBasicGraphicsMod: replaces existing sprite frame textures in place, with dimension checks.</summary>
    public void ApplyBasicGraphicsMod()
    {
        EnsureDataLoaded();

        string importFolder = PromptChooseDirectory() ?? throw new Exception("The import folder was not set.");

        string[] dirFiles = Directory.GetFiles(importFolder);
        List<(string filename, string strippedFilename, string spriteName, UndertaleSprite sprite, int frame)> images = new();

        // Stop the script if there's missing sprite entries or w/e.
        foreach (string file in dirFiles)
        {
            string filenameWithExtension = Path.GetFileName(file);
            if (!filenameWithExtension.EndsWith(".png", StringComparison.InvariantCultureIgnoreCase) || !filenameWithExtension.Contains("_"))
                continue;

            string stripped = Path.GetFileNameWithoutExtension(file);
            int lastUnderscore = stripped.LastIndexOf('_');
            string spriteName;
            try
            {
                spriteName = stripped.Substring(0, lastUnderscore);
            }
            catch
            {
                throw new Exception($"Getting the sprite name of {filenameWithExtension} failed.");
            }

            UndertaleSprite sprite = Data.Sprites.ByName(spriteName);
            if (sprite is null)
                throw new Exception($"{filenameWithExtension} could not be imported, as the sprite \"{spriteName}\" does not exist.");

            if (!int.TryParse(stripped.Substring(lastUnderscore + 1), out int frame))
                throw new Exception($"The frame index of {filenameWithExtension} could not be determined (should be an integer).");
            if (frame < 0)
                throw new Exception($"The frame index of {filenameWithExtension} appears to be negative (should be 0 or greater).");
            if (frame >= sprite.Textures.Count)
                throw new Exception($"The frame index of {filenameWithExtension} is too large (sprite in the data only has {sprite.Textures.Count} frames).");

            if (frame > 0)
            {
                int prevframe = frame - 1;
                string prevFrameName = $"{spriteName}_{prevframe}.png";
                if (!File.Exists(Paths.JoinVerifyWithinDirectory(importFolder, prevFrameName)))
                    throw new Exception($"{spriteName} is missing image index {prevframe} (failed to find {prevFrameName}).");
            }

            images.Add((file, stripped, spriteName, sprite, frame));
        }

        SetProgressBar(null, "Files", 0, dirFiles.Length);

        bool errored = false;
        foreach ((string filename, string strippedFilename, string spriteName, UndertaleSprite sprite, int frame) in images)
        {
            IncrementProgress();

            try
            {
                using MagickImage image = TextureWorker.ReadBGRAImageFromFile(filename);
                UndertaleTexturePageItem item = sprite.Textures[frame].Texture;
                if ((int)image.Width != item.TargetWidth || (int)image.Height != item.TargetHeight)
                {
                    string error = $"Incorrect dimensions of {strippedFilename}; should be {item.TargetWidth}x{item.TargetHeight}, to fit on the texture page.\n\nStopping early. Some sprites may already be modified.";
                    if ((int)image.Width == sprite.Width && (int)image.Height == sprite.Height)
                    {
                        error = $"{strippedFilename} appears to be exported with padding. The resulting sprite would be too large to fit in the same space on the texture page. " +
                                "Export the sprite without padding, or use ImportGraphics.csx to import sprites of arbitrary dimensions, on new texture pages.\n\nStopping early. Some sprites may already be modified.";
                    }
                    ScriptError(error, "Unexpected texture dimensions");
                    errored = true;
                    return;
                }

                item.ReplaceTexture(image);
            }
            catch
            {
                string error = $"{filename} encountered an unknown error during import. " +
                               "Contact the Underminers discord with as much information as possible, the file, and this error message. Aborting!";
                ScriptError(error, "Sprite Error");
                errored = true;
                return;
            }
        }

        HideProgressBar();
        if (!errored)
            ScriptMessage("Import complete!");
    }

    /// <summary>Imports all tilesets (backgrounds) from a folder, by name.</summary>
    public void ImportAllTilesets()
    {
        EnsureDataLoaded();

        string subPath = PromptChooseDirectory() ?? throw new Exception("The import folder was not set.");

        SetProgressBar(null, "Tilesets", 0, Data.Backgrounds.Count);

        foreach (UndertaleBackground tileset in Data.Backgrounds)
        {
            if (tileset is not null)
            {
                string filename = $"{tileset.Name.Content}.png";
                try
                {
                    string path = Paths.JoinVerifyWithinDirectory(subPath, filename);
                    if (File.Exists(path))
                    {
                        using MagickImage img = TextureWorker.ReadBGRAImageFromFile(path);
                        tileset.Texture.ReplaceTexture(img);
                    }
                }
                catch (Exception ex)
                {
                    ScriptMessage($"Failed to import {filename}: {ex.Message}");
                }
            }

            IncrementProgress();
        }

        HideProgressBar();
        ScriptMessage("Import complete.");
    }

    /// <summary>Imports all embedded textures from a folder named <c>i.png</c>.</summary>
    public void ImportAllEmbeddedTextures()
    {
        EnsureDataLoaded();

        string subPath = PromptChooseDirectory() ?? throw new Exception("The import folder was not set.");

        int i = 0;
        foreach (UndertaleEmbeddedTexture target in Data.EmbeddedTextures)
        {
            if (target is null)
            {
                i++;
                continue;
            }
            string filename = $"{i}.png";
            try
            {
                target.TextureData.Image = GMImage.FromPng(File.ReadAllBytes(Paths.JoinVerifyWithinDirectory(subPath, filename)))
                                                  .ConvertToFormat(target.TextureData.Image.Format);
            }
            catch (Exception ex)
            {
                ScriptMessage($"Failed to import {filename}: {ex.Message}");
            }
            i++;
        }

        ScriptMessage("Import complete.");
    }

    /// <summary>Imports sprite collision masks as PNG files from a folder.</summary>
    public void ImportMasks()
    {
        EnsureDataLoaded();

        string importFolder = PromptChooseDirectory() ?? throw new Exception("The import folder was not set.");

        string[] dirFiles = Directory.GetFiles(importFolder, "*.png");

        // Stop the script if there's missing sprite entries, or invalid data.
        foreach (string file in dirFiles)
        {
            string fileNameWithExtension = Path.GetFileName(file);

            string stripped = Path.GetFileNameWithoutExtension(file);
            int lastUnderscore = stripped.LastIndexOf('_');
            string spriteName = "";
            try
            {
                spriteName = stripped.Substring(0, lastUnderscore);
            }
            catch
            {
                throw new Exception($"Getting the sprite name of {fileNameWithExtension} failed.");
            }

            UndertaleSprite foundSprite = Data.Sprites.ByName(spriteName);
            if (foundSprite is null)
                throw new Exception($"{fileNameWithExtension} could not be imported as the sprite {spriteName} does not exist.");
            (int imgWidth, int imgHeight) = TextureWorker.GetImageSizeFromFile(file);
            (int expectedMaskWidth, int expectedMaskHeight) = foundSprite.CalculateMaskDimensions(Data);
            if (expectedMaskWidth != imgWidth || expectedMaskHeight != imgHeight)
                throw new Exception($"{fileNameWithExtension} is not the proper size to be imported! Please correct this before importing! The proper dimensions are width: {expectedMaskWidth} px, height: {expectedMaskHeight} px.");

            int validFrameNumber;
            try
            {
                validFrameNumber = int.Parse(stripped.Substring(lastUnderscore + 1));
            }
            catch
            {
                throw new Exception($"The index of {fileNameWithExtension} could not be determined.");
            }
            if (validFrameNumber < 0)
                throw new Exception($"{spriteName} is using an invalid numbering scheme. The script has stopped for your own protection.");
            if (validFrameNumber == 0)
                continue;
            string prevFrameName = $"{spriteName}_{validFrameNumber - 1}.png";
            string[] previousFrameFiles = Directory.GetFiles(importFolder, prevFrameName);
            if (previousFrameFiles.Length < 1)
                throw new Exception($"{spriteName} is missing one or more indexes. The detected missing index is: {prevFrameName}");
        }

        SetProgressBar(null, "Files", 0, dirFiles.Length);

        foreach (string file in dirFiles)
        {
            IncrementProgress();

            string stripped = Path.GetFileNameWithoutExtension(file);
            int lastUnderscore = stripped.LastIndexOf('_');
            string spriteName = stripped.Substring(0, lastUnderscore);
            int frame = int.Parse(stripped.Substring(lastUnderscore + 1));
            UndertaleSprite sprite = Data.Sprites.ByName(spriteName);
            int collisionMaskCount = sprite.CollisionMasks.Count;
            if (collisionMaskCount <= frame)
            {
                do
                {
                    sprite.CollisionMasks.Add(sprite.NewMaskEntry(Data));
                    collisionMaskCount++;
                }
                while (collisionMaskCount <= frame);
            }

            (int maskWidth, int maskHeight) = sprite.CalculateMaskDimensions(Data);
            var maskData = TextureWorker.ReadMaskData(file, maskWidth, maskHeight);
            sprite.CollisionMasks[frame].Data = maskData;
            Project?.MarkAssetForExport(sprite);
        }

        HideProgressBar();
        ScriptMessage("Import Complete!");
    }
}