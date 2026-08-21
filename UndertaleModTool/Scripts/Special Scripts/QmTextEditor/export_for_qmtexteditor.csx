//=====================================================================
// export_for_qmtexteditor.csx
//---------------------------------------------------------------------
// Merged exporter that regenerates a QM Text Editor style "project" folder.
// Combines four scripts (in order):
//   1. ExportAssetOrder.csx        -> assets_order.txt
//   2. RoomDecompiler.csx          -> rooms/<Room>/<Room>.yy (+ creation code .gml)
//   3. ExportAllSprites_copy1.csx  -> sprite/<Sprite>/<Sprite>_<frame>.png + .json
//   4. ExportAllCode.csx           -> code/<Entry>.gml
//
// Resulting structure (see the reference "project" folder):
//   <root>/
//   ├── assets_order.txt
//   ├── code/            *.gml
//   ├── objects/         *.yy
//   ├── rooms/           <Room>/<Room>.yy (+ RoomCreationCode.gml, InstanceCreationCode_inst_*.gml)
//   └── sprite/          <Sprite>/<Sprite>_<frame>.png + <Sprite>_<frame>.json
//
// Dialogs:
//   * Clear existing code/sprite/rooms subfolders?   -> recommended Yes
//   * Export sprites with padding?                   -> recommended No
//   * Export sprites into subdirectories?            -> recommended Yes
//
// The output root is fixed to "<data.win folder>/project".
//=====================================================================

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Text.Json;
using Newtonsoft.Json;
using UndertaleModLib.Util;
using UndertaleModLib.Models;
using Json = System.Text.Json.JsonSerializer;

EnsureDataLoaded();

//---------------------------------------------------------------------
// 0. Output root: fixed to a "project" folder next to the game file
//---------------------------------------------------------------------
string projectFolder = Paths.JoinVerifyWithinDirectory(Path.GetDirectoryName(FilePath), "project");

string codeFolder   = Paths.JoinVerifyWithinDirectory(projectFolder, "code");
string spriteFolder = Paths.JoinVerifyWithinDirectory(projectFolder, "sprite");
string objectFolder = Paths.JoinVerifyWithinDirectory(projectFolder, "objects");
string roomFolder   = Paths.JoinVerifyWithinDirectory(projectFolder, "rooms") + Path.DirectorySeparatorChar;
string orderFile    = Paths.JoinVerifyWithinDirectory(projectFolder, "assets_order.txt");

// Regenerate everything from scratch (the original RoomDecompiler also deleted
// its own output folder before writing a fresh one).
bool clearExisting = ScriptQuestion("Clear existing code/sprite/rooms subfolders before exporting? (recommended: Yes)");
if (clearExisting)
{
    if (Directory.Exists(codeFolder))   Directory.Delete(codeFolder, true);
    if (Directory.Exists(spriteFolder)) Directory.Delete(spriteFolder, true);
    if (Directory.Exists(objectFolder)) Directory.Delete(objectFolder, true);
    if (Directory.Exists(roomFolder))   Directory.Delete(roomFolder, true);
    if (File.Exists(orderFile))         File.Delete(orderFile);
}
Directory.CreateDirectory(codeFolder);
Directory.CreateDirectory(spriteFolder);
Directory.CreateDirectory(objectFolder);
Directory.CreateDirectory(roomFolder);

// Sprite options (two dialogs, same as ExportAllSprites_copy1.csx).
bool padded = ScriptQuestion("Export sprites with padding? (recommended: No)");
bool useSubDirectories = ScriptQuestion("Export sprites into subdirectories? (recommended: Yes)");

//---------------------------------------------------------------------
// 1. Asset order (ExportAssetOrder.csx)
//---------------------------------------------------------------------
void WriteAssetNames<T>(StreamWriter writer, IList<T> assets) where T : UndertaleNamedResource
{
    if (assets.Count == 0)
        return;
    foreach (var asset in assets)
    {
        if (asset is not null)
            writer.WriteLine(asset.Name?.Content ?? assets.IndexOf(asset).ToString());
        else
            writer.WriteLine("(null)");
    }
}

