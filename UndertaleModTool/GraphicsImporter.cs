using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ImageMagick;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
using UndertaleModLib.Project;

namespace UndertaleModTool
{
    public enum ImportMode
    {
        ReplaceInPlace = 0,
        NewTexturePages = 1,
        KeepOriginalTexturePage = 2
    }

    public enum SpriteType
    {
        Sprite,
        Background,
        Font,
        Unknown
    }

    public class GraphicsImportSettings
    {
        public string ImportFolder { get; set; }
        public ImportMode Mode { get; set; }
        public bool ImportUnknownAsSprite { get; set; } = true;
        public bool ImportFrameless { get; set; } = true;
        public int OriginPosition { get; set; } = 0;
        public float AnimationSpeed { get; set; } = 1f;
        public int PlaybackType { get; set; } = 1;
        public bool IsSpecialType { get; set; } = false;
        public uint SpecialVersion { get; set; } = 1;
        public int TexturePageSize { get; set; } = 2048;
    }

    public class TextureInfo
    {
        public string Source;
        public int Width;
        public int Height;
        public int TargetX;
        public int TargetY;
        public int BoundingWidth;
        public int BoundingHeight;
        public MagickImage Image;
    }

    public enum SplitType
    {
        Horizontal,
        Vertical,
    }

    public enum BestFitHeuristic
    {
        Area,
        MaxOneAxis,
    }

    public struct PackerRect
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public class TexturePackerNode
    {
        public PackerRect Bounds;
        public TextureInfo Texture;
        public SplitType SplitType;
    }

    public class TexturePackerAtlas
    {
        public int Width;
        public int Height;
        public List<TexturePackerNode> Nodes;
    }

    public class TexturePacker
    {
        public List<TextureInfo> SourceTextures;
        public StringWriter Log;
        public StringWriter Error;
        public int Padding;
        public int AtlasSize;
        public bool DebugMode;
        public BestFitHeuristic FitHeuristic;
        public List<TexturePackerAtlas> Atlasses;
        public HashSet<string> Sources;

        private readonly List<MagickImage> _imagesToCleanup;
        private readonly bool _importAsSprite;

        public TexturePacker(List<MagickImage> imagesToCleanup, bool importAsSprite, int atlasSize)
        {
            SourceTextures = new List<TextureInfo>();
            Log = new StringWriter();
            Error = new StringWriter();
            _imagesToCleanup = imagesToCleanup;
            _importAsSprite = importAsSprite;
            AtlasSize = atlasSize;
        }

        public void Process(string sourceDir, int atlasSize, int padding, bool debugMode)
        {
            Padding = padding;
            AtlasSize = atlasSize;
            DebugMode = debugMode;
            Sources = new HashSet<string>();
            ScanForTextures(sourceDir);
            List<TextureInfo> textures = SourceTextures.ToList();
            Atlasses = new List<TexturePackerAtlas>();
            while (textures.Count > 0)
            {
                TexturePackerAtlas atlas = new TexturePackerAtlas();
                atlas.Width = atlasSize;
                atlas.Height = atlasSize;
                List<TextureInfo> leftovers = LayoutAtlas(textures, atlas);
                if (leftovers.Count == 0)
                {
                    while (leftovers.Count == 0)
                    {
                        atlas.Width /= 2;
                        atlas.Height /= 2;
                        leftovers = LayoutAtlas(textures, atlas);
                    }
                    if (atlas.Width == 0)
                        atlas.Width = 1;
                    else
                        atlas.Width *= 2;
                    if (atlas.Height == 0)
                        atlas.Height = 1;
                    else
                        atlas.Height *= 2;
                    leftovers = LayoutAtlas(textures, atlas);
                }
                Atlasses.Add(atlas);
                textures = leftovers;
            }
        }

        public void SaveAtlasses(string destination)
        {
            int atlasCount = 0;
            string prefix = destination.Replace(Path.GetExtension(destination), "");
            StreamWriter tw = new StreamWriter(destination);
            tw.WriteLine("source_tex, atlas_tex, x, y, width, height");
            foreach (TexturePackerAtlas atlas in Atlasses)
            {
                string atlasName = $"{prefix}{atlasCount:000}.png";
                using (MagickImage img = CreateAtlasImage(atlas))
                    TextureWorker.SaveImageToFile(img, atlasName);
                foreach (TexturePackerNode n in atlas.Nodes)
                {
                    if (n.Texture != null)
                    {
                        tw.Write(n.Texture.Source + ", ");
                        tw.Write(atlasName + ", ");
                        tw.Write((n.Bounds.X).ToString() + ", ");
                        tw.Write((n.Bounds.Y).ToString() + ", ");
                        tw.Write((n.Bounds.Width).ToString() + ", ");
                        tw.WriteLine((n.Bounds.Height).ToString());
                    }
                }
                ++atlasCount;
            }
            tw.Close();
            tw = new StreamWriter(prefix + ".log");
            tw.WriteLine("--- LOG -------------------------------------------");
            tw.WriteLine(Log.ToString());
            tw.WriteLine("--- ERROR -----------------------------------------");
            tw.WriteLine(Error.ToString());
            tw.Close();
        }

        private void ScanForTextures(string path)
        {
            DirectoryInfo di = new DirectoryInfo(path);
            FileInfo[] files = di.GetFiles("*", SearchOption.AllDirectories);
            foreach (FileInfo fi in files)
            {
                SpriteType spriteType = GraphicsImporter.GetSpriteType(fi.FullName);
                string ext = Path.GetExtension(fi.FullName);

                bool isSprite = spriteType == SpriteType.Sprite || (spriteType == SpriteType.Unknown && _importAsSprite);

                if (ext.Equals(".gif", StringComparison.InvariantCultureIgnoreCase))
                {
                    string dirName = Path.GetDirectoryName(fi.FullName);
                    string spriteName = Path.GetFileNameWithoutExtension(fi.FullName);

                    MagickReadSettings settings = new()
                    {
                        ColorSpace = ColorSpace.sRGB,
                    };
                    using MagickImageCollection gif = new(fi.FullName, settings);
                    int frames = gif.Count;
                    if (!isSprite && frames > 1)
                        throw new Exception(fi.FullName + " is a " + spriteType + ", but has more than 1 frame.");

                    for (int i = frames - 1; i >= 0; i--)
                    {
                        AddSource(
                            (MagickImage)gif[i],
                            Path.Join(
                                dirName,
                                isSprite
                                    ? (spriteName + "_" + i + ".png")
                                    : (spriteName + ".png")
                            )
                        );
                        gif.RemoveAt(i);
                    }
                }
                else if (ext.Equals(".png", StringComparison.InvariantCultureIgnoreCase))
                {
                    Match stripMatch = null;
                    if (isSprite)
                    {
                        stripMatch = Regex.Match(Path.GetFileNameWithoutExtension(fi.Name), @"(.*)_strip(\d+)");
                    }
                    if (stripMatch is not null && stripMatch.Success)
                    {
                        string spriteName = stripMatch.Groups[1].Value;
                        string frameCountStr = stripMatch.Groups[2].Value;

                        uint frames;
                        try
                        {
                            frames = UInt32.Parse(frameCountStr);
                        }
                        catch
                        {
                            throw new Exception(fi.FullName + " has an invalid strip numbering scheme.");
                        }
                        if (frames <= 0)
                            throw new Exception(fi.FullName + " has 0 frames.");

                        if (!isSprite && frames > 1)
                            throw new Exception(fi.FullName + " is not a sprite, but has more than 1 frame.");

                        MagickReadSettings settings = new()
                        {
                            ColorSpace = ColorSpace.sRGB,
                        };
                        using MagickImage img = new(fi.FullName, settings);
                        if ((img.Width % frames) > 0)
                            throw new Exception(fi.FullName + " has a width not divisible by the number of frames.");

                        string dirName = Path.GetDirectoryName(fi.FullName);

                        uint frameWidth = (uint)img.Width / frames;
                        uint frameHeight = (uint)img.Height;
                        for (uint i = 0; i < frames; i++)
                        {
                            AddSource(
                                (MagickImage)img.CloneArea((int)(frameWidth * i), 0, frameWidth, frameHeight),
                                Path.Join(dirName,
                                    isSprite
                                        ? (spriteName + "_" + i + ".png")
                                        : (spriteName + ".png")
                                )
                            );
                        }
                    }
                    else
                    {
                        MagickImage img = new(fi.FullName);
                        AddSource(img, fi.FullName);
                    }
                }
            }
        }

