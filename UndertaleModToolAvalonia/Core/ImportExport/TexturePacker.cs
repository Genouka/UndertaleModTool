using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ImageMagick;
using UndertaleModLib.Util;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Rectangle type used by <see cref="TexturePacker"/>.
/// </summary>
public struct N1Rect
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public enum TextureSplitType
{
    Horizontal,
    Vertical,
}

public enum TextureBestFitHeuristic
{
    Area,
    MaxOneAxis,
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

public class TextureNode
{
    public N1Rect Bounds;
    public TextureInfo Texture;
    public TextureSplitType SplitType;
}

public class TextureAtlas
{
    public int Width;
    public int Height;
    public List<TextureNode> Nodes;
}

/// <summary>
/// General-purpose texture packer, based on the one bundled with the
/// ImportGraphics / ImportFonts / ReduceEmbeddedTexturePages scripts.
/// </summary>
public class TexturePacker
{
    public List<TextureInfo> SourceTextures;
    public StringWriter Log;
    public StringWriter Error;
    public int Padding;
    public int AtlasSize;
    public bool DebugMode;
    public TextureBestFitHeuristic FitHeuristic;
    public List<TextureAtlas> Atlasses;

    // Used to keep references to in-memory images alive (and disposed later).
    public List<MagickImage> ImagesToCleanup = [];

    public TexturePacker()
    {
        SourceTextures = [];
        Log = new StringWriter();
        Error = new StringWriter();
        FitHeuristic = TextureBestFitHeuristic.MaxOneAxis;
    }

    public void Process(string sourceDir, string searchPattern, int atlasSize, int padding, bool debug, bool loadImages, bool trimImages, bool readSizesOnly = false)
    {
        Padding = padding;
        AtlasSize = atlasSize;
        DebugMode = debug;

        ScanForTextures(sourceDir, searchPattern, loadImages, trimImages, readSizesOnly);

        List<TextureInfo> textures = SourceTextures.ToList();
        Atlasses = [];

        while (textures.Count > 0)
        {
            TextureAtlas atlas = new()
            {
                Width = atlasSize,
                Height = atlasSize,
            };

            List<TextureInfo> leftovers = LayoutAtlas(textures, atlas);

            if (leftovers.Count == 0)
            {
                // we reached the last atlas. Check if this last atlas could have been twice smaller
                while (leftovers.Count == 0)
                {
                    atlas.Width /= 2;
                    atlas.Height /= 2;
                    leftovers = LayoutAtlas(textures, atlas);
                }

                // we need to go 1 step larger as we found the first size that is too small
                atlas.Width = (atlas.Width == 0) ? 1 : atlas.Width * 2;
                atlas.Height = (atlas.Height == 0) ? 1 : atlas.Height * 2;
                leftovers = LayoutAtlas(textures, atlas);
            }

            Atlasses.Add(atlas);
            textures = leftovers;
        }
    }