using (StreamWriter writer = new StreamWriter(orderFile))
{
    // Write Sounds.
    writer.WriteLine("@@sounds@@");
    WriteAssetNames(writer, Data.Sounds);

    // Write Sprites.
    writer.WriteLine("@@sprites@@");
    WriteAssetNames(writer, Data.Sprites);

    // Write Backgrounds.
    writer.WriteLine("@@backgrounds@@");
    WriteAssetNames(writer, Data.Backgrounds);

    // Write Paths.
    writer.WriteLine("@@paths@@");
    WriteAssetNames(writer, Data.Paths);

    // Write Scripts.
    writer.WriteLine("@@scripts@@");
    WriteAssetNames(writer, Data.Scripts);

    // Write Fonts.
    writer.WriteLine("@@fonts@@");
    WriteAssetNames(writer, Data.Fonts);

    // Write Objects.
    writer.WriteLine("@@objects@@");
    WriteAssetNames(writer, Data.GameObjects);

    // Write Timelines.
    writer.WriteLine("@@timelines@@");
    WriteAssetNames(writer, Data.Timelines);

    // Write Rooms.
    writer.WriteLine("@@rooms@@");
    WriteAssetNames(writer, Data.Rooms);

    // Write Shaders.
    writer.WriteLine("@@shaders@@");
    WriteAssetNames(writer, Data.Shaders);

    // Write Extensions.
    writer.WriteLine("@@extensions@@");
    WriteAssetNames(writer, Data.Extensions);
}

//---------------------------------------------------------------------
// 2. Rooms (RoomDecompiler.csx)
//---------------------------------------------------------------------
// Made with the help of QuantumV and The United Modders Of Pizza Tower Team
public class AssetReference
{
    public string name { get; set; }
    public string path { get; set; }

	public static AssetReference Create(UndertaleNamedResource obj, UndertaleNamedResource pathObj, string folderName) {
		if (obj == null) return null;
		return new AssetReference {
			name = obj.Name.Content,
			path = $"{folderName}/{pathObj.Name.Content}/{pathObj.Name.Content}.yy"
		};
	}
}

public class GMRView {
	public bool inherit = false;
	public bool visible = true;
	public int xview = 0;
	public int yview = 0;
	public int wview = 1920;
	public int hview = 1080;
	public int xport = 0;
	public int yport = 0;
	public int wport = 1920;
	public int hport = 1080;
	public AssetReference objectId = null;
	public uint hborder = 32;
	public uint vborder = 32;
	public int hspeed = -1;
	public int vspeed = -1;
}

public class GMRLayer {
	public string resourceType;
	public string resourceVersion;
	public string name = "";
	public bool visible = true;
	public float depth = 0;
	public bool userdefinedDepth = false;
	public bool inheritLayerDepth = false;
	public bool inheritLayerSettings = false;
	public double gridX = 32;
	public double gridY = 32;
	public List<GMRLayer> layers = new List<GMRLayer>();

	public bool hierarchyFrozen = false;
	public bool effectEnabled = true;
	public string effectType = null;
	public List<GMREffectProperty> properties = new List<GMREffectProperty>();
}

public class GMRAssetLayer : GMRLayer {
	public string resourceType = "GMRAssetLayer";
	public string resourceVersion = "1.0";
	public List<GMRAsset> assets = new List<GMRAsset>();
}

public class GMRAsset {
	public string resourceType;
	public string resourceVersion;
	// probably???
	public AssetReference inheritedItemId = null;
	public bool frozen = false;
	public bool ignore = false;
	public bool inheritItemSettings = false;
}

public class GMRSpriteGraphic : GMRAsset {
	public string resourceType = "GMRSpriteGraphic";
	public string resourceVersion = "1.0";
	public string name = "";
	public AssetReference spriteId = null;
	public float headPosition = 0f;
	public float rotation = 0f;
	public float scaleX = 1f;
	public float scaleY = 1f;
	public float animationSpeed = 1f;
	public uint colour = 0xffffffff;
	public float x = 0f;
	public float y = 0f;
}

// legacy tiles
public class GMRGraphic : GMRAsset {
	public string resourceType = "GMRGraphic";
	public string resourceVersion = "1.0";
	public string name = "";
	public AssetReference spriteId = null;
	public int x = 0;
	public int y = 0;
	public float w = 0;
	public float h = 0;
	public float u0 = 0;
	public float v0 = 0;
	public float u1 = 0;
	// ultrakill refere
	public float v1 = 0;
	public uint colour = 0xffffffff;
	public List<string> tags = new List<string>();
}

public class GMRPathLayer : GMRLayer {
	public string resourceType = "GMRPathLayer";
	public string resourceVersion = "1.0";
	public AssetReference pathId = null;
	public uint colour = 0xffffffff;
}

public class GMRTileLayer : GMRLayer {
	public string resourceType = "GMRTileLayer";
	public string resourceVersion = "1.1";
	public AssetReference tilesetId = null;
	public float x = 0f;
	public float y = 0f;
	public GMRTileData tiles = null;
}