        private void AddSource(MagickImage img, string fullName)
        {
            _imagesToCleanup.Add(img);
            if (img.Width <= AtlasSize && img.Height <= AtlasSize)
            {
                TextureInfo ti = new TextureInfo();

                if (!Sources.Add(fullName))
                {
                    throw new Exception(
                        Path.GetFileNameWithoutExtension(fullName) +
                        " as a frame already exists (possibly due to having multiple types of sprite images named the same)."
                    );
                }

                ti.Source = fullName;
                ti.BoundingWidth = (int)img.Width;
                ti.BoundingHeight = (int)img.Height;

                ti.TargetX = 0;
                ti.TargetY = 0;
                if (GraphicsImporter.GetSpriteType(ti.Source) != SpriteType.Background)
                {
                    img.BorderColor = MagickColors.Transparent;
                    img.BackgroundColor = MagickColors.Transparent;
                    img.Border(1);
                    IMagickGeometry bbox = img.BoundingBox;
                    if (bbox is not null)
                    {
                        ti.TargetX = bbox.X - 1;
                        ti.TargetY = bbox.Y - 1;
                        img.Trim();
                    }
                    else
                    {
                        ti.TargetX = 0;
                        ti.TargetY = 0;
                        img.Crop(1, 1);
                    }
                    img.ResetPage();
                }
                ti.Width = (int)img.Width;
                ti.Height = (int)img.Height;
                ti.Image = img;

                SourceTextures.Add(ti);

                Log.WriteLine("Added " + fullName);
            }
            else
            {
                Error.WriteLine(fullName + " is too large to fit in the atlas. Skipping!");
            }
        }

        private void HorizontalSplit(TexturePackerNode toSplit, int width, int height, List<TexturePackerNode> list)
        {
            TexturePackerNode n1 = new TexturePackerNode();
            n1.Bounds.X = toSplit.Bounds.X + width + Padding;
            n1.Bounds.Y = toSplit.Bounds.Y;
            n1.Bounds.Width = toSplit.Bounds.Width - width - Padding;
            n1.Bounds.Height = height;
            n1.SplitType = SplitType.Vertical;
            TexturePackerNode n2 = new TexturePackerNode();
            n2.Bounds.X = toSplit.Bounds.X;
            n2.Bounds.Y = toSplit.Bounds.Y + height + Padding;
            n2.Bounds.Width = toSplit.Bounds.Width;
            n2.Bounds.Height = toSplit.Bounds.Height - height - Padding;
            n2.SplitType = SplitType.Horizontal;
            if (n1.Bounds.Width > 0 && n1.Bounds.Height > 0)
                list.Add(n1);
            if (n2.Bounds.Width > 0 && n2.Bounds.Height > 0)
                list.Add(n2);
        }

        private void VerticalSplit(TexturePackerNode toSplit, int width, int height, List<TexturePackerNode> list)
        {
            TexturePackerNode n1 = new TexturePackerNode();
            n1.Bounds.X = toSplit.Bounds.X + width + Padding;
            n1.Bounds.Y = toSplit.Bounds.Y;
            n1.Bounds.Width = toSplit.Bounds.Width - width - Padding;
            n1.Bounds.Height = toSplit.Bounds.Height;
            n1.SplitType = SplitType.Vertical;
            TexturePackerNode n2 = new TexturePackerNode();
            n2.Bounds.X = toSplit.Bounds.X;
            n2.Bounds.Y = toSplit.Bounds.Y + height + Padding;
            n2.Bounds.Width = width;
            n2.Bounds.Height = toSplit.Bounds.Height - height - Padding;
            n2.SplitType = SplitType.Horizontal;
            if (n1.Bounds.Width > 0 && n1.Bounds.Height > 0)
                list.Add(n1);
            if (n2.Bounds.Width > 0 && n2.Bounds.Height > 0)
                list.Add(n2);
        }

        private TextureInfo FindBestFitForNode(TexturePackerNode node, List<TextureInfo> textures)
        {
            TextureInfo bestFit = null;
            float nodeArea = node.Bounds.Width * node.Bounds.Height;
            float maxCriteria = 0.0f;
            foreach (TextureInfo ti in textures)
            {
                switch (FitHeuristic)
                {
                    case BestFitHeuristic.MaxOneAxis:
                        if (ti.Width <= node.Bounds.Width && ti.Height <= node.Bounds.Height)
                        {
                            float wRatio = (float)ti.Width / (float)node.Bounds.Width;
                            float hRatio = (float)ti.Height / (float)node.Bounds.Height;
                            float ratio = wRatio > hRatio ? wRatio : hRatio;
                            if (ratio > maxCriteria)
                            {
                                maxCriteria = ratio;
                                bestFit = ti;
                            }
                        }
                        break;
                    case BestFitHeuristic.Area:
                        if (ti.Width <= node.Bounds.Width && ti.Height <= node.Bounds.Height)
                        {
                            float textureArea = ti.Width * ti.Height;
                            float coverage = textureArea / nodeArea;
                            if (coverage > maxCriteria)
                            {
                                maxCriteria = coverage;
                                bestFit = ti;
                            }
                        }
                        break;
                }
            }
            return bestFit;
        }

        private List<TextureInfo> LayoutAtlas(List<TextureInfo> textures, TexturePackerAtlas atlas)
        {
            List<TexturePackerNode> freeList = new List<TexturePackerNode>();
            List<TextureInfo> remaining = textures.ToList();
            atlas.Nodes = new List<TexturePackerNode>();
            TexturePackerNode root = new TexturePackerNode();
            root.Bounds.Width = atlas.Width;
            root.Bounds.Height = atlas.Height;
            root.SplitType = SplitType.Horizontal;
            freeList.Add(root);
            while (freeList.Count > 0 && remaining.Count > 0)
            {
                TexturePackerNode node = freeList[0];
                freeList.RemoveAt(0);
                TextureInfo bestFit = FindBestFitForNode(node, remaining);
                if (bestFit != null)
                {
                    if (node.SplitType == SplitType.Horizontal)
                        HorizontalSplit(node, bestFit.Width, bestFit.Height, freeList);
                    else
                        VerticalSplit(node, bestFit.Width, bestFit.Height, freeList);
                    node.Texture = bestFit;
                    node.Bounds.Width = bestFit.Width;
                    node.Bounds.Height = bestFit.Height;
                    remaining.Remove(bestFit);
                }
                atlas.Nodes.Add(node);
            }
            return remaining;
        }

        private MagickImage CreateAtlasImage(TexturePackerAtlas atlas)
        {
            MagickImage img = new(MagickColors.Transparent, (uint)atlas.Width, (uint)atlas.Height);
            foreach (TexturePackerNode n in atlas.Nodes)
            {
                if (n.Texture is not null)
                {
                    using IMagickImage<byte> resizedSourceImg = TextureWorker.ResizeImage(n.Texture.Image, n.Bounds.Width, n.Bounds.Height);
                    img.Composite(resizedSourceImg, n.Bounds.X, n.Bounds.Y, CompositeOperator.Copy);
                }
            }
            return img;
        }
    }

    public static class GraphicsImporter
    {
        private static readonly string[] Offsets = {
            "Top Left", "Top Center", "Top Right",
            "Center Left", "Center", "Center Right",
            "Bottom Left", "Bottom Center", "Bottom Right"
        };

        private static readonly Regex SprFrameRegex = new(@"^(.+?)(?:_(\d+))$", RegexOptions.Compiled);