    private void ScanForTextures(string path, string wildcard, bool loadImages, bool trimImages, bool readSizesOnly)
    {
        DirectoryInfo di = new(path);
        FileInfo[] files = di.GetFiles(wildcard, SearchOption.AllDirectories);
        foreach (FileInfo fi in files)
        {
            (int width, int height) = readSizesOnly
                ? TextureWorker.GetImageSizeFromFile(fi.FullName)
                : ((int)new MagickImageInfo(fi.FullName).Width, (int)new MagickImageInfo(fi.FullName).Height);

            if (width == -1 || height == -1)
                continue;

            if (width <= AtlasSize && height <= AtlasSize)
            {
                TextureInfo ti = new()
                {
                    Source = fi.FullName,
                };

                if (loadImages)
                {
                    MagickImage img = TextureWorker.ReadBGRAImageFromFile(fi.FullName);
                    ImagesToCleanup.Add(img);

                    ti.BoundingWidth = (int)img.Width;
                    ti.BoundingHeight = (int)img.Height;

                    if (trimImages)
                    {
                        img.BorderColor = MagickColors.Transparent;
                        img.BackgroundColor = MagickColors.Transparent;
                        img.Border(1);
                        IMagickGeometry? bbox = img.BoundingBox;
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
                    else
                    {
                        ti.BoundingWidth = (int)img.Width;
                        ti.BoundingHeight = (int)img.Height;
                    }

                    ti.Width = (int)img.Width;
                    ti.Height = (int)img.Height;
                    ti.Image = img;
                }
                else
                {
                    ti.Width = width;
                    ti.Height = height;
                    ti.BoundingWidth = width;
                    ti.BoundingHeight = height;
                }

                SourceTextures.Add(ti);
                Log.WriteLine($"Added {fi.FullName}");
            }
            else
            {
                Error.WriteLine($"{fi.FullName} is too large to fix in the atlas. Skipping!");
            }
        }
    }

    public void AddSource(MagickImage img, string fullName, bool trimImages, bool readSizesOnly = false)
    {
        ImagesToCleanup.Add(img);

        if (img.Width > AtlasSize || img.Height > AtlasSize)
        {
            Error.WriteLine($"{fullName} is too large to fix in the atlas. Skipping!");
            return;
        }

        TextureInfo ti = new()
        {
            Source = fullName,
        };

        if (readSizesOnly)
        {
            ti.Width = (int)img.Width;
            ti.Height = (int)img.Height;
            ti.BoundingWidth = (int)img.Width;
            ti.BoundingHeight = (int)img.Height;
        }
        else if (trimImages)
        {
            ti.BoundingWidth = (int)img.Width;
            ti.BoundingHeight = (int)img.Height;

            img.BorderColor = MagickColors.Transparent;
            img.BackgroundColor = MagickColors.Transparent;
            img.Border(1);
            IMagickGeometry? bbox = img.BoundingBox;
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

            ti.Width = (int)img.Width;
            ti.Height = (int)img.Height;
            ti.Image = img;
        }
        else
        {
            ti.BoundingWidth = (int)img.Width;
            ti.BoundingHeight = (int)img.Height;
            ti.Width = (int)img.Width;
            ti.Height = (int)img.Height;
            ti.Image = img;
        }

        SourceTextures.Add(ti);
        Log.WriteLine($"Added {fullName}");
    }

    public void SaveAtlasses(string destination)
    {
        int atlasCount = 0;
        string prefix = Path.Join(Path.GetDirectoryName(destination), Path.GetFileNameWithoutExtension(destination));

        StreamWriter tw = new(destination);
        tw.WriteLine("source_tex, atlas_tex, x, y, width, height");
        foreach (TextureAtlas atlas in Atlasses)
        {
            string atlasName = $"{prefix}{atlasCount:000}.png";

            using (MagickImage img = CreateAtlasImage(atlas))
                TextureWorker.SaveImageToFile(img, atlasName);

            foreach (TextureNode n in atlas.Nodes)
            {
                if (n.Texture is not null)
                {
                    tw.Write(n.Texture.Source + ", ");
                    tw.Write(atlasName + ", ");
                    tw.Write(n.Bounds.X.ToString() + ", ");
                    tw.Write(n.Bounds.Y.ToString() + ", ");
                    tw.Write(n.Bounds.Width.ToString() + ", ");
                    tw.WriteLine(n.Bounds.Height.ToString());
                }
            }

            if (atlas.Nodes.Count == 0 && atlas.Width > 0)
            {
                // Keep a reference so an empty placeholder atlas still gets written.
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

    private void HorizontalSplit(TextureNode toSplit, int width, int height, List<TextureNode> list)
    {
        TextureNode n1 = new()
        {
            Bounds = new N1Rect
            {
                X = toSplit.Bounds.X + width + Padding,
                Y = toSplit.Bounds.Y,
                Width = toSplit.Bounds.Width - width - Padding,
                Height = height,
            },
            SplitType = TextureSplitType.Vertical,
        };
        TextureNode n2 = new()
        {
            Bounds = new N1Rect
            {
                X = toSplit.Bounds.X,
                Y = toSplit.Bounds.Y + height + Padding,
                Width = toSplit.Bounds.Width,
                Height = toSplit.Bounds.Height - height - Padding,
            },
            SplitType = TextureSplitType.Horizontal,
        };
        if (n1.Bounds.Width > 0 && n1.Bounds.Height > 0)
            list.Add(n1);
        if (n2.Bounds.Width > 0 && n2.Bounds.Height > 0)
            list.Add(n2);
    }

    private void VerticalSplit(TextureNode toSplit, int width, int height, List<TextureNode> list)
    {
        TextureNode n1 = new()
        {
            Bounds = new N1Rect
            {
                X = toSplit.Bounds.X + width + Padding,
                Y = toSplit.Bounds.Y,
                Width = toSplit.Bounds.Width - width - Padding,
                Height = toSplit.Bounds.Height,
            },
            SplitType = TextureSplitType.Vertical,
        };
        TextureNode n2 = new()
        {
            Bounds = new N1Rect
            {
                X = toSplit.Bounds.X,
                Y = toSplit.Bounds.Y + height + Padding,
                Width = width,
                Height = toSplit.Bounds.Height - height - Padding,
            },
            SplitType = TextureSplitType.Horizontal,
        };
        if (n1.Bounds.Width > 0 && n1.Bounds.Height > 0)
            list.Add(n1);
        if (n2.Bounds.Width > 0 && n2.Bounds.Height > 0)
            list.Add(n2);
    }

    private TextureInfo FindBestFitForNode(TextureNode node, List<TextureInfo> textures)
    {
        TextureInfo bestFit = null;
        float nodeArea = node.Bounds.Width * node.Bounds.Height;
        float maxCriteria = 0.0f;
        foreach (TextureInfo ti in textures)
        {
            switch (FitHeuristic)
            {
                case TextureBestFitHeuristic.MaxOneAxis:
                    if (ti.Width <= node.Bounds.Width && ti.Height <= node.Bounds.Height)
                    {
                        float wRatio = (float)ti.Width / node.Bounds.Width;
                        float hRatio = (float)ti.Height / node.Bounds.Height;
                        float ratio = wRatio > hRatio ? wRatio : hRatio;
                        if (ratio > maxCriteria)
                        {
                            maxCriteria = ratio;
                            bestFit = ti;
                        }
                    }
                    break;
                case TextureBestFitHeuristic.Area:
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

    public List<TextureInfo> LayoutAtlasPublic(List<TextureInfo> textures, TextureAtlas atlas)
        => LayoutAtlas(textures, atlas);

    private List<TextureInfo> LayoutAtlas(List<TextureInfo> textures, TextureAtlas atlas)
    {
        List<TextureNode> freeList = [];
        atlas.Nodes = [];
        List<TextureInfo> remainingTextures = textures.ToList();

        TextureNode root = new()
        {
            Bounds = new N1Rect { Width = atlas.Width, Height = atlas.Height },
            SplitType = TextureSplitType.Horizontal,
        };
        freeList.Add(root);

        while (freeList.Count > 0 && remainingTextures.Count > 0)
        {
            TextureNode node = freeList[0];
            freeList.RemoveAt(0);

            TextureInfo bestFit = FindBestFitForNode(node, remainingTextures);
            if (bestFit is not null)
            {
                if (node.SplitType == TextureSplitType.Horizontal)
                    HorizontalSplit(node, bestFit.Width, bestFit.Height, freeList);
                else
                    VerticalSplit(node, bestFit.Width, bestFit.Height, freeList);

                node.Texture = bestFit;
                node.Bounds.Width = bestFit.Width;
                node.Bounds.Height = bestFit.Height;
                remainingTextures.Remove(bestFit);
            }
            atlas.Nodes.Add(node);
        }

        return remainingTextures;
    }

    private MagickImage CreateAtlasImage(TextureAtlas atlas)
    {
        MagickImage img = new(MagickColors.Transparent, (uint)atlas.Width, (uint)atlas.Height);

        foreach (TextureNode n in atlas.Nodes)
        {
            if (n.Texture is not null)
            {
                using IMagickImage<byte> source = n.Texture.Image is not null
                    ? (IMagickImage<byte>)n.Texture.Image
                    : TextureWorker.ReadBGRAImageFromFile(n.Texture.Source);
                using IMagickImage<byte> resizedSourceImg = TextureWorker.ResizeImage(source, n.Bounds.Width, n.Bounds.Height);
                img.Composite(resizedSourceImg, n.Bounds.X, n.Bounds.Y, CompositeOperator.Copy);
            }
        }

        return img;
    }

    public void DisposeImages()
    {
        foreach (MagickImage img in ImagesToCleanup)
            img.Dispose();
        ImagesToCleanup.Clear();
    }
}