public class GMRTileData {
	public uint SerialiseWidth = 0;
	public uint SerialiseHeight = 0;
	public List<int> TileSerialiseData = new List<int>();
}

public class GMREffectProperty {
	public uint type = 0;
	public string name;
	public string value;
}

public class GMRInstanceLayer : GMRLayer {
	public string resourceType = "GMRInstanceLayer";
	public string resourceVersion = "1.0";
	public List<GMRInstance> instances = new List<GMRInstance>();
}

public class GMRInstance {
	public string resourceType = "GMRInstance";
	public string resourceVersion = "1.0";
	public string name;
	// overridden variables
	// this isn't in utmt's gui, so it's probably
	// compiled into the creation code
	public List<object> properties = new List<object>();
	public bool isDnd = false;
	public AssetReference objectId = null;
	public bool inheritCode = false;
	public bool hasCreationCode = false;
	public uint colour = 0xffffffff;
	public float rotation = 0f;
	public float scaleX = 1f;
	public float scaleY = 1f;
	public int imageIndex = 0;
	public float imageSpeed = 0f;
	// probably???
	public AssetReference inheritedItemId = null;
	public bool frozen = false;
	public bool ignore = false;
	public bool inheritItemSettings = false;
	public int x = 0;
	public int y = 0;
}

public class GMRBackgroundLayer : GMRLayer {
	public string resourceType = "GMRBackgroundLayer";
	public string resourceVersion = "1.0";
	public AssetReference spriteId = null;
	public uint colour = 0xffffffff;
	public float x = 0;
	public float y = 0;
	public bool htiled = false;
	public bool vtiled = false;
	public float hspeed = 0f;
	public float vspeed = 0f;
	public bool stretch = false;
	public float animationFPS = 15f;
	public uint animationSpeedType = 0;
	public bool userdefinedAnimFPS = false;
}

public class GMREffectLayer : GMRLayer {
	public string resourceType = "GMREffectLayer";
	public string resourceVersion = "1.0";
}

public class GMRoom {
	public string resourceType = "GMRoom";
	public string resourceVersion = "1.0";
	public string name;
	public bool isDnd = false;
	public float volume = 1f;
	// probably???
	public AssetReference parentRoom = null;
	public List<GMRView> views = new List<GMRView>();
	public List<GMRLayer> layers = new List<GMRLayer>();
	public bool inheritLayers = false;
	public string creationCodeFile = "";
	public bool inheritCode = false;
	public List<AssetReference> instanceCreationOrder = new List<AssetReference>();
	public bool inheritCreationOrder = false;
	public AssetReference sequenceId = null;
	public GMRoomSettings roomSettings = new GMRoomSettings();
	public GMViewSettings viewSettings = new GMViewSettings();
	public GMPhysicsSettings physicsSettings = new GMPhysicsSettings();
	public AssetReference parent = null; 
}

public class GMRoomSettings {
	public bool inheritRoomSettings = false;
	public uint Width = 1920;
	public uint Height = 1080;
	public bool persistent = false;
}
public class GMViewSettings {
	public bool inheritViewSettings = false;
	public bool enableViews = false;
	public bool clearViewBackground = false;
	public bool clearDisplayBuffer = false;
}
public class GMPhysicsSettings {
	public bool inheritPhysicsSettings = false;
	public bool PhysicsWorld = false;
	public float PhysicsWorldGravityX = 0f;
	public float PhysicsWorldGravityY = 10f;
	public float PhysicsWorldPixToMetres = 0.1f;
}

void ApplyCommonLayerData(GMRLayer layerData, UndertaleRoom.Layer layer, UndertaleRoom room) {
	layerData.name = layer.LayerName.Content;
	layerData.visible = layer.IsVisible;
	layerData.depth = layer.LayerDepth;
	layerData.effectEnabled = layer.EffectEnabled;
	layerData.effectType = layer.EffectType?.Content;
	foreach (UndertaleRoom.EffectProperty property in layer.EffectProperties) {
		layerData.properties.Add(new GMREffectProperty{
			type = (uint)property.Kind,
			name = property.Name.Content,
			value = property.Value.Content
		});
	}
	layerData.userdefinedDepth = true;
	layerData.gridX = room.GridWidth;
	layerData.gridY = room.GridHeight;
}

ThreadLocal<GlobalDecompileContext> DECOMPILE_CONTEXT = new ThreadLocal<GlobalDecompileContext>(() => new GlobalDecompileContext(Data));

