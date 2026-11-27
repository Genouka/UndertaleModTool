// ==UndertaleModTool==
// @name        Better Export Sprites as GIF
// @name:zh-Hans 更好的精灵 GIF 导出
// @name:zh-CN  更好的精灵 GIF 导出
// ==/UndertaleModTool==
/*
    BetterExportSpritesAsGIF.csx
		by burnedpopcorn180, with parts based off of ExportSpritesAsGIFDLL.csx by CST1229

    Exports Sprites as a GIF, but without an external DLL, and with a handy dandy UI
	older versions of UTMT won't work, because of new TextureWorker and ImageMagick
 */

using QiuForms;

using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UndertaleModLib.Util;
using ImageMagick;

EnsureDataLoaded();
TextureWorker worker = new TextureWorker();

ScriptMessage("Please Select an Output Directory");
string folder = PromptChooseDirectory();
if (folder == null)
    return;
folder = Path.GetDirectoryName(folder);

List<string> Selected_Sprites = new List<string>();
public bool custom_fpsENABLED = true;
public int custom_fps = 24;

private int currentFrameIndex = 0;

#region Main UI

QiuForm form = new("Sprite Selector");

QiuPanel columns = form.Panel(QiuOrientation.Horizontal);

QiuPanel left = columns.Panel(QiuOrientation.Vertical);
QiuTextBox searchbar = left.Text("", "", id: "search");
searchbar.ShowLabel = false;
searchbar.Placeholder = "Search sprites";
QiuTreeView treeView = left.Tree("Sprites", height: 320, width: 220, id: "tree");

QiuPanel middle = columns.Panel(QiuOrientation.Vertical);
middle.Button("->", _ => AddSelected(treeView.SelectedText));
middle.Button("<-", _ =>
{
    if (listbox.SelectedItem is not null)
        Selected_Sprites.RemoveAll(r => r == listbox.SelectedItem);
    RefreshList();
});

QiuPanel right = columns.Panel(QiuOrientation.Vertical);
QiuTextBox searchbar2 = right.Text("", "", id: "search2");
searchbar2.ShowLabel = false;
searchbar2.Placeholder = "Search selection";
QiuListBox listbox = right.List(height: 320, width: 220, id: "selected");

QiuCheckBox useGameFps = form.Check("Use In-Game FPS", false, id: "useGameFps");
QiuSlider fps = form.Slider("FPS", 24, minimum: 1, maximum: 60, id: "fps");
fps.EnabledWhen(() => !useGameFps.Value);

void AddSelected(string name)
{
    if (name is not null && !Selected_Sprites.Contains(name))
        Selected_Sprites.Add(name);
    RefreshList();
}

void RefreshTree() =>
    treeView.SetItems(Data.Sprites
        .Where(sprite => sprite.Name.Content.Contains(searchbar.Value))
        .Select(sprite => sprite.Name.Content));

void RefreshList() =>
    listbox.SetItems(Selected_Sprites.Where(name => name.Contains(searchbar2.Value)));

searchbar.Changed += _ => RefreshTree();
searchbar2.Changed += _ => RefreshList();
treeView.ItemActivated += _ => AddSelected(treeView.SelectedText);
listbox.ItemActivated += _ =>
{
    if (listbox.SelectedItem is not null)
        Selected_Sprites.RemoveAll(r => r == listbox.SelectedItem);
    RefreshList();
};

RefreshTree();
RefreshList();

#endregion

if (ShowQiuForm(form) != QiuDialogResult.Submit)
    return;

custom_fpsENABLED = !useGameFps.Value;
custom_fps = fps.Value;

await ExtractSprites(folder, Selected_Sprites);

private async Task ExtractSprites(string folder, List<string> Selected_Sprites)
{
    //TextureWorker worker = new TextureWorker();
    try
    {
        IList<UndertaleSprite> sprites = Data.Sprites;
        if (Selected_Sprites.Count > 0)
        {
            // init list with all desired sprites to be exported
            sprites = new List<UndertaleSprite> { };
            foreach (UndertaleSprite sprite in Data.Sprites)
            {
                if (Selected_Sprites.Contains(sprite.Name.Content))
                {
                    // if sprite exists, add to list that will be exported
                    sprites.Add(sprite);
                }
            }
        }

        SetProgressBar(null, "Exporting Sprites to GIF...", 0, sprites.Count);
        StartProgressBarUpdater();
		
		// run the actual exporting function
        await Task.Run(() => Parallel.ForEach(sprites, (sprite) => {
            IncrementProgressParallel();
            ExtractSprite(sprite, folder, worker);
        }));
		
        await StopProgressBarUpdater();
        HideProgressBar();

        ScriptMessage($"Sprite GIFs exported to: \n{folder}");
    }
    catch (Exception e)
    {
        throw;
    }
    finally
    {
		// for newer UTMT/UA Releases
		worker.Dispose();
    }
}

private void ExtractSprite(UndertaleSprite sprite, string folder, TextureWorker worker)
{
    // List to hold the frames
    List<MagickImage> frames = new List<MagickImage>();

    // For every frame in sprite
    for (int i = 0; i < sprite.Textures.Count; i++)
    {
        // Create a new blank frame
		// fucking why 0.7.0.0
		var settings = new MagickReadSettings
        {
            BackgroundColor = MagickColors.Transparent,
            Width = (uint)sprite.Textures[i].Texture.BoundingWidth,
            Height = (uint)sprite.Textures[i].Texture.BoundingHeight
        };
        MagickImage frame = new MagickImage("xc:none", settings);
        
        // Get texture for this frame
        IMagickImage<byte> image = worker.GetTextureFor(sprite.Textures[i].Texture, sprite.Name.Content, true);
        MagickImage spriteFrame = new MagickImage(image);
        frame.Composite(spriteFrame, CompositeOperator.Over);

        // Set the playback speed
        if (!custom_fpsENABLED)
            // If using In-Game Time
            frame.AnimationDelay = (uint)(100 / sprite.GMS2PlaybackSpeed);
        else
            // If using Custom Time
            frame.AnimationDelay = (uint)(100 / custom_fps);

        // Add the frame to the list
        frames.Add(frame);
    }

    // Create the GIF from the frames
    using (MagickImageCollection collection = new MagickImageCollection())
    {
        // Add all frames
        foreach (var frame in frames)
        {
            collection.Add(frame);
			
			// very important as it stops smearing/ghosting
			frame.GifDisposeMethod = GifDisposeMethod.Background;
        }
        
        // Save GIF
        collection.Write(folder + "/" + sprite.Name.Content + ".gif");
    }
}