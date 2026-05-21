#pragma warning disable CA1416

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ImageMagick;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModTool.Localization;
using Ookii.Dialogs.Wpf;

namespace UndertaleModTool
{
    public partial class ImportGraphicsDialog : Window
    {
        private static readonly MainWindow mainWindow = Application.Current.MainWindow as MainWindow;

        private readonly UndertaleData data;

        public static readonly List<string> ImportModeDisplayList = new()
        {
            LocalizationSource.GetString("ImportGraphics_ReplaceInPlace"),
            LocalizationSource.GetString("ImportGraphics_NewTexturePages"),
            LocalizationSource.GetString("ImportGraphics_KeepOriginalTexturePage")
        };

        public ObservableCollection<ImportImageEntry> ImageEntries { get; } = new();

        public List<ImportImageEntry> SelectedEntries { get; private set; }

        public int SelectedTexturePageSize { get; private set; } = 2048;

        public bool IsGMS2 { get; }

        private static readonly Regex SprFrameRegex = new(@"^(.+?)(?:_(\d+))$", RegexOptions.Compiled);

        public ImportGraphicsDialog(UndertaleData data)
        {
            this.data = data;
            IsGMS2 = data.IsGameMaker2();

            InitializeComponent();

            ImageListView.ItemsSource = ImageEntries;

            if (!IsGMS2)
            {
                IsSpecialTypeCheckBox.IsEnabled = false;
                AnimationSpeedTextBox.IsEnabled = false;
                PlaybackTypeComboBox.IsEnabled = false;
                SpecialVersionTextBox.IsEnabled = false;
            }
        }

        private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible || IsLoaded)
                return;