SetProgressBar(null, "Rooms", 0, Data.Rooms.Count);
StartProgressBarUpdater();
await Task.Run(() => Parallel.ForEach(Data.Rooms, (UndertaleRoom room) => {
    string roomDir = roomFolder + room.Name.Content + Path.DirectorySeparatorChar;
    Directory.CreateDirectory(roomDir);

	List<GMRView> viewsData = new List<GMRView>();
	foreach (UndertaleRoom.View view in room.Views) {
		viewsData.Add(new GMRView {
			visible = view.Enabled,
			xview = view.ViewX,
			yview = view.ViewY,
			wview = view.ViewWidth,
			hview = view.ViewHeight,
			xport = view.PortX,
			yport = view.PortY,
			wport = view.PortWidth,
			hport = view.PortHeight,
			objectId = AssetReference.Create(view.ObjectId, view.ObjectId, "objects"),
			hborder = view.BorderX,
			vborder = view.BorderY,
			hspeed = view.SpeedX,
			vspeed = view.SpeedY
		});
	}
	List<AssetReference> orderData = new List<AssetReference>();
	List<GMRLayer> layersData = new List<GMRLayer>();
	foreach (UndertaleRoom.Layer layer in room.Layers) {
		switch (layer.LayerType) {
			case UndertaleRoom.LayerType.Path:
				// no data apparently
				continue;
			case UndertaleRoom.LayerType.Background: {
				GMRBackgroundLayer layerData = new GMRBackgroundLayer();
				layerData.x = layer.XOffset;
				layerData.y = layer.YOffset;
				layerData.hspeed = layer.HSpeed;
				layerData.vspeed = layer.VSpeed;

				layerData.spriteId = AssetReference.Create(
					layer.BackgroundData.Sprite, layer.BackgroundData.Sprite,
					"sprites"
				);
				layerData.htiled = layer.BackgroundData.TiledHorizontally;
				layerData.vtiled = layer.BackgroundData.TiledVertically;
				layerData.stretch = layer.BackgroundData.Stretch;
				// bri'ish vs. americ'n
				layerData.colour = layer.BackgroundData.Color;

				layerData.animationFPS = layer.BackgroundData.AnimationSpeed;
				layerData.animationSpeedType = (uint)layer.BackgroundData.AnimationSpeedType;
				layerData.userdefinedAnimFPS = true;
				ApplyCommonLayerData(layerData, layer, room);
				layersData.Add(layerData);
				} break;
			case UndertaleRoom.LayerType.Instances: {
				GMRInstanceLayer layerData = new GMRInstanceLayer();

				// not UndertaleGameObject, GameObject. confusing, right?
				foreach (UndertaleRoom.GameObject obj in layer.InstancesData.Instances) {
					string instName = $"inst_{obj.InstanceID}";
					if (obj.CreationCode != null) {
						var code = obj.CreationCode;
						var gmlPath = $"{roomDir}InstanceCreationCode_{instName}.gml";
						try
						{
							File.WriteAllText(gmlPath, (code != null ? ConvertEnumToConst(new Underanalyzer.Decompiler.DecompileContext(DECOMPILE_CONTEXT.Value,code).DecompileToString()) : ""));
						}
						catch (Exception e)
						{
							File.WriteAllText(gmlPath, "/*\nDECOMPILER FAILED!\n\n" + e.ToString() + "\n*/");
        				}
					}
					layerData.instances.Add(new GMRInstance{
						objectId = AssetReference.Create(
							obj.ObjectDefinition, obj.ObjectDefinition,
							"objects"
						),
						name = instName,
						x = obj.X,
						y = obj.Y,
						scaleX = obj.ScaleX,
						scaleY = obj.ScaleY,
						colour = obj.Color,
						rotation = obj.Rotation,
						hasCreationCode = obj.CreationCode != null,
						imageSpeed = obj.ImageSpeed,
						imageIndex = obj.ImageIndex
					});
					orderData.Add(new AssetReference{
						name = instName,
						path = $"rooms/{room.Name.Content}/{room.Name.Content}.yy"
					});
				}

				ApplyCommonLayerData(layerData, layer, room);
				layersData.Add(layerData);
				} break;
			case UndertaleRoom.LayerType.Assets: {
				GMRAssetLayer layerData = new GMRAssetLayer();
				foreach (UndertaleRoom.Tile asset in layer.AssetsData.LegacyTiles) {
					layerData.assets.Add(new GMRGraphic{
						name = $"inst_{asset.InstanceID}",
						spriteId = AssetReference.Create(
							asset.ObjectDefinition, asset.ObjectDefinition,
							"sprites"
						),
						x = asset.X,
						y = asset.Y,
						w = asset.Width * asset.ScaleX,
						h = asset.Height * asset.ScaleY,
						u0 = asset.SourceX,
						v0 = asset.SourceY,
						u1 = asset.SourceX + Convert.ToUInt32(asset.Width),
						v1 = asset.SourceY + Convert.ToUInt32(asset.Height),
					});
				}
				foreach (UndertaleRoom.SpriteInstance asset in layer.AssetsData.Sprites) {
					layerData.assets.Add(new GMRSpriteGraphic{
						name = asset.Name.Content,
						spriteId = AssetReference.Create(
							asset.Sprite, asset.Sprite,
							"sprites"
						),
						x = asset.X,
						y = asset.Y,
						scaleX = asset.ScaleX,
						scaleY = asset.ScaleY,
						colour = asset.Color,
						rotation = asset.Rotation,
						headPosition = asset.FrameIndex,
						animationSpeed = asset.AnimationSpeed
					});
				}
				ApplyCommonLayerData(layerData, layer, room);
				layersData.Add(layerData);
				} break;
			case UndertaleRoom.LayerType.Tiles: {
				GMRTileLayer layerData = new GMRTileLayer();
				layerData.x = layer.XOffset;
				layerData.y = layer.YOffset;

				layerData.tilesetId = AssetReference.Create(
					layer.TilesData.Background, layer.TilesData.Background,
					"tilesets"
				);

				layerData.tiles = new GMRTileData();
				layerData.tiles.SerialiseWidth = layer.TilesData.TilesX;
				layerData.tiles.SerialiseHeight = layer.TilesData.TilesY;
				foreach (uint[] tileRow in layer.TilesData.TileData) {
					foreach (uint tileId in tileRow) {
						int _tileId = (int)tileId;
						layerData.tiles.TileSerialiseData.Add(_tileId);
					}
				}

				ApplyCommonLayerData(layerData, layer, room);
				layersData.Add(layerData);
				} break;
			case UndertaleRoom.LayerType.Effect: {
				GMREffectLayer layerData = new GMREffectLayer();
				// no other data
				ApplyCommonLayerData(layerData, layer, room);
				layersData.Add(layerData);
				} break;
			default:
				throw new Exception($"Unknown layer type: {layer.LayerType}");
		}
	}

	if (room.CreationCodeId != null) {
		var code = room.CreationCodeId;
        var gmlPath = $"{roomDir}RoomCreationCode.gml";
        try
        {
            File.WriteAllText(gmlPath, (code != null ? ConvertEnumToConst(new Underanalyzer.Decompiler.DecompileContext(DECOMPILE_CONTEXT.Value,code).DecompileToString()) : ""));
        }
        catch (Exception e)
        {
            File.WriteAllText(gmlPath, "/*\nDECOMPILER FAILED!\n\n" + e.ToString() + "\n*/");
        }
	}
	GMRoom roomData = new GMRoom {
		name = room.Name.Content,
		creationCodeFile = room.CreationCodeId == null ? "" : $"${{project_dir}}\\rooms\\{room.Name.Content}\\RoomCreationCode.gml",
		views = viewsData,
		layers = layersData,
		instanceCreationOrder = orderData,
		roomSettings = new GMRoomSettings {
			Width = room.Width,
			Height = room.Height,
			persistent = room.Persistent
		},
		viewSettings = new GMViewSettings {
			enableViews = room.Flags.HasFlag(UndertaleRoom.RoomEntryFlags.EnableViews),
			clearViewBackground = room.DrawBackgroundColor,
			clearDisplayBuffer = room.DrawBackgroundColor
		},
		physicsSettings = new GMPhysicsSettings {
			PhysicsWorld = room.World,
			PhysicsWorldGravityX = room.GravityX,
			PhysicsWorldGravityY = room.GravityY,
			PhysicsWorldPixToMetres = room.MetersPerPixel
		},
        parent = new AssetReference()
        {
            name = "Rooms",
            path = "folders/Rooms.yy"
        }
	};

    string json = JsonConvert.SerializeObject(roomData, Formatting.Indented);
	json = ConvertEnumToConst(json);
    File.WriteAllText(roomDir + room.Name.Content + ".yy", json);

	IncrementProgressParallel();
}));
await StopProgressBarUpdater();
HideProgressBar();

