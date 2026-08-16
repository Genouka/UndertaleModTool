using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImageMagick;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

namespace UndertaleModToolAvalonia;

public partial class ImportExportService
{
    /// <summary>Imports fonts from a folder: <c>FontName.png</c> + <c>glyphs_FontName.csv</c>.</summary>
    public void ImportFonts()
    {
        EnsureDataLoaded();

        string importFolder = PromptChooseDirectory();
        if (importFolder is null)
            throw new Exception("The import folder was not set.");

        string packagerDirPath = Path.Join(ExePath, "Packager");
        Directory.CreateDirectory(packagerDirPath);

        string sourcePath = importFolder;
        string searchPattern = "*.png";
        string outName = Path.Join(packagerDirPath, "atlas.txt");
        int textureSize = 2048;
        int border = 2;
        bool debug = false;

        TexturePacker packer = new();
        packer.Process(sourcePath, searchPattern, textureSize, border, debug, loadImages: false, trimImages: false, readSizesOnly: true);
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
                        TargetX = 0,
                        TargetY = 0,
                        TargetWidth = (ushort)n.Bounds.Width,
                        TargetHeight = (ushort)n.Bounds.Height,
                        BoundingWidth = (ushort)n.Bounds.Width,
                        BoundingHeight = (ushort)n.Bounds.Height,
                        TexturePage = texture,
                    };
                    Data.TexturePageItems.Add(texturePageItem);
                    string spriteName = Path.GetFileNameWithoutExtension(n.Texture.Source);

                    UndertaleFont font = Data.Fonts.ByName(spriteName);

                    if (font is null)
                    {
                        UndertaleString fontUTString = Data.Strings.MakeString(spriteName);
                        UndertaleFont newFont = new()
                        {
                            Name = fontUTString,
                        };

                        fontUpdate(newFont);
                        newFont.Texture = texturePageItem;
                        Data.Fonts.Add(newFont);
                        continue;
                    }

                    fontUpdate(font);
                    font.Texture = texturePageItem;
                }
            }
            atlasCount++;
        }

        packer.DisposeImages();
        HideProgressBar();
        ScriptMessage("Import Complete!");

        void fontUpdate(UndertaleFont newFont)
        {
            using (StreamReader reader = new(Paths.JoinVerifyWithinDirectory(sourcePath, $"glyphs_{newFont.Name.Content}.csv")))
            {
                newFont.Glyphs.Clear();
                string line;
                int head = 0;
                bool hadError = false;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] s = line.Split(';');

                    // Skip blank lines like ";;;;;;;"
                    if (s.All(x => x.Length == 0))
                        continue;

                    try
                    {
                        if (head == 1)
                        {
                            newFont.RangeStart = UInt16.Parse(s[0]);
                            head++;
                        }

                        if (head == 0)
                        {
                            String namae = s[0].Replace("\"", "");
                            newFont.DisplayName = Data.Strings.MakeString(namae);
                            newFont.EmSize = UInt16.Parse(s[1]);
                            newFont.Bold = Boolean.Parse(s[2]);
                            newFont.Italic = Boolean.Parse(s[3]);
                            newFont.Charset = Byte.Parse(s[4]);
                            newFont.AntiAliasing = Byte.Parse(s[5]);
                            newFont.ScaleX = UInt16.Parse(s[6]);
                            newFont.ScaleY = UInt16.Parse(s[7]);
                            head++;
                        }

                        if (head > 1)
                        {
                            newFont.Glyphs.Add(new UndertaleFont.Glyph()
                            {
                                Character = UInt16.Parse(s[0]),
                                SourceX = UInt16.Parse(s[1]),
                                SourceY = UInt16.Parse(s[2]),
                                SourceWidth = UInt16.Parse(s[3]),
                                SourceHeight = UInt16.Parse(s[4]),
                                Shift = Int16.Parse(s[5]),
                                Offset = Int16.Parse(s[6]),
                            });
                            newFont.RangeEnd = UInt32.Parse(s[0]);
                        }
                    }
                    catch
                    {
                        hadError = true;
                    }
                }

                if (hadError)
                {
                    ScriptError($"File \"glyphs_{newFont.Name.Content}.csv\" contained some invalid data.", "Format error", false);
                }
            }
        }
    }
}