// ==UndertaleModTool==
// @name        Export All Embedded Textures
// @name:zh-Hans 导出所有嵌入纹理
// @name:zh-CN  导出所有嵌入纹理
// ==/UndertaleModTool==
using System.Text;
using System;
using System.IO;
using System.Threading.Tasks;
using UndertaleModLib.Util;

EnsureDataLoaded();

string texturesFolder = PromptChooseDirectory();
if (texturesFolder is null)
{
    return;
}

SetProgressBar(null, "Embedded Textures", 0, Data.EmbeddedTextures.Count);
StartProgressBarUpdater();

await Task.Run(() =>
{
    for (int i = 0; i < Data.EmbeddedTextures.Count; i++)
    {
        try
        {
            using FileStream fs = new(Paths.JoinVerifyWithinDirectory(texturesFolder, $"{i}.png"), FileMode.Create);
            Data.EmbeddedTextures[i].TextureData.Image.SavePng(fs);
        }
        catch (Exception ex)
        {
            ScriptMessage($"Failed to export file: {ex.Message}");
        }

        IncrementProgress();
    }
});

await StopProgressBarUpdater();
HideProgressBar();