public string ConvertEnumToConst(string inputCode)
{
    // 存储所有枚举定义和替换映射
    var enumDefinitions = new Dictionary<string, Dictionary<string, int>>();
    var replaceDict = new Dictionary<string, string>();
    var sbDefinitions = new StringBuilder();

    // 匹配所有枚举定义
    var enumRegex = new Regex(@"enum\s+(\w+)\s*\{(.*?)\}", RegexOptions.Singleline);
    var enumMatches = enumRegex.Matches(inputCode);

    foreach (Match enumMatch in enumMatches)
    {
        string enumName = enumMatch.Groups[1].Value;
        string enumContent = enumMatch.Groups[2].Value;
        var members = new Dictionary<string, int>();

        // 解析枚举成员（支持显式赋值）
        int currentValue = 0;
        var memberRegex = new Regex(@"\s*(\w+)\s*(=\s*(\d+))?\s*,?");
        var memberMatches = memberRegex.Matches(enumContent);

        foreach (Match memberMatch in memberMatches)
        {
            if (!memberMatch.Success) continue;

            string memberName = memberMatch.Groups[1].Value;
            string explicitValue = memberMatch.Groups[3].Value;

            if (!string.IsNullOrEmpty(explicitValue))
            {
                currentValue = int.Parse(explicitValue);
            }

            members[memberName] = currentValue;

            // 添加到替换字典
            string fullName = $"{enumName}.{memberName}";
            string replacement = $"global.{enumName}__{memberName}";
            replaceDict[fullName] = replacement;

            // 生成定义行
            sbDefinitions.AppendLine($"{replacement} = {currentValue};");

            currentValue++; // 为下一个成员递增
        }

        enumDefinitions[enumName] = members;
    }

    // 移除所有枚举定义
    string outputCode = enumRegex.Replace(inputCode, "");

    // 替换所有枚举引用
    foreach (var kvp in replaceDict)
    {
        outputCode = outputCode.Replace(kvp.Key, kvp.Value);
    }

    // 在代码开头插入定义
    return sbDefinitions.ToString() + outputCode;
}