            if (Settings.Instance.EnableDarkMode)
                MainWindow.SetDarkTitleBarForWindow(this, true, false);
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            VistaFolderBrowserDialog folderBrowser = new();
            if (folderBrowser.ShowDialog() == true)
            {
                ImportFolderTextBox.Text = folderBrowser.SelectedPath;
            }
        }

        private void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ImportFolderTextBox.Text))
            {
                this.ShowWarning(LocalizationSource.GetString("ImportGraphics_NoFolderSelected"));
                return;
            }

            if (!Directory.Exists(ImportFolderTextBox.Text))
            {
                this.ShowWarning(LocalizationSource.GetString("ImportGraphics_FolderNotExist"));
                return;
            }

            ScanImages(ImportFolderTextBox.Text, ImportUnknownAsSpriteCheck.IsChecked == true);
        }

        private void ModeCombo_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ComboBoxDark combo)
                return;

            if (combo.DataContext is ImportImageEntry entry)
                combo.ItemsSource = entry.AvailableModeDisplays;

            Popup popup = MainWindow.FindVisualChild<Popup>(combo);
            var content = MainWindow.FindVisualChild<Border>(popup?.Child);
            if (content is not null)
                content.SetResourceReference(ForegroundProperty, SystemColors.ControlTextBrushKey);
        }

        private void ImageListView_PreparingCellForEdit(object sender, DataGridPreparingCellForEditEventArgs e)
        {
            if (e.EditingElement is ComboBoxDark combo)
            {
                if (combo.DataContext is ImportImageEntry entry)
                    combo.ItemsSource = entry.AvailableModeDisplays;

                Popup popup = MainWindow.FindVisualChild<Popup>(combo);
                var content = MainWindow.FindVisualChild<Border>(popup?.Child);
                if (content is not null)
                    content.SetResourceReference(ForegroundProperty, SystemColors.ControlTextBrushKey);
            }
        }

        private void ScanImages(string importFolder, bool importUnknownAsSprite)
        {
            ImageEntries.Clear();

            string[] files = Directory.GetFiles(importFolder, "*.*", SearchOption.AllDirectories);

            foreach (string file in files)
            {
                string ext = Path.GetExtension(file);
                if (!ext.Equals(".png", StringComparison.InvariantCultureIgnoreCase) &&
                    !ext.Equals(".gif", StringComparison.InvariantCultureIgnoreCase))
                    continue;

                string filenameWithExtension = Path.GetFileName(file);
                string stripped = Path.GetFileNameWithoutExtension(file);

                SpriteType spriteType = GraphicsImporter.GetSpriteType(file);
                if (importUnknownAsSprite && (spriteType == SpriteType.Unknown || spriteType == SpriteType.Font))
                    spriteType = SpriteType.Sprite;

                if (spriteType == SpriteType.Background)
                {
                    var entry = ScanBackgroundEntry(file, stripped, spriteType);
                    ImageEntries.Add(entry);
                    continue;
                }

                if (spriteType != SpriteType.Sprite)
                    continue;

                if (ext.Equals(".gif", StringComparison.InvariantCultureIgnoreCase))
                {
                    ScanGifEntries(file, stripped, spriteType, importUnknownAsSprite);
                    continue;
                }

                Match stripMatch = Regex.Match(stripped, @"(.*)_strip(\d+)");
                if (stripMatch.Success)
                {
                    ScanStripEntries(file, stripped, stripMatch, spriteType);
                    continue;
                }

                int lastUnderscore = stripped.LastIndexOf('_');
                if (lastUnderscore >= 0 && int.TryParse(stripped.Substring(lastUnderscore + 1), out int frame))
                {
                    string spriteName = stripped.Substring(0, lastUnderscore);
                    var entry = ScanSpriteEntry(file, filenameWithExtension, stripped, spriteName, frame, spriteType);
                    ImageEntries.Add(entry);
                }
                else
                {
                    var entry = ScanSpriteEntry(file, filenameWithExtension, stripped, stripped, 0, spriteType);
                    ImageEntries.Add(entry);
                }
            }

            EstimateNewTexturePageIndices();
        }

        private void EstimateNewTexturePageIndices()
        {
            var newPageEntries = ImageEntries.Where(e => e.ImportMode == ImportMode.NewTexturePages).ToList();
            if (newPageEntries.Count == 0)
                return;

            int pageSize = 2048;
            int startPage = data.EmbeddedTextures.Count;

            var sortedEntries = newPageEntries
                .OrderByDescending(e => Math.Max(e.ImageWidth, e.ImageHeight))
                .ThenByDescending(e => e.ImageWidth * e.ImageHeight)
                .ToList();

            var pageRemaining = new List<int> { pageSize };
            var pageRowHeight = new List<int> { 0 };
            var pageRowX = new List<int> { 0 };

            foreach (var entry in sortedEntries)
            {
                bool placed = false;

                for (int i = 0; i < pageRemaining.Count; i++)
                {
                    int imgW = entry.ImageWidth + 2;
                    int imgH = entry.ImageHeight + 2;

                    if (imgW > pageSize || imgH > pageSize)
                        continue;

                    if (pageRowX[i] + imgW <= pageSize && imgH <= pageRemaining[i] - pageRowHeight[i])
                    {
                        entry.EstimatedTexturePageIndex = startPage + i;
                        pageRowX[i] += imgW;
                        pageRowHeight[i] = Math.Max(pageRowHeight[i], imgH);
                        placed = true;
                        break;
                    }

                    int newRowStart = pageRowHeight[i];
                    if (imgW <= pageSize && newRowStart + imgH <= pageRemaining[i])
                    {
                        entry.EstimatedTexturePageIndex = startPage + i;
                        pageRowX[i] = imgW;
                        pageRowHeight[i] = newRowStart + imgH;
                        placed = true;
                        break;
                    }
                }

                if (!placed)
                {
                    int imgW = entry.ImageWidth + 2;
                    int imgH = entry.ImageHeight + 2;
                    pageRemaining.Add(pageSize);
                    pageRowHeight.Add(imgH);
                    pageRowX.Add(imgW);
                    entry.EstimatedTexturePageIndex = startPage + pageRemaining.Count - 1;
                }
            }
        }

        private ImportImageEntry ScanBackgroundEntry(string file, string stripped, SpriteType spriteType)
        {
            int imgW = 0, imgH = 0;
            try
            {
                using MagickImage img = new(file);
                imgW = (int)img.Width;
                imgH = (int)img.Height;
            }
            catch { }

            UndertaleBackground bg = data.Backgrounds.ByName(stripped);
            bool exists = bg is not null;
            int? tgtW = exists ? (int?)bg.Texture?.TargetWidth : null;
            int? tgtH = exists ? (int?)bg.Texture?.TargetHeight : null;
            int? srcW = exists ? (int?)bg.Texture?.SourceWidth : null;
            int? srcH = exists ? (int?)bg.Texture?.SourceHeight : null;
            bool canReplace = exists && tgtW == imgW && tgtH == imgH;

            return new ImportImageEntry
            {
                FilePath = file,
                FileName = Path.GetFileName(file),
                SpriteName = stripped,
                FrameIndex = 0,
                ImageType = spriteType,
                ImageWidth = imgW,
                ImageHeight = imgH,
                TargetWidth = tgtW,
                TargetHeight = tgtH,
                SourceWidth = srcW,
                SourceHeight = srcH,
                CanReplaceInPlace = canReplace,
                SpriteExists = exists,
                StatusText = exists ? (canReplace ? "✓ Can replace" : "Dimension mismatch") : "New background",
                ImportMode = canReplace ? ImportMode.KeepOriginalTexturePage : ImportMode.NewTexturePages,
                OriginalTexturePageIndex = exists ? GetTexturePageIndex(bg?.Texture?.TexturePage) : null,
                EstimatedTexturePageIndex = null
            };
        }

        private void ScanGifEntries(string file, string stripped, SpriteType spriteType, bool importUnknownAsSprite)
        {
            try
            {
                using MagickImageCollection gif = new(file);
                int frames = gif.Count;
                for (int i = 0; i < frames; i++)
                {
                    string spriteName = stripped;
                    int frame = i;
                    int imgW = (int)gif[i].Width;
                    int imgH = (int)gif[i].Height;

                    UndertaleSprite sprite = data.Sprites.ByName(spriteName);
                    bool exists = sprite is not null;
                    bool canReplace = false;
                    int? tgtW = null, tgtH = null;
                    int? srcW = null, srcH = null;
                    UndertaleTexturePageItem tex = null;

                    if (exists && frame < sprite.Textures.Count)
                    {
                        tex = sprite.Textures[frame]?.Texture;
                        tgtW = tex?.TargetWidth;
                        tgtH = tex?.TargetHeight;
                        srcW = tex?.SourceWidth;
                        srcH = tex?.SourceHeight;
                        canReplace = tgtW == imgW && tgtH == imgH;
                    }

                    ImageEntries.Add(new ImportImageEntry
                    {
                        FilePath = file,
                        FileName = $"{Path.GetFileName(file)} [{i}]",
                        SpriteName = spriteName,
                        FrameIndex = frame,
                        ImageType = spriteType,
                        ImageWidth = imgW,
                        ImageHeight = imgH,
                        TargetWidth = tgtW,
                        TargetHeight = tgtH,
                        SourceWidth = srcW,
                        SourceHeight = srcH,
                        CanReplaceInPlace = canReplace,
                        SpriteExists = exists,
                        StatusText = exists ? (canReplace ? "✓ Can replace" : "Dimension mismatch") : "New sprite",
                        ImportMode = canReplace ? ImportMode.KeepOriginalTexturePage : ImportMode.NewTexturePages,
                        OriginalTexturePageIndex = exists ? GetTexturePageIndex(tex?.TexturePage) : null,
                        EstimatedTexturePageIndex = null
                    });
                }
            }
            catch { }
        }

        private void ScanStripEntries(string file, string stripped, Match stripMatch, SpriteType spriteType)
        {
            string spriteName = stripMatch.Groups[1].Value;
            if (!uint.TryParse(stripMatch.Groups[2].Value, out uint frameCount) || frameCount == 0)
                return;

            try
            {
                using MagickImage img = new(file);
                if (img.Width % frameCount != 0)
                    return;

                uint frameWidth = (uint)img.Width / frameCount;
                uint frameHeight = (uint)img.Height;

                for (uint i = 0; i < frameCount; i++)
                {
                    int frame = (int)i;
                    UndertaleSprite sprite = data.Sprites.ByName(spriteName);
                    bool exists = sprite is not null;
                    bool canReplace = false;
                    int? tgtW = null, tgtH = null;
                    int? srcW = null, srcH = null;
                    UndertaleTexturePageItem tex = null;

                    if (exists && frame < sprite.Textures.Count)
                    {
                        tex = sprite.Textures[frame]?.Texture;
                        tgtW = tex?.TargetWidth;
                        tgtH = tex?.TargetHeight;
                        srcW = tex?.SourceWidth;
                        srcH = tex?.SourceHeight;
                        canReplace = tgtW == (int)frameWidth && tgtH == (int)frameHeight;
                    }

                    ImageEntries.Add(new ImportImageEntry
                    {
                        FilePath = file,
                        FileName = $"{Path.GetFileName(file)} [{i}]",
                        SpriteName = spriteName,
                        FrameIndex = frame,
                        ImageType = spriteType,
                        ImageWidth = (int)frameWidth,
                        ImageHeight = (int)frameHeight,
                        TargetWidth = tgtW,
                        TargetHeight = tgtH,
                        SourceWidth = srcW,
                        SourceHeight = srcH,
                        CanReplaceInPlace = canReplace,
                        SpriteExists = exists,
                        StatusText = exists ? (canReplace ? "✓ Can replace" : "Dimension mismatch") : "New sprite",
                        ImportMode = canReplace ? ImportMode.KeepOriginalTexturePage : ImportMode.NewTexturePages,
                        OriginalTexturePageIndex = exists ? GetTexturePageIndex(tex?.TexturePage) : null,
                        EstimatedTexturePageIndex = null
                    });
                }
            }
            catch { }
        }

        private ImportImageEntry ScanSpriteEntry(string file, string filenameWithExtension, string stripped, string spriteName, int frame, SpriteType spriteType)
        {
            int imgW = 0, imgH = 0;
            try
            {
                using MagickImage img = new(file);
                imgW = (int)img.Width;
                imgH = (int)img.Height;
            }
            catch { }

            UndertaleSprite sprite = data.Sprites.ByName(spriteName);
            bool exists = sprite is not null;
            bool canReplace = false;
            int? tgtW = null, tgtH = null;
            int? srcW = null, srcH = null;

            if (exists && frame >= 0 && frame < sprite.Textures.Count)
            {
                var tex = sprite.Textures[frame]?.Texture;
                tgtW = tex?.TargetWidth;
                tgtH = tex?.TargetHeight;
                srcW = tex?.SourceWidth;
                srcH = tex?.SourceHeight;
                canReplace = tgtW == imgW && tgtH == imgH;
            }

            string status;
            if (!exists)
                status = "New sprite";
            else if (frame < 0 || frame >= sprite.Textures.Count)
                status = "Frame out of range";
            else if (canReplace)
                status = "✓ Can replace";
            else
                status = "Dimension mismatch";

            UndertaleTexturePageItem texItem = null;
            if (exists && frame >= 0 && frame < sprite.Textures.Count)
                texItem = sprite.Textures[frame]?.Texture;

            return new ImportImageEntry
            {
                FilePath = file,
                FileName = filenameWithExtension,
                SpriteName = spriteName,
                FrameIndex = frame,
                ImageType = spriteType,
                ImageWidth = imgW,
                ImageHeight = imgH,
                TargetWidth = tgtW,
                TargetHeight = tgtH,
                SourceWidth = srcW,
                SourceHeight = srcH,
                CanReplaceInPlace = canReplace,
                SpriteExists = exists,
                StatusText = status,
                ImportMode = canReplace ? ImportMode.KeepOriginalTexturePage : ImportMode.NewTexturePages,
                OriginalTexturePageIndex = exists ? GetTexturePageIndex(texItem?.TexturePage) : null,
                EstimatedTexturePageIndex = null
            };
        }

        private void ApplyDefaultsButton_Click(object sender, RoutedEventArgs e)
        {
            ImportMode defaultMode = ReplaceInPlaceRadio.IsChecked == true ? ImportMode.ReplaceInPlace :
                                     NewTexturePagesRadio.IsChecked == true ? ImportMode.NewTexturePages :
                                     ImportMode.KeepOriginalTexturePage;

            int originPos = OriginPositionComboBox.SelectedIndex;
            float animSpd = float.TryParse(AnimationSpeedTextBox.Text, out float s) ? s : 1f;
            int playType = PlaybackTypeComboBox.SelectedIndex;
            bool isSpecial = IsSpecialTypeCheckBox.IsChecked == true;
            uint specVer = uint.TryParse(SpecialVersionTextBox.Text, out uint v) ? v : 1;

            foreach (var entry in ImageEntries)
            {
                if (!entry.IsSelected)
                    continue;

                ImportMode resolved = ResolveMode(entry, defaultMode);
                entry.ImportMode = resolved;
                entry.OriginPosition = originPos;
                entry.AnimationSpeed = animSpd;
                entry.PlaybackType = playType;
                entry.IsSpecialType = isSpecial;
                entry.SpecialVersion = specVer;
            }
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var entry in ImageEntries)
                entry.IsSelected = true;
        }

        private void DeselectAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var entry in ImageEntries)
                entry.IsSelected = false;
        }

        private void SetSelectedModeButton_Click(object sender, RoutedEventArgs e)
        {
            ImportMode defaultMode = ReplaceInPlaceRadio.IsChecked == true ? ImportMode.ReplaceInPlace :
                                     NewTexturePagesRadio.IsChecked == true ? ImportMode.NewTexturePages :
                                     ImportMode.KeepOriginalTexturePage;

            foreach (var entry in ImageEntries)
            {
                if (!entry.IsSelected)
                    continue;

                entry.ImportMode = ResolveMode(entry, defaultMode);
            }
        }

        private static ImportMode ResolveMode(ImportImageEntry entry, ImportMode requested)
        {
            if (entry.AvailableModes.Contains(requested))
                return requested;

            if (requested == ImportMode.ReplaceInPlace)
            {
                if (entry.AvailableModes.Contains(ImportMode.KeepOriginalTexturePage))
                    return ImportMode.KeepOriginalTexturePage;
                return ImportMode.NewTexturePages;
            }

            if (requested == ImportMode.KeepOriginalTexturePage)
                return ImportMode.NewTexturePages;

            return ImportMode.NewTexturePages;
        }

        private int? GetTexturePageIndex(UndertaleEmbeddedTexture texturePage)
        {
            if (texturePage is null)
                return null;
            int index = data.EmbeddedTextures.IndexOf(texturePage);
            return index >= 0 ? index : null;
        }

        private void OKButton_Click(object sender, RoutedEventArgs e)
        {
            if (ImageEntries.Count == 0)
            {
                this.ShowWarning(LocalizationSource.GetString("ImportGraphics_NoImagesFound"));
                return;
            }

            var selected = ImageEntries.Where(x => x.IsSelected).ToList();
            if (selected.Count == 0)
            {
                this.ShowWarning(LocalizationSource.GetString("ImportGraphics_NoImagesSelected"));
                return;
            }

            SelectedEntries = selected;

            int[] pageSizes = { 2048, 4096, 8192 };
            SelectedTexturePageSize = pageSizes[TexturePageSizeComboBox.SelectedIndex];

            DialogResult = true;
        }
    }
}
