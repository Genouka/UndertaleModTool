using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

public partial class UndertaleEmbeddedTextureViewModel : IUndertaleResourceViewModel
{
    public MainViewModel MainVM;
    public UndertaleResource Resource => EmbeddedTexture;
    public UndertaleEmbeddedTexture EmbeddedTexture { get; }

    readonly List<UndertaleTexturePageItem> itemsInTexture;

    public UndertaleEmbeddedTextureViewModel(UndertaleEmbeddedTexture embeddedTexture, IServiceProvider serviceProvider)
    {
        MainVM = serviceProvider.GetRequiredService<MainViewModel>();

        EmbeddedTexture = embeddedTexture;

        itemsInTexture = MainVM.Data!.TexturePageItems.Where(item => item.TexturePage == embeddedTexture).ToList();
    }

    /// <summary>
    /// Returns the texture page item drawn at this pixel of the texture, if any.
    /// </summary>
    public UndertaleTexturePageItem? FindItemAt(double x, double y)
    {
        foreach (UndertaleTexturePageItem item in itemsInTexture)
        {
            bool isInsideX = x >= item.SourceX && x < item.SourceX + item.SourceWidth;
            bool isInsideY = y >= item.SourceY && y < item.SourceY + item.SourceHeight;
            if (isInsideX && isInsideY)
                return item;
        }
        return null;
    }

    public async void OpenItem(UndertaleTexturePageItem item, bool inNewTab)
    {
        await MainVM.Tabs.TabOpen(item, inNewTab);
    }

    public async void ImportImage()
    {
        // TODO: Allow formats other than PNG, either directly or to convert it
        IReadOnlyList<IStorageFile> files = await MainVM.View!.OpenFileDialog(new FilePickerOpenOptions
        {
            Title = LocalizationSource.GetString("Msg_ImportImage"),
            FileTypeFilter = FilePickerFileTypes.Image,
        });

        if (files.Count != 1)
            return;

        using (Stream stream = await files[0].OpenReadAsync())
        {
            await ImportExport.ImportEmbeddedTexture(EmbeddedTexture, stream);
        }
    }

    public async void ExportImage()
    {
        (IReadOnlyList<FilePickerFileType> filePickerFileTypeList, string extension) type = EmbeddedTexture.TextureData.Image.Format switch
        {
            GMImage.ImageFormat.Png => (FilePickerFileTypes.PNG, "png"),
            GMImage.ImageFormat.Qoi => (FilePickerFileTypes.QOI, "qoi"),
            GMImage.ImageFormat.Bz2Qoi => (FilePickerFileTypes.BZ2, "bz2"),
            _ => (FilePickerFileTypes.BIN, "bin"),
        };

        IStorageFile? file = await MainVM.View!.SaveFileDialog(new FilePickerSaveOptions()
        {
            Title = LocalizationSource.GetString("Msg_ExportImage"),
            FileTypeChoices = type.filePickerFileTypeList,
            DefaultExtension = $"*.{type.extension}",
            SuggestedFileName = $"{EmbeddedTexture.Name?.Content ?? "image"}.{type.extension}",
        });

        if (file is null)
            return;

        using (Stream stream = await file.OpenWriteAsync())
        {
            await ImportExport.ExportEmbeddedTexture(EmbeddedTexture, stream);
        }
    }

    public async void ExportImageAsPNG()
    {
        IStorageFile? file = await MainVM.View!.SaveFileDialog(new FilePickerSaveOptions()
        {
            Title = LocalizationSource.GetString("Msg_ExportImageAsPNG"),
            FileTypeChoices = FilePickerFileTypes.PNG,
            DefaultExtension = ".png",
            SuggestedFileName = $"{EmbeddedTexture.Name?.Content ?? "image"}.png",
        });

        if (file is null)
            return;

        using (Stream stream = await file.OpenWriteAsync())
        {
            await ImportExport.ExportEmbeddedTextureAsPNG(EmbeddedTexture, stream);
        }
    }
}