//---------------------------------------------------------------------
// 3. Sprites (ExportAllSprites_copy1.csx)
//---------------------------------------------------------------------
ConcurrentDictionary<string, ConcurrentBag<TextureToExport>> texturesToExport = new();

SetProgressBar(null, "Generating Cache", 0, Data.Sprites.Count);
StartProgressBarUpdater();

await Task.Run(() => Parallel.ForEach(Data.Sprites, spr =>
{
    FetchTexturesFromSprite(spr);
}));

await StopProgressBarUpdater();
HideProgressBar();

SetProgressBar(null, "Exporting Texture Pages", 0, texturesToExport.Count);
StartProgressBarUpdater();

await Task.Run(() => ExportTextures());

await StopProgressBarUpdater();
HideProgressBar();

//---------------------------------------------------------------------
// 4. Objects
//---------------------------------------------------------------------

SetProgressBar(null, "Objects", 0, Data.GameObjects.Count);
StartProgressBarUpdater();

await Task.Run(() => ExportObjects());

await StopProgressBarUpdater();
HideProgressBar();

void FetchTexturesFromSprite(UndertaleSprite sprite)
{
    // Empty, null, or not a raster image? We can't do anything with it.
    if (sprite is not { SSpriteType: UndertaleSprite.SpriteType.Normal, Textures.Count: > 0 })
    {
        IncrementProgressParallel();
        return;
    }
    
    string outputFolder = spriteFolder;
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
            
            // Get the bag, or create it if necessary
            var bag = texturesToExport.GetOrAdd(pageItem.TexturePage.Name.Content, _ => new ConcurrentBag<TextureToExport>());
        
            bag.Add(new TextureToExport(pageItem, Paths.JoinVerifyWithinDirectory(outputFolder, $"{sprite.Name.Content}_{i}.png"))
            {
                SpriteName = sprite.Name.Content,
                FrameIndex = i,
                OriginX = sprite.OriginX,
                OriginY = sprite.OriginY
            });
        }
    }
    IncrementProgressParallel();
}

void ExportTextures()
{
    int totalCores = Environment.ProcessorCount;
    int outerLimit = Math.Max(1, totalCores / 4); // save some memory
    Parallel.ForEach(texturesToExport, new ParallelOptions { MaxDegreeOfParallelism = outerLimit }, kvp =>
    {
        // separate worker for each page to bound memory usage
        using (TextureWorker localWorker = new TextureWorker())
        {
            foreach (TextureToExport tte in kvp.Value)
            {
                localWorker.ExportAsPNG(tte.PageItem, tte.FileExportLocation, null, padded);
                ExportSpriteOrigin(tte);
            }
        }
        IncrementProgressParallel();
    });
}