        public static async Task ImportAsync(UndertaleData data, GraphicsImportSettings settings,
            IProgress<(int current, int total, string message)> progress, ProjectContext project)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrEmpty(settings.ImportFolder) || !Directory.Exists(settings.ImportFolder))
                throw new ArgumentException("Import folder does not exist.");

            switch (settings.Mode)
            {
                case ImportMode.ReplaceInPlace:
                    await ImportReplaceInPlace(data, settings, progress);
                    break;
                case ImportMode.NewTexturePages:
                    await ImportNewTexturePages(data, settings, progress, project);
                    break;
                case ImportMode.KeepOriginalTexturePage:
                    await ImportPreservePositions(data, settings, progress, project);
                    break;
            }
        }

        private static async Task ImportReplaceInPlace(UndertaleData data, GraphicsImportSettings settings,
            IProgress<(int current, int total, string message)> progress)
        {
            string importFolder = settings.ImportFolder;
            string[] dirFiles = Directory.GetFiles(importFolder);
            var images = new List<(string filename, string strippedFilename, string spriteName, UndertaleSprite sprite, int frame)>();

            await Task.Run(() =>
            {
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

                    UndertaleSprite sprite = data.Sprites.ByName(spriteName);
                    if (sprite is null)
                        throw new Exception($"{filenameWithExtension} could not be imported, as the sprite \"{spriteName}\" does not exist.");

                    if (!int.TryParse(stripped.Substring(lastUnderscore + 1), out int frame))
                        throw new Exception($"The frame index of {filenameWithExtension} could not be determined (should be an integer).");
                    if (frame < 0)
                        throw new Exception($"The frame index of {filenameWithExtension} appears to be negative.");
                    if (frame >= sprite.Textures.Count)
                        throw new Exception($"The frame index of {filenameWithExtension} is too large (sprite only has {sprite.Textures.Count} frames).");

                    if (frame > 0)
                    {
                        int prevframe = frame - 1;
                        string prevFrameName = $"{spriteName}_{prevframe}.png";
                        if (!File.Exists(Path.Join(importFolder, prevFrameName)))
                            throw new Exception($"{spriteName} is missing image index {prevframe}.");
                    }

                    images.Add((file, stripped, spriteName, sprite, frame));
                }
            });

            int total = images.Count;
            var dispatcher = Application.Current?.Dispatcher;
            var replaceActions = new List<Action>();

            await Task.Run(() =>
            {
                for (int i = 0; i < images.Count; i++)
                {
                    var (filename, strippedFilename, spriteName, sprite, frame) = images[i];
                    progress?.Report((i + 1, total, $"Preparing {strippedFilename}..."));

                    try
                    {
                        MagickImage image = TextureWorker.ReadBGRAImageFromFile(filename);
                        UndertaleTexturePageItem item = sprite.Textures[frame].Texture;
                        if ((int)image.Width != item.TargetWidth || (int)image.Height != item.TargetHeight)
                        {
                            image.Dispose();
                            string error = $"Incorrect dimensions of {strippedFilename}; should be {item.TargetWidth}x{item.TargetHeight}." +
                                           "\nStopping early. Some sprites may already be modified.";
                            if ((int)image.Width == sprite.Width && (int)image.Height == sprite.Height)
                            {
                                error = $"{strippedFilename} appears to be exported with padding. The resulting sprite would be too large to fit in the same space on the texture page. " +
                                        "Export the sprite without padding, or use \"New texture pages\" mode to import sprites of arbitrary dimensions.";
                            }
                            throw new Exception(error);
                        }

                        var capturedItem = item;
                        replaceActions.Add(() =>
                        {
                            capturedItem.ReplaceTexture(image);
                            image.Dispose();
                        });
                    }
                    catch (Exception ex) when (ex.Message.Contains("Incorrect dimensions") || ex.Message.Contains("exported with padding"))
                    {
                        throw;
                    }
                    catch
                    {
                        throw new Exception($"{filename} encountered an unknown error during import.");
                    }
                }
            });

            if (dispatcher != null)
            {
                await dispatcher.InvokeAsync(() =>
                {
                    foreach (var action in replaceActions)
                        action();
                });
            }
            else
            {
                foreach (var action in replaceActions)
                    action();
            }
        }

        private static async Task ImportNewTexturePages(UndertaleData data, GraphicsImportSettings settings,
            IProgress<(int current, int total, string message)> progress, ProjectContext project)
        {
            var dispatcher = Application.Current?.Dispatcher;

            string importFolder = settings.ImportFolder;
            string packDir = Path.Join(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "Packager");
            Directory.CreateDirectory(packDir);

            List<MagickImage> imagesToCleanup = new();

            try
            {
                string sourcePath = importFolder;
                string outName = Path.Join(packDir, "atlas.txt");
                int textureSize = settings.TexturePageSize;
                int paddingValue = 2;

                TexturePacker packer = new TexturePacker(imagesToCleanup, settings.ImportUnknownAsSprite, textureSize);
                packer.Process(sourcePath, textureSize, paddingValue, false);
                packer.SaveAtlasses(outName);

                bool noMasksForBasicRectangles = data.IsVersionAtLeast(2022, 9);
                bool bboxMasks = data.IsVersionAtLeast(2024, 6);
                HashSet<string> spritesStartAt1 = await Task.Run(() =>
                    ValidateAndGetSpriteStartIndices(data, importFolder, settings.ImportUnknownAsSprite));

                string prefix = Path.Join(Path.GetDirectoryName(outName), Path.GetFileNameWithoutExtension(outName));
                int totalNodes = packer.Atlasses.Sum(a => a.Nodes.Count);
                int processedNodes = 0;
                int embeddedTextureCount = data.EmbeddedTextures.Count;
                int texturePageItemCount = data.TexturePageItems.Count;

                foreach (TexturePackerAtlas atlas in packer.Atlasses)
                {
                    string atlasName = $"{prefix}{packer.Atlasses.IndexOf(atlas):000}.png";
                    using MagickImage atlasImage = TextureWorker.ReadBGRAImageFromFile(atlasName);
                    IPixelCollection<byte> atlasPixels = atlasImage.GetPixels();

                    int currentTextureIndex = embeddedTextureCount;
                    embeddedTextureCount++;

                    UndertaleEmbeddedTexture texture = new UndertaleEmbeddedTexture();
                    texture.Name = new UndertaleString($"Texture {currentTextureIndex}");
                    texture.TextureData.Image = GMImage.FromMagickImage(atlasImage).ConvertToPng();

                    Dictionary<UndertaleSprite, TexturePackerNode> maskNodes = new();

                    List<Action> dataModifications = new();

                    dataModifications.Add(() => data.EmbeddedTextures.Add(texture));

                    foreach (TexturePackerNode n in atlas.Nodes)
                    {
                        if (n.Texture != null)
                        {
                            processedNodes++;
                            progress?.Report((processedNodes, totalNodes, $"Processing texture page item {processedNodes}/{totalNodes}..."));

                            int currentPageItemCount = texturePageItemCount;
                            texturePageItemCount++;

                            UndertaleTexturePageItem texturePageItem = new UndertaleTexturePageItem();
                            texturePageItem.Name = new UndertaleString("PageItem " + currentPageItemCount);
                            texturePageItem.SourceX = (ushort)n.Bounds.X;
                            texturePageItem.SourceY = (ushort)n.Bounds.Y;
                            texturePageItem.SourceWidth = (ushort)n.Bounds.Width;
                            texturePageItem.SourceHeight = (ushort)n.Bounds.Height;
                            texturePageItem.TargetX = (ushort)n.Texture.TargetX;
                            texturePageItem.TargetY = (ushort)n.Texture.TargetY;
                            texturePageItem.TargetWidth = (ushort)n.Bounds.Width;
                            texturePageItem.TargetHeight = (ushort)n.Bounds.Height;
                            texturePageItem.BoundingWidth = (ushort)n.Texture.BoundingWidth;
                            texturePageItem.BoundingHeight = (ushort)n.Texture.BoundingHeight;
                            texturePageItem.TexturePage = texture;

                            dataModifications.Add(() => data.TexturePageItems.Add(texturePageItem));

                            string stripped = Path.GetFileNameWithoutExtension(n.Texture.Source);
                            SpriteType spriteType = GetSpriteType(n.Texture.Source);

                            if (settings.ImportUnknownAsSprite && (spriteType == SpriteType.Unknown || spriteType == SpriteType.Font))
                                spriteType = SpriteType.Sprite;

                            if (spriteType == SpriteType.Background)
                            {
                                ImportBackgroundDeferred(data, stripped, texturePageItem, project, dataModifications);
                            }
                            else if (spriteType == SpriteType.Sprite)
                            {
                                ImportSpriteDeferred(data, stripped, texturePageItem, n, settings, maskNodes, spritesStartAt1, project, dataModifications);
                            }
                        }
                    }

                    if (dispatcher != null)
                    {
                        await dispatcher.InvokeAsync(() =>
                        {
                            foreach (var action in dataModifications)
                                action();
                        });

                        await dispatcher.InvokeAsync(() =>
                        {
                            UpdateCollisionMasks(data, maskNodes, atlasPixels, bboxMasks);
                        });
                    }
                    else
                    {
                        foreach (var action in dataModifications)
                            action();

                        UpdateCollisionMasks(data, maskNodes, atlasPixels, bboxMasks);
                    }

                    maskNodes.Clear();
                }
            }
            finally
            {
                foreach (MagickImage img in imagesToCleanup)
                    img.Dispose();

                if (Directory.Exists(packDir))
                {
                    try { Directory.Delete(packDir, true); } catch { }
                }
            }
        }

        private static async Task ImportPreservePositions(UndertaleData data, GraphicsImportSettings settings,
            IProgress<(int current, int total, string message)> progress, ProjectContext project)
        {
            string importFolder = settings.ImportFolder;
            string[] dirFiles = Directory.GetFiles(importFolder, "*.png", SearchOption.AllDirectories);

            var inPlaceImages = new List<(string filename, string strippedFilename, string spriteName, UndertaleSprite sprite, int frame)>();
            var newPageFiles = new List<string>();

            await Task.Run(() =>
            {
                foreach (string file in dirFiles)
                {
                    string filenameWithExtension = Path.GetFileName(file);
                    string stripped = Path.GetFileNameWithoutExtension(file);

                    SpriteType spriteType = GetSpriteType(file);
                    if (settings.ImportUnknownAsSprite && (spriteType == SpriteType.Unknown || spriteType == SpriteType.Font))
                        spriteType = SpriteType.Sprite;

                    if (spriteType == SpriteType.Background)
                    {
                        newPageFiles.Add(file);
                        continue;
                    }

                    if (spriteType != SpriteType.Sprite)
                        continue;

                    int lastUnderscore = stripped.LastIndexOf('_');
                    if (lastUnderscore < 0)
                    {
                        newPageFiles.Add(file);
                        continue;
                    }

                    string spriteName;
                    try
                    {
                        spriteName = stripped.Substring(0, lastUnderscore);
                    }
                    catch
                    {
                        newPageFiles.Add(file);
                        continue;
                    }

                    if (!int.TryParse(stripped.Substring(lastUnderscore + 1), out int frame))
                    {
                        newPageFiles.Add(file);
                        continue;
                    }

                    UndertaleSprite sprite = data.Sprites.ByName(spriteName);
                    if (sprite is null || frame < 0 || frame >= sprite.Textures.Count)
                    {
                        newPageFiles.Add(file);
                        continue;
                    }

                    try
                    {
                        using MagickImage image = TextureWorker.ReadBGRAImageFromFile(file);
                        UndertaleTexturePageItem item = sprite.Textures[frame].Texture;
                        if (item != null && (int)image.Width == item.TargetWidth && (int)image.Height == item.TargetHeight)
                        {
                            inPlaceImages.Add((file, stripped, spriteName, sprite, frame));
                        }
                        else
                        {
                            newPageFiles.Add(file);
                        }
                    }
                    catch
                    {
                        newPageFiles.Add(file);
                    }
                }
            });

            int totalInPlace = inPlaceImages.Count;
            var dispatcher = Application.Current?.Dispatcher;
            var inPlaceActions = new List<Action>();

            await Task.Run(() =>
            {
                for (int i = 0; i < inPlaceImages.Count; i++)
                {
                    var (filename, strippedFilename, spriteName, sprite, frame) = inPlaceImages[i];
                    progress?.Report((i + 1, totalInPlace + newPageFiles.Count, $"Preparing: {strippedFilename}..."));

                    MagickImage image = TextureWorker.ReadBGRAImageFromFile(filename);
                    UndertaleTexturePageItem item = sprite.Textures[frame].Texture;
                    var capturedItem = item;
                    inPlaceActions.Add(() =>
                    {
                        capturedItem.ReplaceTexture(image);
                        image.Dispose();
                    });
                }
            });

            if (dispatcher != null)
            {
                await dispatcher.InvokeAsync(() =>
                {
                    foreach (var action in inPlaceActions)
                        action();
                });
            }
            else
            {
                foreach (var action in inPlaceActions)
                    action();
            }

            if (newPageFiles.Count > 0)
            {
                string tempDir = Path.Join(Path.GetTempPath(), $"UMT_Import_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);
                try
                {
                    foreach (string file in newPageFiles)
                    {
                        string dest = Path.Join(tempDir, Path.GetFileName(file));
                        File.Copy(file, dest, true);
                    }

                    var newPageSettings = new GraphicsImportSettings
                    {
                        ImportFolder = tempDir,
                        Mode = ImportMode.NewTexturePages,
                        ImportUnknownAsSprite = settings.ImportUnknownAsSprite,
                        ImportFrameless = settings.ImportFrameless,
                        OriginPosition = settings.OriginPosition,
                        AnimationSpeed = settings.AnimationSpeed,
                        PlaybackType = settings.PlaybackType,
                        IsSpecialType = settings.IsSpecialType,
                        SpecialVersion = settings.SpecialVersion,
                        TexturePageSize = settings.TexturePageSize
                    };

                    await ImportNewTexturePages(data, newPageSettings, progress, project);
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        public static SpriteType GetSpriteType(string path)
        {
            string folderPath = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(folderPath))
                return SpriteType.Unknown;
            string folderName = new DirectoryInfo(folderPath).Name;
            string lowerName = folderName.ToLower();

            if (lowerName == "backgrounds" || lowerName == "background")
                return SpriteType.Background;
            if (lowerName == "fonts" || lowerName == "font")
                return SpriteType.Font;
            if (lowerName == "sprites" || lowerName == "sprite")
                return SpriteType.Sprite;
            return SpriteType.Unknown;
        }

        private static void ImportBackground(UndertaleData data, string stripped, UndertaleTexturePageItem texturePageItem, ProjectContext project)
        {
            UndertaleBackground background = data.Backgrounds.ByName(stripped);
            if (background is not null)
            {
                background.Texture = texturePageItem;
            }
            else
            {
                UndertaleString backgroundUTString = data.Strings.MakeString(stripped);
                background = new UndertaleBackground();
                background.Name = backgroundUTString;
                background.Transparent = false;
                background.Preload = false;
                background.Texture = texturePageItem;
                data.Backgrounds.Add(background);
            }
            project?.MarkAssetForExport(background);
        }

        private static void ImportBackgroundDeferred(UndertaleData data, string stripped, UndertaleTexturePageItem texturePageItem, ProjectContext project, List<Action> modifications)
        {
            UndertaleBackground background = data.Backgrounds.ByName(stripped);
            if (background is not null)
            {
                var bg = background;
                modifications.Add(() => bg.Texture = texturePageItem);
                project?.MarkAssetForExport(bg);
            }
            else
            {
                UndertaleBackground newBg = new UndertaleBackground();
                newBg.Transparent = false;
                newBg.Preload = false;
                newBg.Texture = texturePageItem;
                modifications.Add(() =>
                {
                    newBg.Name = data.Strings.MakeString(stripped);
                    data.Backgrounds.Add(newBg);
                });
                project?.MarkAssetForExport(newBg);
            }
        }

        private static void ImportSprite(UndertaleData data, string stripped, UndertaleTexturePageItem texturePageItem,
            TexturePackerNode n, GraphicsImportSettings settings, Dictionary<UndertaleSprite, TexturePackerNode> maskNodes,
            HashSet<string> spritesStartAt1, ProjectContext project)
        {
            string spriteName;
            int frame;

            int lastUnderscore = stripped.LastIndexOf('_');
            try
            {
                Int32.Parse(stripped.Substring(lastUnderscore + 1));
                spriteName = stripped.Substring(0, lastUnderscore);
                frame = Int32.Parse(stripped.Substring(lastUnderscore + 1));
            }
            catch
            {
                if (!settings.ImportFrameless)
                    return;
                spriteName = stripped;
                frame = 0;
            }

            if (spritesStartAt1.Contains(spriteName))
                frame--;

            UndertaleSprite.TextureEntry texentry = new UndertaleSprite.TextureEntry();
            texentry.Texture = texturePageItem;

            UndertaleSprite sprite = data.Sprites.ByName(spriteName);
            if (sprite is null)
            {
                UndertaleString spriteUTString = data.Strings.MakeString(spriteName);
                UndertaleSprite newSprite = new UndertaleSprite();
                newSprite.Name = spriteUTString;
                newSprite.Width = (uint)n.Texture.BoundingWidth;
                newSprite.Height = (uint)n.Texture.BoundingHeight;
                newSprite.MarginLeft = n.Texture.TargetX;
                newSprite.MarginRight = n.Texture.TargetX + n.Bounds.Width - 1;
                newSprite.MarginTop = n.Texture.TargetY;
                newSprite.MarginBottom = n.Texture.TargetY + n.Bounds.Height - 1;
                newSprite.GMS2PlaybackSpeedType = (AnimSpeedType)settings.PlaybackType;
                newSprite.GMS2PlaybackSpeed = settings.AnimationSpeed;
                newSprite.IsSpecialType = settings.IsSpecialType;
                newSprite.SVersion = settings.SpecialVersion;
                SetOrigin(newSprite, settings.OriginPosition);
                if (frame > 0)
                {
                    for (int i = 0; i < frame; i++)
                        newSprite.Textures.Add(null);
                }

                bool noMasksForBasicRectangles = data.IsVersionAtLeast(2022, 9);
                if (!noMasksForBasicRectangles ||
                    newSprite.SepMasks is not (UndertaleSprite.SepMaskType.AxisAlignedRect or UndertaleSprite.SepMaskType.RotatedRect))
                {
                    maskNodes.Add(newSprite, n);
                }

                newSprite.Textures.Add(texentry);
                data.Sprites.Add(newSprite);
                project?.MarkAssetForExport(newSprite);
                return;
            }

            project?.MarkAssetForExport(sprite);

            if (frame > sprite.Textures.Count - 1)
            {
                while (frame > sprite.Textures.Count - 1)
                {
                    sprite.Textures.Add(texentry);
                }
                return;
            }

            sprite.Textures[frame] = texentry;
            sprite.GMS2PlaybackSpeedType = (AnimSpeedType)settings.PlaybackType;
            sprite.GMS2PlaybackSpeed = settings.AnimationSpeed;
            sprite.IsSpecialType = settings.IsSpecialType;
            sprite.SVersion = settings.SpecialVersion;

            uint oldWidth = sprite.Width, oldHeight = sprite.Height;
            sprite.Width = (uint)n.Texture.BoundingWidth;
            sprite.Height = (uint)n.Texture.BoundingHeight;
            bool changedSpriteDimensions = (oldWidth != sprite.Width || oldHeight != sprite.Height);

            SetOrigin(sprite, settings.OriginPosition);

            bool grewBoundingBox = false;
            bool fullImageBbox = sprite.BBoxMode == 1;
            bool manualBbox = sprite.BBoxMode == 2;
            if (!manualBbox)
            {
                int marginLeft = fullImageBbox ? 0 : n.Texture.TargetX;
                int marginRight = fullImageBbox ? ((int)sprite.Width - 1) : (n.Texture.TargetX + n.Bounds.Width - 1);
                int marginTop = fullImageBbox ? 0 : n.Texture.TargetY;
                int marginBottom = fullImageBbox ? ((int)sprite.Height - 1) : (n.Texture.TargetY + n.Bounds.Height - 1);
                if (marginLeft < sprite.MarginLeft)
                {
                    sprite.MarginLeft = marginLeft;
                    grewBoundingBox = true;
                }
                if (marginTop < sprite.MarginTop)
                {
                    sprite.MarginTop = marginTop;
                    grewBoundingBox = true;
                }
                if (marginRight > sprite.MarginRight)
                {
                    sprite.MarginRight = marginRight;
                    grewBoundingBox = true;
                }
                if (marginBottom > sprite.MarginBottom)
                {
                    sprite.MarginBottom = marginBottom;
                    grewBoundingBox = true;
                }
            }

            bool noMasksForBasicRectangles2 = data.IsVersionAtLeast(2022, 9);
            bool bboxMasks = data.IsVersionAtLeast(2024, 6);
            if (!noMasksForBasicRectangles2 ||
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

        private static void ImportSpriteDeferred(UndertaleData data, string stripped, UndertaleTexturePageItem texturePageItem,
            TexturePackerNode n, GraphicsImportSettings settings, Dictionary<UndertaleSprite, TexturePackerNode> maskNodes,
            HashSet<string> spritesStartAt1, ProjectContext project, List<Action> modifications)
        {
            string spriteName;
            int frame;

            int lastUnderscore = stripped.LastIndexOf('_');
            try
            {
                Int32.Parse(stripped.Substring(lastUnderscore + 1));
                spriteName = stripped.Substring(0, lastUnderscore);
                frame = Int32.Parse(stripped.Substring(lastUnderscore + 1));
            }
            catch
            {
                if (!settings.ImportFrameless)
                    return;
                spriteName = stripped;
                frame = 0;
            }

            if (spritesStartAt1.Contains(spriteName))
                frame--;

            UndertaleSprite.TextureEntry texentry = new UndertaleSprite.TextureEntry();
            texentry.Texture = texturePageItem;

            UndertaleSprite sprite = data.Sprites.ByName(spriteName);
            if (sprite is null)
            {
                UndertaleSprite newSprite = new UndertaleSprite();
                newSprite.Width = (uint)n.Texture.BoundingWidth;
                newSprite.Height = (uint)n.Texture.BoundingHeight;
                newSprite.MarginLeft = n.Texture.TargetX;
                newSprite.MarginRight = n.Texture.TargetX + n.Bounds.Width - 1;
                newSprite.MarginTop = n.Texture.TargetY;
                newSprite.MarginBottom = n.Texture.TargetY + n.Bounds.Height - 1;
                newSprite.GMS2PlaybackSpeedType = (AnimSpeedType)settings.PlaybackType;
                newSprite.GMS2PlaybackSpeed = settings.AnimationSpeed;
                newSprite.IsSpecialType = settings.IsSpecialType;
                newSprite.SVersion = settings.SpecialVersion;
                SetOrigin(newSprite, settings.OriginPosition);

                var nullEntries = new List<UndertaleSprite.TextureEntry>();
                if (frame > 0)
                {
                    for (int i = 0; i < frame; i++)
                        nullEntries.Add(null);
                }

                bool noMasksForBasicRectangles = data.IsVersionAtLeast(2022, 9);
                if (!noMasksForBasicRectangles ||
                    newSprite.SepMasks is not (UndertaleSprite.SepMaskType.AxisAlignedRect or UndertaleSprite.SepMaskType.RotatedRect))
                {
                    maskNodes.Add(newSprite, n);
                }

                modifications.Add(() =>
                {
                    newSprite.Name = data.Strings.MakeString(spriteName);
                    foreach (var nullEntry in nullEntries)
                        newSprite.Textures.Add(nullEntry);
                    newSprite.Textures.Add(texentry);
                    data.Sprites.Add(newSprite);
                });
                project?.MarkAssetForExport(newSprite);
                return;
            }

            project?.MarkAssetForExport(sprite);

            if (frame > sprite.Textures.Count - 1)
            {
                var extraEntries = new List<UndertaleSprite.TextureEntry>();
                while (frame > sprite.Textures.Count - 1)
                {
                    extraEntries.Add(texentry);
                }
                modifications.Add(() =>
                {
                    foreach (var extraEntry in extraEntries)
                        sprite.Textures.Add(extraEntry);
                });
                return;
            }

            modifications.Add(() =>
            {
                sprite.Textures[frame] = texentry;
                sprite.GMS2PlaybackSpeedType = (AnimSpeedType)settings.PlaybackType;
                sprite.GMS2PlaybackSpeed = settings.AnimationSpeed;
                sprite.IsSpecialType = settings.IsSpecialType;
                sprite.SVersion = settings.SpecialVersion;

                uint oldWidth = sprite.Width, oldHeight = sprite.Height;
                sprite.Width = (uint)n.Texture.BoundingWidth;
                sprite.Height = (uint)n.Texture.BoundingHeight;
                bool changedSpriteDimensions = (oldWidth != sprite.Width || oldHeight != sprite.Height);

                SetOrigin(sprite, settings.OriginPosition);

                bool grewBoundingBox = false;
                bool fullImageBbox = sprite.BBoxMode == 1;
                bool manualBbox = sprite.BBoxMode == 2;
                if (!manualBbox)
                {
                    int marginLeft = fullImageBbox ? 0 : n.Texture.TargetX;
                    int marginRight = fullImageBbox ? ((int)sprite.Width - 1) : (n.Texture.TargetX + n.Bounds.Width - 1);
                    int marginTop = fullImageBbox ? 0 : n.Texture.TargetY;
                    int marginBottom = fullImageBbox ? ((int)sprite.Height - 1) : (n.Texture.TargetY + n.Bounds.Height - 1);
                    if (marginLeft < sprite.MarginLeft)
                    {
                        sprite.MarginLeft = marginLeft;
                        grewBoundingBox = true;
                    }
                    if (marginTop < sprite.MarginTop)
                    {
                        sprite.MarginTop = marginTop;
                        grewBoundingBox = true;
                    }
                    if (marginRight > sprite.MarginRight)
                    {
                        sprite.MarginRight = marginRight;
                        grewBoundingBox = true;
                    }
                    if (marginBottom > sprite.MarginBottom)
                    {
                        sprite.MarginBottom = marginBottom;
                        grewBoundingBox = true;
                    }
                }

                bool noMasksForBasicRectangles2 = data.IsVersionAtLeast(2022, 9);
                bool bboxMasks = data.IsVersionAtLeast(2024, 6);
                if (!noMasksForBasicRectangles2 ||
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
            });
        }

        private static void SetOrigin(UndertaleSprite sprite, int originIndex)
        {
            switch (originIndex)
            {
                case 0:
                    sprite.OriginX = 0;
                    sprite.OriginY = 0;
                    break;
                case 1:
                    sprite.OriginX = (int)(sprite.Width / 2);
                    sprite.OriginY = 0;
                    break;
                case 2:
                    sprite.OriginX = (int)sprite.Width;
                    sprite.OriginY = 0;
                    break;
                case 3:
                    sprite.OriginX = 0;
                    sprite.OriginY = (int)(sprite.Height / 2);
                    break;
                case 4:
                    sprite.OriginX = (int)(sprite.Width / 2);
                    sprite.OriginY = (int)(sprite.Height / 2);
                    break;
                case 5:
                    sprite.OriginX = (int)sprite.Width;
                    sprite.OriginY = (int)(sprite.Height / 2);
                    break;
                case 6:
                    sprite.OriginX = 0;
                    sprite.OriginY = (int)sprite.Height;
                    break;
                case 7:
                    sprite.OriginX = (int)(sprite.Width / 2);
                    sprite.OriginY = (int)sprite.Height;
                    break;
                case 8:
                    sprite.OriginX = (int)sprite.Width;
                    sprite.OriginY = (int)sprite.Height;
                    break;
            }
        }

        private static void UpdateCollisionMasks(UndertaleData data, Dictionary<UndertaleSprite, TexturePackerNode> maskNodes,
            IPixelCollection<byte> atlasPixels, bool bboxMasks)
        {
            foreach ((UndertaleSprite maskSpr, TexturePackerNode maskNode) in maskNodes)
            {
                maskSpr.CollisionMasks.Clear();
                maskSpr.CollisionMasks.Add(maskSpr.NewMaskEntry(data));
                (int maskWidth, int maskHeight) = maskSpr.CalculateMaskDimensions(data);
                int maskStride = ((maskWidth + 7) / 8) * 8;

                BitArray maskingBitArray = new BitArray(maskStride * maskHeight);
                for (int y = 0; y < maskHeight && y < maskNode.Bounds.Height; y++)
                {
                    for (int x = 0; x < maskWidth && x < maskNode.Bounds.Width; x++)
                    {
                        IMagickColor<byte> pixelColor = atlasPixels.GetPixel(x + maskNode.Bounds.X, y + maskNode.Bounds.Y).ToColor();
                        if (bboxMasks)
                        {
                            maskingBitArray[(y * maskStride) + x] = (pixelColor.A > 0);
                        }
                        else
                        {
                            maskingBitArray[((y + maskNode.Texture.TargetY) * maskStride) + x + maskNode.Texture.TargetX] = (pixelColor.A > 0);
                        }
                    }
                }
                BitArray tempBitArray = new BitArray(maskingBitArray.Length);
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
        }

        private static HashSet<string> ValidateAndGetSpriteStartIndices(UndertaleData data, string importFolder, bool importUnknownAsSprite)
        {
            HashSet<string> spritesStartAt1 = new HashSet<string>();
            string[] dirFiles = Directory.GetFiles(importFolder, "*.png", SearchOption.AllDirectories);

            foreach (string file in dirFiles)
            {
                string filenameWithExtension = Path.GetFileName(file);
                string stripped = Path.GetFileNameWithoutExtension(file);

                SpriteType spriteType = GetSpriteType(file);
                if (importUnknownAsSprite && (spriteType == SpriteType.Unknown || spriteType == SpriteType.Font))
                    spriteType = SpriteType.Sprite;

                if (spriteType != SpriteType.Sprite)
                    continue;

                Match stripMatch = Regex.Match(stripped, @"(.*)_strip(\d+)");
                if (stripMatch.Success)
                {
                    int frames;
                    try
                    {
                        frames = Int32.Parse(stripMatch.Groups[2].Value);
                    }
                    catch
                    {
                        throw new Exception(filenameWithExtension + " has an invalid strip numbering scheme.");
                    }
                    if (frames <= 0)
                        throw new Exception(filenameWithExtension + " has 0 frames.");
                    continue;
                }

                string[] dupFiles = Directory.GetFiles(importFolder, filenameWithExtension, SearchOption.AllDirectories);
                if (dupFiles.Length > 1)
                    throw new Exception("Duplicate file detected. There are " + dupFiles.Length + " files named: " + filenameWithExtension);

                int lastUnderscore = stripped.LastIndexOf('_');
                if (lastUnderscore < 0)
                    continue;

                string spriteName;
                int frame;
                try
                {
                    spriteName = stripped.Substring(0, lastUnderscore);
                    frame = Int32.Parse(stripped.Substring(lastUnderscore + 1));
                }
                catch
                {
                    continue;
                }

                if (frame < 0)
                    throw new Exception(spriteName + " is using an invalid numbering scheme.");

                if (frame > 0)
                {
                    int prevframe = frame - 1;
                    string prevFrameName = spriteName + "_" + prevframe.ToString() + ".png";
                    string[] previousFrameFiles = Directory.GetFiles(importFolder, prevFrameName, SearchOption.AllDirectories);
                    if (previousFrameFiles.Length < 1)
                    {
                        if (frame == 1)
                        {
                            spritesStartAt1.Add(spriteName);
                        }
                        else
                        {
                            throw new Exception(spriteName + " is missing one or more indexes. The detected missing index is: " + prevFrameName);
                        }
                    }
                }
            }

            return spritesStartAt1;
        }

        public static async Task ImportFromEntriesAsync(UndertaleData data, List<ImportImageEntry> entries,
            int texturePageSize, IProgress<(int current, int total, string message)> progress, ProjectContext project)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (entries == null || entries.Count == 0) throw new ArgumentException("No entries to import.");

            foreach (var entry in entries)
            {
                if (!entry.AvailableModes.Contains(entry.ImportMode))
                {
                    if (entry.AvailableModes.Contains(ImportMode.KeepOriginalTexturePage))
                        entry.ImportMode = ImportMode.KeepOriginalTexturePage;
                    else
                        entry.ImportMode = ImportMode.NewTexturePages;
                }
            }

            var replaceEntries = entries.Where(e => e.ImportMode == ImportMode.ReplaceInPlace).ToList();
            var newPageEntries = entries.Where(e => e.ImportMode == ImportMode.NewTexturePages).ToList();
            var preserveEntries = entries.Where(e => e.ImportMode == ImportMode.KeepOriginalTexturePage).ToList();

            if (replaceEntries.Count > 0)
            {
                await ImportReplaceInPlaceFromEntries(data, replaceEntries, progress);
            }

            if (newPageEntries.Count > 0)
            {
                await ImportNewTexturePagesFromEntries(data, newPageEntries, texturePageSize, progress, project);
            }

            if (preserveEntries.Count > 0)
            {
                await ImportKeepOriginalTexturePageFromEntries(data, preserveEntries, texturePageSize, progress, project);
            }
        }

        private static async Task ImportReplaceInPlaceFromEntries(UndertaleData data, List<ImportImageEntry> entries,
            IProgress<(int current, int total, string message)> progress)
        {
            var dispatcher = Application.Current?.Dispatcher;
            int total = entries.Count;

            var actions = new List<Action>();

            await Task.Run(() =>
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    progress?.Report((i + 1, total, $"Preparing: {entry.FileName}..."));

                    if (entry.ImageType == SpriteType.Background)
                    {
                        UndertaleBackground bg = data.Backgrounds.ByName(entry.SpriteName);
                        if (bg is not null && bg.Texture is not null)
                        {
                            MagickImage bgImage = TextureWorker.ReadBGRAImageFromFile(entry.FilePath);
                            var bgItem = bg.Texture;
                            actions.Add(() =>
                            {
                                bgItem.ReplaceTexture(bgImage);
                                bgImage.Dispose();
                            });
                        }
                        continue;
                    }

                    UndertaleSprite sprite = data.Sprites.ByName(entry.SpriteName);
                    if (sprite is null || entry.FrameIndex < 0 || entry.FrameIndex >= sprite.Textures.Count)
                        continue;

                    UndertaleTexturePageItem item = sprite.Textures[entry.FrameIndex].Texture;
                    if (item is null)
                        continue;

                    MagickImage spriteImage = TextureWorker.ReadBGRAImageFromFile(entry.FilePath);
                    if ((int)spriteImage.Width != item.TargetWidth || (int)spriteImage.Height != item.TargetHeight)
                    {
                        spriteImage.Dispose();
                        throw new Exception($"Incorrect dimensions of {entry.FileName}; should be {item.TargetWidth}x{item.TargetHeight}.");
                    }

                    var capturedItem = item;
                    actions.Add(() =>
                    {
                        capturedItem.ReplaceTexture(spriteImage);
                        spriteImage.Dispose();
                    });
                }
            });

            if (dispatcher != null)
            {
                await dispatcher.InvokeAsync(() =>
                {
                    foreach (var action in actions)
                        action();
                });
            }
            else
            {
                foreach (var action in actions)
                    action();
            }
        }

        private static async Task ImportNewTexturePagesFromEntries(UndertaleData data, List<ImportImageEntry> entries,
            int texturePageSize, IProgress<(int current, int total, string message)> progress, ProjectContext project)
        {
            string tempDir = Path.Join(Path.GetTempPath(), $"UMT_Import_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                foreach (var entry in entries)
                {
                    string destFileName;
                    if (entry.ImageType == SpriteType.Background)
                    {
                        destFileName = entry.SpriteName + ".png";
                    }
                    else
                    {
                        destFileName = entry.SpriteName + "_" + entry.FrameIndex + ".png";
                    }

                    string destDir = Path.Join(tempDir, entry.ImageType == SpriteType.Background ? "Backgrounds" : "Sprites");
                    Directory.CreateDirectory(destDir);
                    string dest = Path.Join(destDir, destFileName);
                    File.Copy(entry.FilePath, dest, true);
                }

                var settings = new GraphicsImportSettings
                {
                    ImportFolder = tempDir,
                    Mode = ImportMode.NewTexturePages,
                    ImportUnknownAsSprite = true,
                    ImportFrameless = true,
                    OriginPosition = entries.FirstOrDefault()?.OriginPosition ?? 0,
                    AnimationSpeed = entries.FirstOrDefault()?.AnimationSpeed ?? 1f,
                    PlaybackType = entries.FirstOrDefault()?.PlaybackType ?? 1,
                    IsSpecialType = entries.FirstOrDefault()?.IsSpecialType ?? false,
                    SpecialVersion = entries.FirstOrDefault()?.SpecialVersion ?? 1,
                    TexturePageSize = texturePageSize
                };

                await ImportNewTexturePages(data, settings, progress, project);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static async Task ImportKeepOriginalTexturePageFromEntries(UndertaleData data, List<ImportImageEntry> entries,
            int texturePageSize, IProgress<(int current, int total, string message)> progress, ProjectContext project)
        {
            var dispatcher = Application.Current?.Dispatcher;
            var inPlaceEntries = entries.Where(e => e.CanReplaceInPlace).ToList();
            var repackEntries = entries.Where(e => !e.CanReplaceInPlace).ToList();

            if (inPlaceEntries.Count > 0)
            {
                await ImportReplaceInPlaceFromEntries(data, inPlaceEntries, progress);
            }

            if (repackEntries.Count == 0)
                return;

            var texturePageGroups = repackEntries.GroupBy(e =>
            {
                if (e.ImageType == SpriteType.Background)
                {
                    var bg = data.Backgrounds.ByName(e.SpriteName);
                    return bg?.Texture?.TexturePage;
                }
                else
                {
                    var sprite = data.Sprites.ByName(e.SpriteName);
                    if (sprite is not null && e.FrameIndex >= 0 && e.FrameIndex < sprite.Textures.Count)
                        return sprite.Textures[e.FrameIndex]?.Texture?.TexturePage;
                }
                return null;
            }).Where(g => g.Key is not null);

            int totalRepack = repackEntries.Count;
            int processedRepack = inPlaceEntries.Count;

            foreach (var group in texturePageGroups)
            {
                var texturePage = group.Key;
                var entriesOnPage = group.ToList();

                bool needsRepack = entriesOnPage.Any(e =>
                {
                    if (e.ImageType == SpriteType.Background)
                    {
                        var bg = data.Backgrounds.ByName(e.SpriteName);
                        return bg?.Texture is not null && (e.ImageWidth > bg.Texture.SourceWidth || e.ImageHeight > bg.Texture.SourceHeight);
                    }
                    else
                    {
                        var sprite = data.Sprites.ByName(e.SpriteName);
                        if (sprite is not null && e.FrameIndex >= 0 && e.FrameIndex < sprite.Textures.Count)
                        {
                            var item = sprite.Textures[e.FrameIndex]?.Texture;
                            return item is not null && (e.ImageWidth > item.SourceWidth || e.ImageHeight > item.SourceHeight);
                        }
                    }
                    return false;
                });

                if (!needsRepack)
                {
                    var smallerActions = new List<Action>();
                    foreach (var entry in entriesOnPage)
                    {
                        processedRepack++;
                        progress?.Report((processedRepack, totalRepack + inPlaceEntries.Count, $"Compositing: {entry.FileName}..."));

                        UndertaleTexturePageItem item = null;
                        if (entry.ImageType == SpriteType.Background)
                        {
                            var bg = data.Backgrounds.ByName(entry.SpriteName);
                            item = bg?.Texture;
                        }
                        else
                        {
                            var sprite = data.Sprites.ByName(entry.SpriteName);
                            if (sprite is not null && entry.FrameIndex >= 0 && entry.FrameIndex < sprite.Textures.Count)
                                item = sprite.Textures[entry.FrameIndex]?.Texture;
                        }

                        if (item is null)
                            continue;

                        MagickImage replaceImage = TextureWorker.ReadBGRAImageFromFile(entry.FilePath);
                        var capturedItem = item;
                        var capturedImage = replaceImage;
                        smallerActions.Add(() =>
                        {
                            using IMagickImage<byte> finalImage = TextureWorker.ResizeImage(capturedImage, capturedItem.SourceWidth, capturedItem.SourceHeight);
                            lock (texturePage.TextureData)
                            {
                                using TextureWorker worker = new();
                                MagickImage embImage = worker.GetEmbeddedTexture(texturePage);
                                embImage.Composite(finalImage, capturedItem.SourceX, capturedItem.SourceY, CompositeOperator.Copy);
                                texturePage.TextureData.Image = GMImage.FromMagickImage(embImage)
                                    .ConvertToFormat(texturePage.TextureData.Image.Format);
                            }
                            capturedItem.TargetWidth = (ushort)capturedImage.Width;
                            capturedItem.TargetHeight = (ushort)capturedImage.Height;
                            capturedImage.Dispose();
                        });
                    }

                    if (dispatcher != null)
                    {
                        await dispatcher.InvokeAsync(() =>
                        {
                            foreach (var action in smallerActions)
                                action();
                        });
                    }
                    else
                    {
                        foreach (var action in smallerActions)
                            action();
                    }
                }
                else
                {
                    processedRepack += entriesOnPage.Count;
                    progress?.Report((processedRepack, totalRepack + inPlaceEntries.Count, $"Repacking texture page..."));

                    await RepackTexturePage(data, texturePage, entriesOnPage, dispatcher);
                }
            }
        }

        private static async Task RepackTexturePage(UndertaleData data, UndertaleEmbeddedTexture texturePage,
            List<ImportImageEntry> entriesOnPage, System.Windows.Threading.Dispatcher dispatcher)
        {
            using TextureWorker worker = new();
            MagickImage originalImage = worker.GetEmbeddedTexture(texturePage);
            int pageWidth = (int)originalImage.Width;
            int pageHeight = (int)originalImage.Height;
            var originalFormat = texturePage.TextureData.Image.Format;

            var allItemsOnPage = data.TexturePageItems
                .Where(tpi => tpi.TexturePage == texturePage)
                .ToList();

            var replacedItemSet = new HashSet<UndertaleTexturePageItem>();
            foreach (var entry in entriesOnPage)
            {
                UndertaleTexturePageItem item = null;
                if (entry.ImageType == SpriteType.Background)
                {
                    var bg = data.Backgrounds.ByName(entry.SpriteName);
                    item = bg?.Texture;
                }
                else
                {
                    var sprite = data.Sprites.ByName(entry.SpriteName);
                    if (sprite is not null && entry.FrameIndex >= 0 && entry.FrameIndex < sprite.Textures.Count)
                        item = sprite.Textures[entry.FrameIndex]?.Texture;
                }
                if (item is not null)
                    replacedItemSet.Add(item);
            }

            var itemImages = new List<(UndertaleTexturePageItem item, MagickImage image)>();
            foreach (var item in allItemsOnPage)
            {
                if (replacedItemSet.Contains(item))
                    continue;

                try
                {
                    using var region = originalImage.CloneArea(item.SourceX, item.SourceY, item.SourceWidth, item.SourceHeight);
                    var cloned = new MagickImage(region);
                    itemImages.Add((item, cloned));
                }
                catch { }
            }

            var replaceImages = new List<(ImportImageEntry entry, UndertaleTexturePageItem item, MagickImage image)>();
            foreach (var entry in entriesOnPage)
            {
                UndertaleTexturePageItem item = null;
                if (entry.ImageType == SpriteType.Background)
                {
                    var bg = data.Backgrounds.ByName(entry.SpriteName);
                    item = bg?.Texture;
                }
                else
                {
                    var sprite = data.Sprites.ByName(entry.SpriteName);
                    if (sprite is not null && entry.FrameIndex >= 0 && entry.FrameIndex < sprite.Textures.Count)
                        item = sprite.Textures[entry.FrameIndex]?.Texture;
                }
                if (item is null)
                    continue;

                MagickImage img = TextureWorker.ReadBGRAImageFromFile(entry.FilePath);
                replaceImages.Add((entry, item, img));
            }

            var allRects = new List<(UndertaleTexturePageItem item, int width, int height, MagickImage image, bool isNew)>();

            foreach (var (item, img) in itemImages)
                allRects.Add((item, (int)img.Width, (int)img.Height, img, false));

            foreach (var (entry, item, img) in replaceImages)
                allRects.Add((item, entry.ImageWidth, entry.ImageHeight, img, true));

            int padding = 2;
            var placed = new List<(UndertaleTexturePageItem item, int x, int y, int w, int h, MagickImage image, bool isNew)>();

            allRects.Sort((a, b) =>
            {
                int areaA = a.width * a.height;
                int areaB = b.width * b.height;
                return areaB.CompareTo(areaA);
            });

            foreach (var rect in allRects)
            {
                bool found = false;
                for (int y = 0; y <= pageHeight - rect.height && !found; y += padding)
                {
                    for (int x = 0; x <= pageWidth - rect.width && !found; x += padding)
                    {
                        bool overlaps = false;
                        foreach (var p in placed)
                        {
                            if (x < p.x + p.w + padding && x + rect.width + padding > p.x &&
                                y < p.y + p.h + padding && y + rect.height + padding > p.y)
                            {
                                overlaps = true;
                                break;
                            }
                        }

                        if (!overlaps)
                        {
                            placed.Add((rect.item, x, y, rect.width, rect.height, rect.image, rect.isNew));
                            found = true;
                        }
                    }
                }
            }

            var actions = new List<Action>();

            MagickImage newPageImage = new(MagickColors.Transparent, (uint)pageWidth, (uint)pageHeight);
            newPageImage.Format = MagickFormat.Bgra;
            newPageImage.SetCompression(CompressionMethod.NoCompression);
            newPageImage.Alpha(AlphaOption.Set);

            foreach (var (item, x, y, w, h, image, isNew) in placed)
            {
                newPageImage.Composite(image, x, y, CompositeOperator.Copy);

                var capturedItem = item;
                var capturedX = (ushort)x;
                var capturedY = (ushort)y;
                var capturedW = (ushort)w;
                var capturedH = (ushort)h;

                actions.Add(() =>
                {
                    capturedItem.SourceX = capturedX;
                    capturedItem.SourceY = capturedY;
                    capturedItem.SourceWidth = capturedW;
                    capturedItem.SourceHeight = capturedH;
                    if (isNew)
                    {
                        capturedItem.TargetWidth = capturedW;
                        capturedItem.TargetHeight = capturedH;
                    }
                });
            }

            var capturedFormat = originalFormat;
            actions.Add(() =>
            {
                texturePage.TextureData.Image = GMImage.FromMagickImage(newPageImage)
                    .ConvertToFormat(capturedFormat);
            });

            if (dispatcher != null)
            {
                await dispatcher.InvokeAsync(() =>
                {
                    foreach (var action in actions)
                        action();
                });
            }
            else
            {
                foreach (var action in actions)
                    action();
            }

            foreach (var (item, img) in itemImages)
                img.Dispose();
            foreach (var (entry, item, img) in replaceImages)
                img.Dispose();
            newPageImage.Dispose();
        }
    }
}
