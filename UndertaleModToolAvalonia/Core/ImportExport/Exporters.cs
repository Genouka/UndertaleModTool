using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ImageMagick;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
using Underanalyzer.Decompiler;
using UndertaleModLib.Decompiler;

namespace UndertaleModToolAvalonia;

public partial class ImportExportService
{
    /// <summary>Exports all code entries as assembly (.asm) files.</summary>
    public async void ExportAllAssembly()
    {
        EnsureDataLoaded();

        if (Data.IsYYC())
        {
            ScriptError("The opened game uses YYC: no code is available.");
            return;
        }

        string codeFolder = PromptChooseDirectory();
        if (codeFolder is null)
            return;

        List<UndertaleCode> toDump = Data.Code.Where(c => c.ParentEntry is null).ToList();

        SetProgressBar(null, "Code Entries", 0, toDump.Count);

        await Task.Run(() => Parallel.ForEach(toDump, code =>
        {
            if (code is not null)
            {
                string path = Paths.JoinVerifyWithinDirectory(codeFolder, $"{code.Name.Content}.asm");
                try
                {
                    File.WriteAllText(path, code.Disassemble(Data.Variables, Data.CodeLocals?.For(code)));
                }
                catch (Exception e)
                {
                    File.WriteAllText(path, $"/*\nDISASSEMBLY FAILED!\n\n{e}\n*/");
                }
            }

            IncrementProgressParallel();
        }));

        HideProgressBar();
    }

    /// <summary>Exports all code entries as decompiled GML (.gml) files.</summary>
    public async void ExportAllCode()
    {
        EnsureDataLoaded();

        if (Data.IsYYC())
        {
            ScriptError("The opened game uses YYC: no code is available.");
            return;
        }

        string codeFolder = PromptChooseDirectory();
        if (codeFolder is null)
            return;

        GlobalDecompileContext globalDecompileContext = new(Data);
        IDecompileSettings decompilerSettings = Data.ToolInfo.DecompilerSettings;

        List<UndertaleCode> toDump = Data.Code.Where(c => c.ParentEntry is null).ToList();

        SetProgressBar(null, "Code Entries", 0, toDump.Count);

        await Task.Run(() => Parallel.ForEach(toDump, code =>
        {
            if (code is not null)
            {
                string path = Paths.JoinVerifyWithinDirectory(codeFolder, code.Name.Content + ".gml");
                try
                {
                    File.WriteAllText(path, new DecompileContext(globalDecompileContext, code, decompilerSettings).DecompileToString());
                }
                catch (Exception e)
                {
                    File.WriteAllText(path, "/*\nDECOMPILER FAILED!\n\n" + e.ToString() + "\n*/");
                }
            }

            IncrementProgressParallel();
        }));

        HideProgressBar();
    }

    /// <summary>Exports specific code entries (by typed names, including object events).</summary>
    public async void ExportSpecificCode()
    {
        EnsureDataLoaded();

        if (Data.IsYYC())
        {
            ScriptError("The opened game uses YYC: no code is available.");
            return;
        }

        GlobalDecompileContext globalDecompileContext = new(Data);
        IDecompileSettings decompilerSettings = Data.ToolInfo.DecompilerSettings;

        int failed = 0;

        string codeFolder = PromptChooseDirectory();
        if (codeFolder is null)
            throw new Exception("The export folder was not set.");
        codeFolder = Path.Join(codeFolder, "Code");
        Directory.CreateDirectory(codeFolder);

        List<string> codeToDump = new();
        List<string> gameObjectCandidates = new();
        List<string> splitStringsList = new();

        string inputtedText = SimpleTextInput("Menu", "Enter object, script, or code entry names", "", true) ?? "";
        string[] individualLineArray = inputtedText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var oneLine in individualLineArray)
        {
            splitStringsList.Add(oneLine.Trim());
        }
        for (var j = 0; j < splitStringsList.Count; j++)
        {
            foreach (UndertaleGameObject obj in Data.GameObjects)
            {
                if (obj is null)
                    continue;
                if (splitStringsList[j].ToLower() == obj.Name.Content.ToLower())
                {
                    gameObjectCandidates.Add(obj.Name.Content);
                }
            }
            foreach (UndertaleScript scr in Data.Scripts)
            {
                if (scr is null || scr.Code == null)
                    continue;
                if (splitStringsList[j].ToLower() == scr.Name.Content.ToLower())
                {
                    codeToDump.Add(scr.Code.Name.Content);
                }
            }
            foreach (UndertaleGlobalInit globalInit in Data.GlobalInitScripts)
            {
                if (globalInit is null || globalInit.Code == null)
                    continue;
                if (splitStringsList[j].ToLower() == globalInit.Code.Name.Content.ToLower())
                {
                    codeToDump.Add(globalInit.Code.Name.Content);
                }
            }
            foreach (UndertaleCode code in Data.Code)
            {
                if (code is null)
                    continue;
                if (splitStringsList[j].ToLower() == code.Name.Content.ToLower())
                {
                    codeToDump.Add(code.Name.Content);
                }
            }
        }

