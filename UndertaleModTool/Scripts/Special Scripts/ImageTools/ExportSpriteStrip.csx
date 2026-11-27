// ==UndertaleModTool==
// @name        Export Sprite Strip
// @name:zh-Hans 导出精灵条带
// @name:zh-CN  导出精灵条带
// ==/UndertaleModTool==
/*
	ExportSpriteStrip.csx
		by burnedpopcorn180, with parts based off of ExportSpritesAsGIFDLL.csx by CST1229
	
    Exports Sprite Frames into a Single Sprite Strip
	older versions of UTMT won't work, because of new TextureWorker and ImageMagick
 */

using QiuForms;

using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UndertaleModLib.Util;
using ImageMagick;

EnsureDataLoaded();

ScriptMessage("Please Select an Output Directory");
string folder = PromptChooseDirectory();
if (folder == null)
    return;
folder = Path.GetDirectoryName(folder);

List<string> Selected_Sprites = new List<string>();

#region UI

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

await ExtractSprites(folder, Selected_Sprites);

private async Task ExtractSprites(string folder, List<string> Selected_Sprites)
{
    TextureWorker worker = new TextureWorker();
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

        SetProgressBar(null, "Exporting Sprites to Sprite Strips...", 0, sprites.Count);
        StartProgressBarUpdater();
		
		// run the actual exporting function
        await Task.Run(() => Parallel.ForEach(sprites, (sprite) => {
            IncrementProgressParallel();
            ExtractSprite(sprite, folder, worker);
        }));
		
        await StopProgressBarUpdater();
        HideProgressBar();
		
		ScriptMessage($"Sprite Strips exported to: \n{folder}");
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
	// get initial frame width
    int initWidth = (int)sprite.Textures[0].Texture.BoundingWidth;
    
    // get the final width of the sprite strip
    int totalWidth = initWidth * sprite.Textures.Count;  // original frame times the amount of all frames
	
	// get the final height of the sprite strip
    int totalHeight = (int)sprite.Textures[0].Texture.BoundingHeight; // this doesn't/shouldn't change
    
	// make image
	// fucking why 0.7.0.0
	var settings = new MagickReadSettings
	{
        BackgroundColor = MagickColors.Transparent,
        Width = (uint)totalWidth,
        Height = (uint)totalHeight
    };
    MagickImage spriteStrip = new MagickImage("xc:none", settings);
    
    // add all frames
    for (int i = 0; i < sprite.Textures.Count; i++)
    {
        // get texture for this frame
        IMagickImage<byte> image = worker.GetTextureFor(sprite.Textures[i].Texture, sprite.Name.Content, true);
        MagickImage spriteFrame = new MagickImage(image);
        
        // set the position of this frame
        int xOffset = i * initWidth;
		// add Frame to PNG
        spriteStrip.Composite(spriteFrame, xOffset, 0, CompositeOperator.Over);
    }

    // Save as PNG
    string spriteStripPath = $"{folder}/{sprite.Name.Content}_strip{sprite.Textures.Count}.png";
    spriteStrip.Write(spriteStripPath);
}