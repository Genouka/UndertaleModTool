using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

public partial class UndertaleTexturePageItemViewModel : IUndertaleResourceViewModel
{
    public MainViewModel MainVM;
    public UndertaleResource Resource => TexturePageItem;
    public UndertaleTexturePageItem TexturePageItem { get; }

    public UndertaleTexturePageItemViewModel(UndertaleTexturePageItem texturePageItem, IServiceProvider serviceProvider)
    {
        MainVM = serviceProvider.GetRequiredService<MainViewModel>();

        TexturePageItem = texturePageItem;
    }

    public async void LoadImageWithoutPadding()
    {
        LoadImage();
    }

    public async void SaveImageWithoutPadding()
    {
        SaveImage(includePadding: false);
    }

    public async void SaveImageWithPadding()
    {
        SaveImage(includePadding: true);
    }

    async void LoadImage()
    {
        IReadOnlyList<IStorageFile> files = await MainVM.View!.OpenFileDialog(new FilePickerOpenOptions
        {
            Title = LocalizationSource.GetString("Msg_LoadImageWithoutPadding"),
            FileTypeFilter = FilePickerFileTypes.Image,
        });

        if (files.Count != 1)
            return;

        using (Stream stream = await files[0].OpenReadAsync())
        {
            try
            {
                await ImportExport.ImportTexturePageItem(TexturePageItem, stream);
            }
            catch (Exception ex)
            {
                await MainVM.View.MessageDialog(ex.ToString(), title: LocalizationSource.GetString("Msg_LoadImageError"));
            }
        }
    }

    async void SaveImage(bool includePadding)
    {
        IStorageFile? file = await MainVM.View!.SaveFileDialog(new FilePickerSaveOptions()
        {
            Title = string.Format(LocalizationSource.GetString("Msg_SaveImageWithPadding"), includePadding ? "with" : "without"),
            FileTypeChoices = FilePickerFileTypes.PNG,
            DefaultExtension = ".png",
            SuggestedFileName = TexturePageItem.Name?.Content ?? "image",
        });

        if (file is null)
            return;

        using (Stream stream = await file.OpenWriteAsync())
        {
            try
            {
                await ImportExport.ExportTexturePageItemAsPNG(TexturePageItem, stream, includePadding);
            }
            catch (Exception ex)
            {
                await MainVM.View.MessageDialog(ex.ToString(), title: LocalizationSource.GetString("Msg_SaveImageError"));
            }
        }
    }
}