        for (var j = 0; j < gameObjectCandidates.Count; j++)
        {
            try
            {
                UndertaleGameObject obj = Data.GameObjects.ByName(gameObjectCandidates[j]);
                for (var i = 0; i < obj.Events.Count; i++)
                {
                    foreach (UndertaleGameObject.Event evnt in obj.Events[i])
                    {
                        foreach (UndertaleGameObject.EventAction action in evnt.Actions)
                        {
                            if (action.CodeId?.Name?.Content != null)
                                codeToDump.Add(action.CodeId?.Name?.Content);
                        }
                    }
                }
            }
            catch
            {
                // Just keep going.
            }
        }

        SetProgressBar(null, "Code Entries", 0, codeToDump.Count);

        await Task.Run(() =>
        {
            for (var j = 0; j < codeToDump.Count; j++)
            {
                DumpCodeInner(Data.Code.ByName(codeToDump[j]));
            }
        });

        HideProgressBar();
        if (failed > 0)
            ScriptWarning($"{failed} code entries failed to decompile.");

        void DumpCodeInner(UndertaleCode code)
        {
            if (code is null)
                return;
            string path = Paths.JoinVerifyWithinDirectory(codeFolder, code.Name.Content + ".gml");
            if (code.ParentEntry == null)
            {
                try
                {
                    File.WriteAllText(path, new DecompileContext(globalDecompileContext, code, decompilerSettings).DecompileToString());
                }
                catch (Exception e)
                {
                    if (!Directory.Exists(Path.Join(codeFolder, "Failed")))
                    {
                        Directory.CreateDirectory(Path.Join(codeFolder, "Failed"));
                    }
                    path = Paths.JoinVerifyWithinDirectory(codeFolder, "Failed", code.Name.Content + ".gml");
                    File.WriteAllText(path, "/*\nDECOMPILER FAILED!\n\n" + e.ToString() + "\n*/");
                    failed += 1;
                }
            }
            else
            {
                File.WriteAllText(path, "/*\nDECOMPILER FAILED!\n\nFrom DumpSpecificCode script: cannot decompile sub-code entries individually.\n*/");
                failed += 1;
            }
            IncrementProgress();
        }
    }

    /// <summary>Exports all sprites as PNG files (optionally padded / in subdirectories).</summary>
    public async void ExportAllSprites()
    {
        EnsureDataLoaded();

        string texFolder = PromptChooseDirectory();
        if (texFolder is null)
            return;

        bool padded = ScriptQuestion("Export sprites with padding?");
        bool useSubDirectories = ScriptQuestion("Export sprites into subdirectories?");

        ConcurrentDictionary<string, ConcurrentBag<TextureToExport>> texturesToExport = new();

        SetProgressBar(null, "Generating Cache", 0, Data.Sprites.Count);

        await Task.Run(() => Parallel.ForEach(Data.Sprites, spr =>
        {
            FetchTexturesFromSprite(spr);
        }));

        HideProgressBar();

        SetProgressBar(null, "Exporting Texture Pages", 0, texturesToExport.Count);

        await Task.Run(() => ExportTextures());

        HideProgressBar();

        void FetchTexturesFromSprite(UndertaleSprite sprite)
        {
            if (sprite is not { SSpriteType: UndertaleSprite.SpriteType.Normal, Textures.Count: > 0 })
            {
                IncrementProgressParallel();
                return;
            }

            string outputFolder = texFolder;
            if (useSubDirectories)
            {
                outputFolder = Paths.JoinVerifyWithinDirectory(outputFolder, sprite.Name.Content);
                Directory.CreateDirectory(outputFolder);
            }

            for (int i = 0; i < sprite.Textures.Count; i++)
            {
                if (sprite.Textures[i]?.Texture is not null)
                {
                    UndertaleTexturePageItem pageItem = sprite.Textures[i].Texture;

                    var bag = texturesToExport.GetOrAdd(pageItem.TexturePage.Name.Content, _ => new ConcurrentBag<TextureToExport>());
                    bag.Add(new TextureToExport(pageItem, Paths.JoinVerifyWithinDirectory(outputFolder, $"{sprite.Name.Content}_{i}.png")));
                }
            }
            IncrementProgressParallel();
        }

        void ExportTextures()
        {
            int totalCores = Environment.ProcessorCount;
            int outerLimit = Math.Max(1, totalCores / 4);
            Parallel.ForEach(texturesToExport, new ParallelOptions { MaxDegreeOfParallelism = outerLimit }, kvp =>
            {
                using TextureWorker localWorker = new();
                foreach (TextureToExport tte in kvp.Value)
                {
                    localWorker.ExportAsPNG(tte.PageItem, tte.FileExportLocation, null, padded);
                }
                IncrementProgressParallel();
            });
        }
    }

    class TextureToExport
    {
        public UndertaleTexturePageItem PageItem { get; set; }
        public UndertaleEmbeddedTexture Page => PageItem.TexturePage;
        public string FileExportLocation { get; set; }

        public TextureToExport(UndertaleTexturePageItem pageItem, string fileExportLocation) => (PageItem, FileExportLocation) = (pageItem, fileExportLocation);
    }

    /// <summary>Exports specific sprites (by typed names) as PNG files.</summary>
    public async void ExportSpecificSprites()
    {
        EnsureDataLoaded();

        string texFolder = PromptChooseDirectory();
        if (texFolder is null)
            return;

        bool padded = ScriptQuestion("Export sprites with padding?");

        List<UndertaleSprite> spritesToDump = new();
        List<string> splitStringsList = new();

        string inputtedText = SimpleTextInput("Menu", "Enter the name of the sprites", "", true) ?? "";
        string[] individualLineArray = inputtedText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var oneLine in individualLineArray)
        {
            splitStringsList.Add(oneLine.Trim());
        }
        foreach (string listElement in splitStringsList)
        {
            foreach (UndertaleSprite spr in Data.Sprites)
            {
                if (spr is null)
                    continue;
                if (listElement.Equals(spr.Name.Content, StringComparison.InvariantCultureIgnoreCase))
                {
                    spritesToDump.Add(spr);
                }
            }
        }

        SetProgressBar(null, "Sprites", 0, spritesToDump.Count);

        using TextureWorker worker = new();
        await Task.Run(() =>
        {
            foreach (UndertaleSprite sprToDump in spritesToDump)
            {
                for (int i = 0; i < sprToDump.Textures.Count; i++)
                {
                    if (sprToDump.Textures[i]?.Texture is not null)
                    {
                        worker.ExportAsPNG(sprToDump.Textures[i].Texture, Paths.JoinVerifyWithinDirectory(texFolder, $"{sprToDump.Name.Content}_{i}.png"), null, padded);
                    }
                }
                IncrementProgress();
            }
        });

        HideProgressBar();
    }

    /// <summary>Exports all textures to a folder, grouped into Sprites/Fonts/Backgrounds subfolders.</summary>
    public async void ExportAllTextures()
    {
        EnsureDataLoaded();

        string texFolder = PromptChooseDirectory();
        if (texFolder is null)
            return;

        string sprFolder = Path.Join(texFolder, "Sprites");
        Directory.CreateDirectory(sprFolder);
        string fntFolder = Path.Join(texFolder, "Fonts");
        Directory.CreateDirectory(fntFolder);
        string bgrFolder = Path.Join(texFolder, "Backgrounds");
        Directory.CreateDirectory(bgrFolder);

        SetProgressBar(null, "Textures", 0, Data.TexturePageItems.Count);

        using TextureWorker worker = new();
        await Task.Run(() => Parallel.ForEach(Data.Sprites, sprite =>
        {
            if (sprite is null)
                return;

            for (int i = 0; i < sprite.Textures.Count; i++)
            {
                if (sprite.Textures[i]?.Texture is not null)
                {
                    UndertaleTexturePageItem tex = sprite.Textures[i].Texture;
                    worker.ExportAsPNG(tex, Paths.JoinVerifyWithinDirectory(sprFolder, $"{sprite.Name.Content}_{i}.png"));
                }
            }

            AddProgressParallel(sprite.Textures.Count);
        }));

        await Task.Run(() => Parallel.ForEach(Data.Fonts, font =>
        {
            if (font?.Texture is null)
                return;

            UndertaleTexturePageItem tex = font.Texture;
            worker.ExportAsPNG(tex, Paths.JoinVerifyWithinDirectory(fntFolder, $"{font.Name.Content}_0.png"));

            IncrementProgressParallel();
        }));

        await Task.Run(() => Parallel.ForEach(Data.Backgrounds, background =>
        {
            if (background?.Texture is null)
                return;

            UndertaleTexturePageItem tex = background.Texture;
            worker.ExportAsPNG(tex, Paths.JoinVerifyWithinDirectory(bgrFolder, $"{background.Name.Content}_0.png"));

            IncrementProgressParallel();
        }));

        HideProgressBar();
    }

    /// <summary>Exports all textures to a folder, each asset into its own subfolder.</summary>
    public async void ExportAllTexturesGrouped()
    {
        EnsureDataLoaded();

        string texFolder = PromptChooseDirectory();
        if (texFolder is null)
            return;

        string sprFolder = Path.Join(texFolder, "Sprites");
        Directory.CreateDirectory(sprFolder);
        string fntFolder = Path.Join(texFolder, "Fonts");
        Directory.CreateDirectory(fntFolder);
        string bgrFolder = Path.Join(texFolder, "Backgrounds");
        Directory.CreateDirectory(bgrFolder);

        SetProgressBar(null, "Textures", 0, Data.TexturePageItems.Count);

        using TextureWorker worker = new();
        await Task.Run(() => Parallel.ForEach(Data.Sprites, sprite =>
        {
            if (sprite is null)
                return;

            for (int i = 0; i < sprite.Textures.Count; i++)
            {
                if (sprite.Textures[i]?.Texture is not null)
                {
                    UndertaleTexturePageItem tex = sprite.Textures[i].Texture;
                    string sprFolder2 = Paths.JoinVerifyWithinDirectory(sprFolder, sprite.Name.Content);
                    Directory.CreateDirectory(sprFolder2);
                    worker.ExportAsPNG(tex, Paths.JoinVerifyWithinDirectory(sprFolder2, $"{sprite.Name.Content}_{i}.png"));
                }
            }

            AddProgressParallel(sprite.Textures.Count);
        }));

        await Task.Run(() => Parallel.ForEach(Data.Fonts, font =>
        {
            if (font?.Texture is null)
                return;

            UndertaleTexturePageItem tex = font.Texture;
            string fntFolder2 = Paths.JoinVerifyWithinDirectory(fntFolder, font.Name.Content);
            Directory.CreateDirectory(fntFolder2);
            worker.ExportAsPNG(tex, Paths.JoinVerifyWithinDirectory(fntFolder2, $"{font.Name.Content}_0.png"));
            IncrementProgressParallel();
        }));

        await Task.Run(() => Parallel.ForEach(Data.Backgrounds, background =>
        {
            if (background?.Texture is null)
                return;

            UndertaleTexturePageItem tex = background.Texture;
            string bgrFolder2 = Paths.JoinVerifyWithinDirectory(bgrFolder, background.Name.Content);
            Directory.CreateDirectory(bgrFolder2);
            worker.ExportAsPNG(tex, Paths.JoinVerifyWithinDirectory(bgrFolder2, $"{background.Name.Content}_0.png"));
            IncrementProgressParallel();
        }));

        HideProgressBar();
    }

    /// <summary>Exports all tilesets (backgrounds) as PNG files.</summary>
    public async void ExportAllTilesets()
    {
        EnsureDataLoaded();

        string texFolder = PromptChooseDirectory();
        if (texFolder is null)
            return;

        SetProgressBar(null, "Tilesets", 0, Data.Backgrounds.Count);

        using TextureWorker worker = new();
        await Task.Run(() => Parallel.ForEach(Data.Backgrounds, tileset =>
        {
            if (tileset?.Texture is not null)
            {
                worker.ExportAsPNG(tileset.Texture, Paths.JoinVerifyWithinDirectory(texFolder, $"{tileset.Name.Content}.png"));
            }

            IncrementProgressParallel();
        }));

        HideProgressBar();
    }

    /// <summary>Exports all sprite collision masks as PNG files.</summary>
    public async void ExportAllMasks()
    {
        EnsureDataLoaded();

        string texFolder = PromptChooseDirectory();
        if (texFolder is null)
            return;

        SetProgressBar(null, "Sprite masks", 0, Data.Sprites.Count);

        await Task.Run(() => Parallel.ForEach(Data.Sprites, sprite =>
        {
            if (sprite is null)
                return;

            for (int i = 0; i < sprite.CollisionMasks.Count; i++)
            {
                if (sprite.CollisionMasks[i]?.Data is not null)
                {
                    (int maskWidth, int maskHeight) = sprite.CalculateMaskDimensions(Data);
                    TextureWorker.ExportCollisionMaskPNG(sprite.CollisionMasks[i], Paths.JoinVerifyWithinDirectory(texFolder, $"{sprite.Name.Content}_{i}.png"), maskWidth, maskHeight);
                }
            }

            IncrementProgressParallel();
        }));

        HideProgressBar();
    }
}