public class GMEvent
{
    public string resourceType { get; set; } = "GMEvent";
    public string resourceVersion { get; set; } = "1.0";
    public string name { get; set; } = "";
    public bool isDnD { get; set; } = false;
    public uint eventNum { get; set; } = 0;
    public uint eventType { get; set; } = 0;
    public AssetReference collisionObjectId { get; set; } = null;
}
public class GMObjectProperty
{
    public string resourceType { get; set; } = "GMObjectProperty";
    public string resourceVersion { get; set; } = "1.0";
    public string name { get; set; }
    public int varType { get; set; } = 0;
    public string value { get; set; }
    public bool rangeEnabled { get; set; } = false;
    public double rangeMin { get; set; } = 0.0;
    public double rangeMax { get; set; } = 10.0;
    public List<string> listItems { get; set; } = new List<string> { };
    public bool multiselect { get; set; } = false;
    public List<string> filters { get; set; } = new List<string> { };
}

public class ObjectData
{
    public string resourceType { get; set; } = "GMObject";
    public string resourceVersion { get; set; } = "1.0";
    public string name { get; set; }
    public AssetReference spriteId { get; set; } = new AssetReference();
    public AssetReference spriteMaskId { get; set; } = new AssetReference();
    public bool visible { get; set; }
    public bool solid { get; set; }
    public bool persistent { get; set; }
    public bool managed { get; set; }
    public AssetReference parentObjectId { get; set; } = new AssetReference();
    public List<GMEvent> eventList { get; set; } = new List<GMEvent>();
    public List<GMObjectProperty> properties { get; set; } = new List<GMObjectProperty>();
    public AssetReference parent { get; set; } = new AssetReference();
}

Regex assignmentRegex = new Regex(
    @"^(\w+) = (.+)$",
    RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.ECMAScript
);
// get variable definitions from a precreate event
List<GMObjectProperty> GetObjectProperties(UndertalePointerList<UndertaleGameObject.Event> evList)
{
    List<GMObjectProperty> list = new List<GMObjectProperty> { };
    if (evList == null) return list;
    foreach (UndertaleGameObject.Event ev in evList)
    {
        foreach (UndertaleGameObject.EventAction action in ev.Actions)
        {
            UndertaleCode code = action.CodeId;
            if (code == null) continue;
            string gml = "";
            try
            {
                gml = new Underanalyzer.Decompiler.DecompileContext(DECOMPILE_CONTEXT.Value, code).DecompileToString();
            }
            catch (Exception e) { }
            foreach (Match match in assignmentRegex.Matches(gml))
            {
                list.Add(new GMObjectProperty
                {
                    varType = 4, // expression
                    name = match.Groups[1].Captures[0].Value,
                    value = match.Groups[2].Captures[0].Value,
                });
            }
        }
    }
    return list;
}

