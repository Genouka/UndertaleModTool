using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

namespace UndertaleModToolAvalonia;

public partial class ImportExportService
{
    public class RepackerRect
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int Right { get { return X + Width; } }
        public int Down { get { return Y + Height; } }
        public int Area { get { return Width * Height; } }
    }

    public class Split : RepackerRect
    {
        public bool Invalidated;

        public Split(int X, int Y, int Width, int Height)
        {
            this.X = X;
            this.Y = Y;
            this.Width = Width;
            this.Height = Height;
            this.Invalidated = false;
        }

        public bool containsRect(RepackerRect rect)
        {
            return (rect.X >= this.X) && (rect.Y >= this.Y) && (this.Right >= rect.Right) && (this.Down >= rect.Down);
        }

        public bool overlapsRect(RepackerRect rect)
        {
            return (((rect.X >= this.X) && (rect.X <= this.Right))
                || ((this.X >= rect.X) && (this.X <= rect.Right)))
                && (((rect.Y >= this.Y) && (rect.Y <= this.Down))
                || ((this.Y >= rect.Y) && (this.Y <= rect.Down)));
        }

        public bool fits(int Width, int Height)
        {
            return (this.Width >= Width) && (this.Height >= Height);
        }

        public IEnumerable<Split> splitNode(RepackerRect rect)
        {
            if (!overlapsRect(rect) || Invalidated)
                return new List<Split>();

            this.Invalidated = true;

            return new List<Split> {
                new Split(this.X, this.Y, this.Width, rect.Y - this.Y),
                new Split(this.X, this.Y, rect.X - this.X, this.Height),
                new Split(this.X, rect.Down, this.Width, this.Down - rect.Down),
                new Split(rect.Right, this.Y, this.Right - rect.Right, this.Height),
            }.Where(item => item.Area > 0);
        }
    }

    public class TextureAtlas2
    {
        public int Width;
        public int Height;
        public int Padding;
        public List<Split> Splits;
        public List<RepackerRect> Textures;

        public TextureAtlas2(int Width, int Height, int Padding)
        {
            this.Splits = new List<Split> { new Split(0, 0, Width, Height) };
            this.Textures = new List<RepackerRect>();
            this.Width = Width;
            this.Height = Height;
            this.Padding = Padding;
        }

        public Split findBestFit(int Width, int Height, Func<Split, float> heuristics)
        {
            var possibleNodes =
                from item in Splits
                where item.fits(Width, Height)
                orderby heuristics(item) ascending
                select item;

            return possibleNodes.DefaultIfEmpty(null).First();
        }

        public RepackerRect Allocate(int Width, int Height)
        {
            var pWidth = Width + 2 * this.Padding;
            var pHeight = Height + 2 * this.Padding;

            var bestFit = findBestFit(pWidth, pHeight,
                split => Math.Max(split.Width - pWidth, split.Height - pHeight)
            );

            if (bestFit == null)
                return null;

            RepackerRect rect = new RepackerRect()
            {
                X = bestFit.X,
                Y = bestFit.Y,
                Width = pWidth,
                Height = pHeight
            };

            var newSplits = Splits
                .AsParallel()
                .Select(item => item.splitNode(rect))
                .SelectMany(item => item)
                .ToList();

            Splits = Enumerable.Concat(Splits.Where(item => item.Invalidated == false), newSplits).ToList();

            foreach (var split1 in Splits)
            {
                foreach (var split2 in Splits)
                {
                    if (split1 == split2)
                        continue;

                    if (split1.containsRect((RepackerRect)split2))
                        split2.Invalidated = true;
                }
            }

            Splits.RemoveAll(item => item.Invalidated);

            var tex = new RepackerRect()
            {
                X = bestFit.X + Padding,
                Y = bestFit.Y + Padding,
                Width = Width,
                Height = Height
            };

            Textures.Add(tex);
            return tex;
        }
    }

    public class TPageItem
    {
        public uint Scaled;
        public string Filename;
        public RepackerRect OriginalRect;
        public RepackerRect NewRect;
        public TextureAtlas2 Atlas;
        public UndertaleTexturePageItem Item;
    }

    private static int NearestPowerOf2(uint x)
    {
        return 1 << (sizeof(uint) * 8 - BitOperations.LeadingZeroCount(x - 1));
    }

    /// <summary>Repacks embedded texture pages into smaller pages to reduce VRAM/stutter.</summary>
    public async void NewTextureRepacker()
    {
        EnsureDataLoaded();

        int progress = 0;
        string updateText = "";
        void UpdateProgress(int updateAmount)
        {
            SetProgressBar(null, updateText, progress += updateAmount, Data.TexturePageItems.Count);
            this.progressValue = progress;
        }

        void ResetProgress(string text)
        {
            progress = 0;
            updateText = text;
            UpdateProgress(0);
        }

        TPageItem dumpTexturePageItem(UndertaleTexturePageItem pageItem, TextureWorker worker, string pageItemFile, bool reuse)
        {
            TPageItem page = new TPageItem();
            page.Filename = pageItemFile;
            page.Item = pageItem;
            page.Scaled = page.Item.TexturePage.Scaled;

            page.OriginalRect = new RepackerRect()
            {
                X = pageItem.SourceX,
                Y = pageItem.SourceY,
                Width = pageItem.SourceWidth,
                Height = pageItem.SourceHeight
            };

            if (!reuse)
                worker.ExportAsPNG(pageItem, pageItemFile);
            UpdateProgress(1);

            return page;
        }

        async Task<List<TPageItem>> dumpTexturePageItems(string dir, bool reuse)
        {
            using var worker = new TextureWorker();

            var tpageitems = await Task.Run(() => Data.TexturePageItems
                .AsParallel()
                .Select(item => dumpTexturePageItem(item, worker, Paths.JoinVerifyWithinDirectory(dir, $"texture_page_{Data.TexturePageItems.IndexOf(item)}.png"), reuse))
                .ToList());

            return tpageitems;
        }

        int doItemGrouping(TPageItem item)
        {
            return 1;
        }

        List<TextureAtlas2> layoutPageItemList(List<TPageItem> items, int pageSizeWidth, int pageSizeHeight, int padding)
        {
            var atlas_list = new List<TextureAtlas2>();
            while (items.Count > 0)
            {
                var atlas = new TextureAtlas2(pageSizeWidth, pageSizeHeight, padding);
                foreach (var page in items)
                {
                    var rect = atlas.Allocate(page.OriginalRect.Width, page.OriginalRect.Height);
                    if (rect == null)
                        break;

                    page.NewRect = rect;
                    page.Atlas = atlas;
                    UpdateProgress(1);
                }

                items.RemoveAll(item => item.Atlas != null);

                if (atlas.Textures.Count > 0)
                    atlas_list.Add(atlas);
                else
                    break;
            }

            return atlas_list;
        }

        async Task<List<TextureAtlas2>> layoutPageItemLists<K>(ILookup<K, TPageItem> lookup, int pageSizeWidth, int pageSizeHeight, int padding)
        {
            return await Task.Run(() => lookup
                .AsParallel()
                .Select(list => layoutPageItemList(list.ToList(), pageSizeWidth, pageSizeHeight, padding))
                .SelectMany(item => item)
                .ToList());
        }

        // User Configurable:: Atlas page size and item padding
        var pageSizeWidth = 1024;
        var pageSizeHeight = 1024;
        var padding = 1;

        // User Configurable:: Dimension cutoffs (gets thrown off the atlas pool)
        var maxDims = 256;
        var maxArea = 256 * 128;

        // User Configurable:: Force Power of Two (POT) sizes.
        bool forcePOT = false;
        List<TPageItem> potBlacklist = new List<TPageItem>();

        if (forcePOT)
        {
            pageSizeWidth = NearestPowerOf2((uint)pageSizeWidth);
            pageSizeHeight = NearestPowerOf2((uint)pageSizeHeight);
        }

        bool reuseTextures = false;

        string packagerDirectory = Path.Join(ExePath, "Packager");
        if (Directory.Exists(packagerDirectory))
        {
            reuseTextures = ScriptQuestion("Do you want to reuse previously extracted page items?");
        }
        Directory.CreateDirectory(packagerDirectory);

        // Dump all the texture page items
        ResetProgress("Existing Textures Exported");
        var texPageItems = await dumpTexturePageItems(packagerDirectory, reuseTextures);

        // Clear embedded textures and any possibly stale references to them
        Data.EmbeddedTextures.Clear();
        if (Data.TextureGroupInfo is not null)
        {
            foreach (var texInfo in Data.TextureGroupInfo)
            {
                if (texInfo is null)
                    continue;
                texInfo.TexturePages.Clear();
            }
        }

        ILookup<(uint Scaled, int), TPageItem> texPageLookup = null;
        await Task.Run(() =>
        {
            texPageLookup = texPageItems.OrderBy(
                item => Math.Max(item.OriginalRect.Width, item.OriginalRect.Height)
            ).Where(
                item => (item.OriginalRect.Area < maxArea)
                     && (item.OriginalRect.Width <= maxDims && item.OriginalRect.Height <= maxDims)
                     && (texPageItems.Any(item2 => (item2 != item) && (item.Item.TexturePage == item2.Item.TexturePage)))
            ).ToLookup(
                item => (item.Item.TexturePage.Scaled, doItemGrouping(item))
            );
        });

        ResetProgress("Laying out texture items");
        var atlases = await layoutPageItemLists(texPageLookup, pageSizeWidth, pageSizeHeight, padding);

        int lastTextPage = Data.EmbeddedTextures.Count - 1;

        ResetProgress("Regenerating Texture Pages");

        var f = new StreamWriter(Path.Join(packagerDirectory, "log.txt"));
        int atlasCount = 0;

        var groups = texPageItems.GroupBy(item => item.Atlas);
        await Task.Run(() =>
        {
            foreach (var group in groups)
            {
                TextureAtlas2 atlas = group.Key;
                var atlasName = atlas != null ? (atlasCount++).ToString() : "null";
                f.WriteLine($" -- ATLAS {atlasName} -- ");

                if (atlas != null)
                {
                    UndertaleEmbeddedTexture tex = new();
                    tex.Name = new UndertaleString("Texture " + ++lastTextPage);
                    MainThreadAction(() => Data.EmbeddedTextures.Add(tex));

                    using MagickImage newAtlasImage = new(MagickColors.Transparent, (uint)atlas.Width, (uint)atlas.Height);

                    tex.Scaled = group.First().Scaled;

                    foreach (var split in atlas.Splits)
                        f.WriteLine($"split: {atlas.Splits.IndexOf(split)}: {split.X}, {split.Y}, {split.Width}, {split.Height}");

                    foreach (var item in group)
                    {
                        f.WriteLine($"tex: {texPageItems.IndexOf(item)}: {item.NewRect.X}, {item.NewRect.Y}, {item.NewRect.Width}, {item.NewRect.Height}");

                        using (MagickImage source = TextureWorker.ReadBGRAImageFromFile(item.Filename))
                        {
                            newAtlasImage.Composite(source, item.NewRect.X, item.NewRect.Y, CompositeOperator.Copy);
                        }

                        item.Item.TexturePage = tex;
                        item.Item.SourceX = (ushort)item.NewRect.X;
                        item.Item.SourceY = (ushort)item.NewRect.Y;
                        item.Item.SourceWidth = (ushort)item.NewRect.Width;
                        item.Item.SourceHeight = (ushort)item.NewRect.Height;
                        UpdateProgress(1);
                    }

                    string atlasFile = Paths.JoinVerifyWithinDirectory(packagerDirectory, $"atlas_{atlasName}.png");
                    TextureWorker.SaveImageToFile(newAtlasImage, atlasFile);

                    tex.TextureData.Image = GMImage.FromMagickImage(newAtlasImage).ConvertToPng();
                }
                else
                {
                    foreach (var item in group)
                    {
                        f.WriteLine($"tex: {texPageItems.IndexOf(item)}: {0}, {0}, {item.OriginalRect.Width}, {item.OriginalRect.Height}");

                        UndertaleEmbeddedTexture tex = new();
                        tex.Name = new UndertaleString("Texture " + ++lastTextPage);
                        MainThreadAction(() => Data.EmbeddedTextures.Add(tex));

                        string itemFile = item.Filename;
                        if (forcePOT && !potBlacklist.Contains(item))
                        {
                            int potw = NearestPowerOf2((uint)item.OriginalRect.Width),
                                poth = NearestPowerOf2((uint)item.OriginalRect.Height);

                            using MagickImage newAtlasImage = new(MagickColors.Transparent, (uint)potw, (uint)poth);

                            using (MagickImage source = TextureWorker.ReadBGRAImageFromFile(item.Filename))
                            {
                                newAtlasImage.Composite(source, 0, 0, CompositeOperator.Copy);
                            }

                            itemFile = Paths.JoinVerifyWithinDirectory(packagerDirectory, $"pot_{texPageItems.IndexOf(item)}.png");
                            TextureWorker.SaveImageToFile(newAtlasImage, itemFile);

                            tex.TextureData.Image = GMImage.FromMagickImage(newAtlasImage).ConvertToPng();
                        }
                        else
                        {
                            tex.TextureData.Image = GMImage.FromPng(File.ReadAllBytes(itemFile));
                        }

                        tex.Scaled = item.Scaled;

                        item.Item.TexturePage = tex;
                        item.Item.SourceX = 0;
                        item.Item.SourceY = 0;
                        item.Item.SourceWidth = (ushort)item.OriginalRect.Width;
                        item.Item.SourceHeight = (ushort)item.OriginalRect.Height;
                        UpdateProgress(1);
                    }
                }
            }
        });

        f.Close();

        HideProgressBar();
    }

    /// <summary>Reduces the number of embedded texture pages by repacking all textures.</summary>
    public async void ReduceEmbeddedTexturePages()
    {
        EnsureDataLoaded();

        DirectoryInfo dir = Directory.CreateDirectory(Path.Join(ExePath, "Packager"));

        // Clear any files if they already exist
        foreach (FileInfo file in dir.GetFiles())
            file.Delete();
        foreach (DirectoryInfo di in dir.GetDirectories())
            di.Delete(true);

        string exportedTexturesFolder = Path.Join(dir.FullName, "Textures");
        ConcurrentDictionary<string, int[]> assetCoordinateDict = new();
        ConcurrentDictionary<string, string> assetTypeDict = new();
        using (TextureWorker worker = new())
        {
            Directory.CreateDirectory(exportedTexturesFolder);

            SetProgressBar(null, "Existing Textures Exported", 0, Data.TexturePageItems.Count);

            await Task.Run(() => Parallel.ForEach(Data.Sprites, sprite =>
            {
                if (sprite is not null)
                {
                    for (int i = 0; i < sprite.Textures.Count; i++)
                    {
                        if (sprite.Textures[i]?.Texture != null)
                        {
                            UndertaleTexturePageItem tex = sprite.Textures[i].Texture;
                            worker.ExportAsPNG(tex, Paths.JoinVerifyWithinDirectory(exportedTexturesFolder, $"{sprite.Name.Content}_{i}.png"));
                            assetCoordinateDict.TryAdd($"{sprite.Name.Content}_{i}", new int[] { tex.TargetX, tex.TargetY, tex.SourceWidth, tex.SourceHeight, tex.TargetWidth, tex.TargetHeight, tex.BoundingWidth, tex.BoundingHeight });
                            assetTypeDict.TryAdd($"{sprite.Name.Content}_{i}", "spr");
                        }
                    }
                }

                AddProgressParallel(sprite is not null ? sprite.Textures.Count : 0);
            }));

            await Task.Run(() => Parallel.ForEach(Data.Fonts, font =>
            {
                if (font is null)
                    return;
                if (font.Texture != null)
                {
                    UndertaleTexturePageItem tex = font.Texture;
                    worker.ExportAsPNG(tex, Paths.JoinVerifyWithinDirectory(exportedTexturesFolder, $"{font.Name.Content}.png"));
                    assetCoordinateDict.TryAdd(font.Name.Content, new int[] { tex.TargetX, tex.TargetY, tex.SourceWidth, tex.SourceHeight, tex.TargetWidth, tex.TargetHeight, tex.BoundingWidth, tex.BoundingHeight });
                    assetTypeDict.TryAdd(font.Name.Content, "fnt");

                    IncrementProgressParallel();
                }
            }));

            await Task.Run(() => Parallel.ForEach(Data.Backgrounds, background =>
            {
                if (background is null)
                    return;
                if (background.Texture != null)
                {
                    UndertaleTexturePageItem tex = background.Texture;
                    worker.ExportAsPNG(tex, Paths.JoinVerifyWithinDirectory(exportedTexturesFolder, $"{background.Name.Content}.png"));
                    assetCoordinateDict.TryAdd(background.Name.Content, new int[] { tex.TargetX, tex.TargetY, tex.SourceWidth, tex.SourceHeight, tex.TargetWidth, tex.TargetHeight, tex.BoundingWidth, tex.BoundingHeight });
                    assetTypeDict.TryAdd(background.Name.Content, "bg");
                    IncrementProgressParallel();
                }
            }));
        }

        HideProgressBar();

        string sourcePath = exportedTexturesFolder;
        string searchPattern = "*.png";
        string outName = Path.Join(dir.FullName, "atlas.txt");
        int textureSize = 2048;
        int paddingValue = 2;
        bool debug = false;

        // Delete all existing Textures and TextureSheets
        Data.TexturePageItems.Clear();
        Data.EmbeddedTextures.Clear();

        // Run the texture packer
        TexturePacker packer = new();
        packer.Process(sourcePath, searchPattern, textureSize, paddingValue, debug, loadImages: true, trimImages: false, readSizesOnly: false);
        packer.SaveAtlasses(outName);

        int lastTextPage = Data.EmbeddedTextures.Count - 1;
        int lastTextPageItem = Data.TexturePageItems.Count - 1;

        string prefix = Path.Join(Path.GetDirectoryName(outName), Path.GetFileNameWithoutExtension(outName));
        int atlasCount = 0;
        foreach (TextureAtlas atlas in packer.Atlasses)
        {
            string atlasName = $"{prefix}{atlasCount:000}.png";
            UndertaleEmbeddedTexture texture = new()
            {
                Name = new UndertaleString("Texture " + ++lastTextPage),
            };
            texture.TextureData.Image = GMImage.FromPng(File.ReadAllBytes(atlasName));
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
                        BoundingWidth = (ushort)n.Bounds.Width,
                        BoundingHeight = (ushort)n.Bounds.Height,
                        TexturePage = texture,
                    };
                    Data.TexturePageItems.Add(texturePageItem);

                    string stripped = Path.GetFileNameWithoutExtension(n.Texture.Source);

                    if (assetTypeDict.TryGetValue(stripped, out string spriteType))
                    {
                        setTextureTargetBounds(texturePageItem, stripped, n);
                        if (spriteType.Equals("bg"))
                        {
                            UndertaleBackground background = Data.Backgrounds.ByName(stripped);
                            background.Texture = texturePageItem;
                        }
                        else if (spriteType.Equals("fnt"))
                        {
                            UndertaleFont font = Data.Fonts.ByName(stripped);
                            font.Texture = texturePageItem;
                        }
                        else
                        {
                            string spriteName;
                            int frame;
                            try
                            {
                                int lastUnderscore = stripped.LastIndexOf('_');
                                spriteName = stripped.Substring(0, lastUnderscore);
                                frame = Int32.Parse(stripped.Substring(lastUnderscore + 1));
                            }
                            catch
                            {
                                ScriptMessage($"Error: Image {stripped} has an invalid name. Skipping...");
                                continue;
                            }
                            UndertaleSprite sprite = Data.Sprites.ByName(spriteName);

                            UndertaleSprite.TextureEntry texentry = new() { Texture = texturePageItem };

                            if (frame > sprite.Textures.Count - 1)
                            {
                                while (frame > sprite.Textures.Count - 1)
                                    sprite.Textures.Add(texentry);
                                continue;
                            }
                            sprite.Textures[frame] = texentry;
                        }
                    }
                    else
                    {
                        // Try string parsing fallback (old behaviour without asset type dict entries).
                        try
                        {
                            int lastUnderscore = stripped.LastIndexOf('_');
                            string spriteName = stripped.Substring(0, lastUnderscore);
                            int frame = Int32.Parse(stripped.Substring(lastUnderscore + 1));
                            setTextureTargetBounds(texturePageItem, stripped, n);
                            UndertaleSprite sprite = Data.Sprites.ByName(spriteName);
                            UndertaleSprite.TextureEntry texentry = new() { Texture = texturePageItem };
                            if (frame > sprite.Textures.Count - 1)
                            {
                                while (frame > sprite.Textures.Count - 1)
                                    sprite.Textures.Add(texentry);
                                continue;
                            }
                            sprite.Textures[frame] = texentry;
                        }
                        catch
                        {
                            ScriptMessage("Error: Image " + stripped + " has an invalid name.");
                            continue;
                        }
                    }
                }
            }
            atlasCount++;
        }

        packer.DisposeImages();
        HideProgressBar();
        ScriptMessage("Import Complete!");

        void setTextureTargetBounds(UndertaleTexturePageItem tex, string textureName, TextureNode n)
        {
            if (assetCoordinateDict.TryGetValue(textureName, out int[] coords))
            {
                tex.TargetX = (ushort)coords[0];
                tex.TargetY = (ushort)coords[1];
                tex.SourceWidth = (ushort)coords[2];
                tex.SourceHeight = (ushort)coords[3];
                tex.TargetWidth = (ushort)coords[4];
                tex.TargetHeight = (ushort)coords[5];
                tex.BoundingWidth = (ushort)coords[6];
                tex.BoundingHeight = (ushort)coords[7];
            }
            else
            {
                tex.TargetX = 0;
                tex.TargetY = 0;
                tex.TargetWidth = (ushort)n.Bounds.Width;
                tex.TargetHeight = (ushort)n.Bounds.Height;
            }
        }
    }

    /// <summary>TargetWidth/TargetHeight setter used by ImportFonts & co.</summary>
    internal static void SetFontPageItemBounds(UndertaleTexturePageItem item, TextureNode n)
    {
        item.TargetX = 0;
        item.TargetY = 0;
        item.TargetWidth = (ushort)n.Bounds.Width;
        item.TargetHeight = (ushort)n.Bounds.Height;
    }
}