void ExportObjects()
{
    Parallel.ForEach(Data.GameObjects, (UndertaleGameObject gameObject) =>
    {
        string objectDir = objectFolder + gameObject.Name.Content + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(objectDir);
        ObjectData objectData = new ObjectData()
        {
            name = gameObject.Name.Content,
            spriteId = gameObject.Sprite != null ? new AssetReference()
            {
                name = gameObject.Sprite.Name.Content,
                path = $"sprite/{gameObject.Sprite.Name.Content}/{gameObject.Sprite.Name.Content}.yy"
            } : null,
            spriteMaskId = gameObject.TextureMaskId != null ? new AssetReference()
            {
                name = gameObject.TextureMaskId.Name.Content,
                path = $"sprite/{gameObject.TextureMaskId.Name.Content}/{gameObject.TextureMaskId.Name.Content}.yy"
            } : null,
            visible = gameObject.Visible,
            solid = gameObject.Solid,
            persistent = gameObject.Persistent,
            managed = gameObject.Managed,
            parentObjectId = gameObject.ParentId != null ? new AssetReference()
            {
                name = gameObject.ParentId.Name.Content,
                path = $"objects/{gameObject.ParentId.Name.Content}/{gameObject.ParentId.Name.Content}.yy"
            } : null,
            parent = new AssetReference()
            {
                name = "Objects",
                path = "folders/Objects.yy"
            },
        };
        for (var i = 0; i < gameObject.Events.Count; i++)
        {
            var evList = gameObject.Events[i];
            // PreCreate is used by variable definitions
            if ((EventType)i == EventType.PreCreate)
            {
                objectData.properties = GetObjectProperties(evList);
                continue;
            }
            foreach (var ev in evList)
            {
                AssetReference collObjRef = null;
                uint subtype = ev.EventSubtype;
                if ((EventType)i == EventType.Collision)
                {
                    subtype = 0;
                    var collObj = Data.GameObjects[(int)ev.EventSubtype];
                    if (collObj != null)
                    {
                        collObjRef = new AssetReference()
                        {
                            name = collObj.Name.Content,
                            path = $"objects/{collObj.Name.Content}/{collObj.Name.Content}.yy"
                        };
                    }
                }
                objectData.eventList.Add(new GMEvent()
                {
                    eventType = (uint)i,
                    eventNum = subtype,
                    collisionObjectId = collObjRef
                });

                var subtypeString = subtype.ToString();
                if ((EventType)i == EventType.Collision)
                {
                    subtypeString = Data.GameObjects[(int)ev.EventSubtype].Name.Content;
                }
                var gmlPath = $"{objectDir}{((EventType)i).ToString()}_{subtypeString}.gml";
                if (ev.Actions.Count > 0)
                {
                    var action = ev.Actions[0];
                    var code = action.CodeId;
                    try
                    {
                        File.WriteAllText(gmlPath, (code != null ? ConvertEnumToConst(new Underanalyzer.Decompiler.DecompileContext(DECOMPILE_CONTEXT.Value, code).DecompileToString()) : ""));
                    }
                    catch (Exception e)
                    {
                        File.WriteAllText(gmlPath, "/*\nDECOMPILER FAILED!\n\n" + e.ToString() + "\n*/");
                    }
                }
                else
                {
                    File.WriteAllText(gmlPath, "/* Empty Event */");
                }
            }
        }
        string json = JsonConvert.SerializeObject(objectData, Formatting.Indented);
        File.WriteAllText(objectDir + gameObject.Name.Content + ".yy", json);
        IncrementProgressParallel();
    });
}

// Writes the sprite origin info to a json file with the same name as the exported png (e.g. spriteName_0.json next to spriteName_0.png).
void ExportSpriteOrigin(TextureToExport tte)
{
    string jsonFileLocation = Path.ChangeExtension(tte.FileExportLocation, ".json");
    var originInfo = new
    {
        sprite = tte.SpriteName,
        frame = tte.FrameIndex,
        originX = tte.OriginX,
        originY = tte.OriginY
    };
    string json = Json.Serialize(originInfo, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(jsonFileLocation, json);
}

public class TextureToExport
{
    public UndertaleTexturePageItem PageItem { get; set; }
    public UndertaleEmbeddedTexture Page => PageItem.TexturePage;
    public string FileExportLocation { get; set; }
    public string SpriteName { get; set; }
    public int FrameIndex { get; set; }
    public int OriginX { get; set; }
    public int OriginY { get; set; }
    
    public TextureToExport(UndertaleTexturePageItem pageItem, string fileExportLocation) => (PageItem, FileExportLocation) = (pageItem, fileExportLocation);
}

//---------------------------------------------------------------------
// 4. Code entries (ExportAllCode.csx)
//---------------------------------------------------------------------
bool isYYC = Data.IsYYC();
if (isYYC)
{
    ScriptError("The opened game uses YYC: no code is available. Skipping the code export.");
}

GlobalDecompileContext globalDecompileContext = new(Data);
Underanalyzer.Decompiler.IDecompileSettings decompilerSettings = Data.ToolInfo.DecompilerSettings;

List<UndertaleCode> toDump = Data.Code.Where(c => c.ParentEntry is null).ToList();

if (!isYYC)
{
    SetProgressBar(null, "Code Entries", 0, toDump.Count);
    StartProgressBarUpdater();

    await DumpCode();

    await StopProgressBarUpdater();
    HideProgressBar();
}

async Task DumpCode()
{
    await Task.Run(() => Parallel.ForEach(toDump, DumpCode));
}

void DumpCode(UndertaleCode code)
{
    if (code is not null)
    {
        string path = Paths.JoinVerifyWithinDirectory(codeFolder, code.Name.Content + ".gml");
        try
        {
            File.WriteAllText(path, (code != null 
                ? new Underanalyzer.Decompiler.DecompileContext(globalDecompileContext, code, decompilerSettings).DecompileToString() 
                : ""));
        }
        catch (Exception e)
        {
            File.WriteAllText(path, "/*\nDECOMPILER FAILED!\n\n" + e.ToString() + "\n*/");
        }
    }

    IncrementProgressParallel();
}