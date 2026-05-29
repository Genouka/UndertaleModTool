using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
using UndertaleModTool.Localization;

namespace UndertaleModTool.Windows
{
    public partial class MergeTexturePageWizard : Window, INotifyPropertyChanged
    {
        private static readonly MainWindow mainWindow = Application.Current.MainWindow as MainWindow;

        private readonly UndertaleEmbeddedTexture sourcePage;
        private readonly UndertaleTexturePageItem[] sourceItems;
        private int currentStep = 1;
        private CancellationTokenSource _previewCts;
        private int _previewVersion;
        private bool _isProcessing;

        public ObservableCollection<EntryItem> Entries { get; } = new();
        public List<EntryItem> SelectedEntries => Entries.Where(e => e.IsSelected).ToList();

        public bool IsProcessing
        {
            get => _isProcessing;
            set
            {
                if (_isProcessing != value)
                {
                    _isProcessing = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsProcessing)));
                    UpdateButtonStates();
                }
            }
        }

#pragma warning disable CS0067
        public event PropertyChangedEventHandler PropertyChanged;
#pragma warning restore CS0067

        public MergeTexturePageWizard(UndertaleEmbeddedTexture texturePage)
        {
            InitializeComponent();

            sourcePage = texturePage ?? throw new ArgumentNullException(nameof(texturePage));
            sourceItems = mainWindow.Data.TexturePageItems
                .Where(x => x.TexturePage == sourcePage)
                .ToArray();

            foreach (var item in sourceItems)
            {
                Entries.Add(new EntryItem(item, GetItemDisplayName(item)));
            }

            EntryListBox.ItemsSource = Entries;
            EntryListBox.SelectionChanged += EntryListBox_SelectionChanged;

            LoadSourceImage();
            UpdateStepUI();
        }

        private static string GetItemDisplayName(UndertaleTexturePageItem item)
        {
            string name = item.Name?.Content;
            if (!string.IsNullOrEmpty(name) && name != "PageItem Unknown Index")
                return name;

            var spr = mainWindow.Data.Sprites.FirstOrDefault(s =>
                s.Textures.Any(t => t?.Texture == item));
            if (spr != null)
            {
                int idx = -1;
                for (int i = 0; i < spr.Textures.Count; i++)
                {
                    if (spr.Textures[i]?.Texture == item)
                    {
                        idx = i;
                        break;
                    }
                }
                return idx >= 0 ? $"{spr.Name.Content}[{idx}]" : spr.Name.Content;
            }

            var bg = mainWindow.Data.Backgrounds.FirstOrDefault(b => b?.Texture == item);
            if (bg != null)
                return bg.Name.Content;

            var font = mainWindow.Data.Fonts.FirstOrDefault(f => f?.Texture == item);
            if (font != null)
                return font.Name.Content;

            return item.ToString();
        }

        private void LoadSourceImage()
        {
            if (sourcePage.TextureData?.Image is null)
            {
                Step1TextureImage.Source = null;
                return;
            }

            GMImage image = sourcePage.TextureData.Image;
            BitmapSource bitmap = mainWindow.GetBitmapSourceForImage(image);
            Step1TextureImage.Source = bitmap;
            Step1OverlayCanvas.Width = bitmap.Width;
            Step1OverlayCanvas.Height = bitmap.Height;
            BuildOverlayRects();
        }

        private void BuildOverlayRects()
        {
            Step1OverlayCanvas.Children.Clear();
            foreach (var entry in Entries)
            {
                var border = new Border
                {
                    Width = entry.Item.SourceWidth,
                    Height = entry.Item.SourceHeight,
                    Background = new SolidColorBrush(Color.FromArgb(40, 0, 120, 255)),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(120, 0, 120, 255)),
                    Tag = entry,
                    Cursor = Cursors.Hand
                };
                Canvas.SetLeft(border, entry.Item.SourceX);
                Canvas.SetTop(border, entry.Item.SourceY);
                border.MouseDown += OverlayRect_MouseDown;
                Step1OverlayCanvas.Children.Add(border);
                entry.Border = border;
            }
            UpdateOverlayColors();
        }

        private void UpdateOverlayColors()
        {
            foreach (var entry in Entries)
            {
                if (entry.Border == null) continue;
                entry.Border.Background = entry.IsSelected
                    ? new SolidColorBrush(Color.FromArgb(80, 0, 200, 80))
                    : new SolidColorBrush(Color.FromArgb(40, 0, 120, 255));
                entry.Border.BorderBrush = entry.IsSelected
                    ? new SolidColorBrush(Color.FromArgb(200, 0, 200, 80))
                    : new SolidColorBrush(Color.FromArgb(120, 0, 120, 255));
            }
        }

        private void OverlayRect_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is EntryItem entry)
            {
                entry.IsSelected = !entry.IsSelected;
                UpdateOverlayColors();
                SyncListBoxSelection();
                UpdateSelectionCount();
                e.Handled = true;
            }
        }

        private void SyncListBoxSelection()
        {
            EntryListBox.SelectionChanged -= EntryListBox_SelectionChanged;
            EntryListBox.SelectedItems.Clear();
            foreach (var entry in Entries.Where(e => e.IsSelected))
            {
                EntryListBox.SelectedItems.Add(entry);
            }
            EntryListBox.SelectionChanged += EntryListBox_SelectionChanged;
        }

        private void EntryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            foreach (EntryItem item in e.RemovedItems)
                item.IsSelected = false;
            foreach (EntryItem item in e.AddedItems)
                item.IsSelected = true;
            UpdateOverlayColors();
            UpdateSelectionCount();
        }

        private void UpdateSelectionCount()
        {
            int count = Entries.Count(e => e.IsSelected);
            SelectionCountText.Text = string.Format(LocalizationSource.GetString("MergeTP_SelectedCount"), count, Entries.Count);
        }

        private void UpdateStepUI()
        {
            Step1Panel.Visibility = currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
            Step2Panel.Visibility = currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;

            if (currentStep == 1)
            {
                StepTitle.Text = LocalizationSource.GetString("MergeTP_Step1Title");
                Step1HintText.Text = LocalizationSource.GetString("Editor_TextureMouseInteractableHint");
                BackButton.Visibility = Visibility.Collapsed;
                NextButton.Visibility = Visibility.Visible;
                ConfirmButton.Visibility = Visibility.Collapsed;
                CancelButton.Visibility = Visibility.Visible;
                UpdateSelectionCount();
            }
            else if (currentStep == 2)
            {
                StepTitle.Text = LocalizationSource.GetString("MergeTP_Step2Title");
                BackButton.Visibility = Visibility.Visible;
                NextButton.Visibility = Visibility.Collapsed;
                ConfirmButton.Visibility = Visibility.Visible;
                CancelButton.Visibility = Visibility.Visible;
                PopulateTargetPageCombo();
                UpdateButtonStates();
            }
        }

        private void PopulateTargetPageCombo()
        {
            TargetPageCombo.Items.Clear();

            TargetPageCombo.Items.Add(new ComboBoxItem
            {
                Content = LocalizationSource.GetString("MergeTP_NewPage"),
                Tag = null
            });

            foreach (var tex in mainWindow.Data.EmbeddedTextures)
            {
                if (tex == sourcePage) continue;
                TargetPageCombo.Items.Add(new ComboBoxItem
                {
                    Content = tex.Name?.Content ?? tex.ToString(),
                    Tag = tex
                });
            }

            if (TargetPageCombo.Items.Count > 0)
                TargetPageCombo.SelectedIndex = 0;
        }

        private async void TargetPageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateTargetThumbnail();
            await StartPreviewUpdate();
        }

        private void UpdateTargetThumbnail()
        {
            if (TargetPageCombo.SelectedItem is not ComboBoxItem comboItem ||
                comboItem.Tag is not UndertaleEmbeddedTexture targetPage)
            {
                TargetThumbnailImage.Source = null;
                TargetInfoText.Text = "";
                return;
            }

            if (targetPage.TextureData?.Image is null)
            {
                TargetThumbnailImage.Source = null;
                TargetInfoText.Text = "";
                return;
            }

            GMImage image = targetPage.TextureData.Image;
            BitmapSource bitmap = mainWindow.GetBitmapSourceForImage(image);
            TargetThumbnailImage.Source = bitmap;
            TargetInfoText.Text = string.Format(LocalizationSource.GetString("MergeTP_PreviewSize"),
                image.Width, image.Height);
        }

        private readonly record struct FreeRect(int X, int Y, int W, int H);

        private static void SplitFreeList(List<FreeRect> free, int px, int py, int pw, int ph)
        {
            int count = free.Count;
            for (int i = 0; i < count; i++)
            {
                var f = free[i];
                if (px >= f.X + f.W || px + pw <= f.X || py >= f.Y + f.H || py + ph <= f.Y)
                    continue;

                free[i] = free[count - 1];
                free.RemoveAt(count - 1);
                i--;
                count--;

                if (py > f.Y)
                    free.Add(new(f.X, f.Y, f.W, py - f.Y));
                if (py + ph < f.Y + f.H)
                    free.Add(new(f.X, py + ph, f.W, f.Y + f.H - (py + ph)));
                if (px > f.X)
                    free.Add(new(f.X, f.Y, px - f.X, f.H));
                if (px + pw < f.X + f.W)
                    free.Add(new(px + pw, f.Y, f.X + f.W - (px + pw), f.H));
            }
        }

        private static void PruneContained(List<FreeRect> free)
        {
            if (free.Count < 2) return;
            var keep = new bool[free.Count];
            for (int i = 0; i < keep.Length; i++) keep[i] = true;

            for (int i = 0; i < free.Count; i++)
            {
                if (!keep[i]) continue;
                var a = free[i];
                for (int j = i + 1; j < free.Count; j++)
                {
                    if (!keep[j]) continue;
                    var b = free[j];
                    bool aInB = a.X >= b.X && a.Y >= b.Y && a.X + a.W <= b.X + b.W && a.Y + a.H <= b.Y + b.H;
                    bool bInA = b.X >= a.X && b.Y >= a.Y && b.X + b.W <= a.X + a.W && b.Y + b.H <= a.Y + a.H;
                    if (aInB) { keep[i] = false; break; }
                    if (bInA) { keep[j] = false; }
                }
            }

            int w = 0;
            for (int i = 0; i < free.Count; i++)
            {
                if (keep[i])
                    free[w++] = free[i];
            }
            free.RemoveRange(w, free.Count - w);
        }

        private static bool TryInsertBSSF(List<FreeRect> free, int w, int h, out int bx, out int by)
        {
            bx = 0;
            by = 0;
            int bestSSF = int.MaxValue;
            int bestLSF = int.MaxValue;
            bool found = false;

            for (int i = 0; i < free.Count; i++)
            {
                var f = free[i];
                if (w > f.W || h > f.H) continue;
                int ssf = Math.Min(f.W - w, f.H - h);
                int lsf = Math.Max(f.W - w, f.H - h);
                if (ssf < bestSSF || (ssf == bestSSF && lsf < bestLSF))
                {
                    bx = f.X;
                    by = f.Y;
                    bestSSF = ssf;
                    bestLSF = lsf;
                    found = true;
                }
            }

            return found;
        }

        private static int NextPow2(int v)
        {
            v--;
            v |= v >> 1;
            v |= v >> 2;
            v |= v >> 4;
            v |= v >> 8;
            v |= v >> 16;
            return v + 1;
        }

        private class PackResult
        {
            public int PageWidth;
            public int PageHeight;
            public bool PreservedExisting;
            public List<(int Index, ushort SrcX, ushort SrcY, ushort SrcW, ushort SrcH)> ExistingPlacements = new();
            public List<(int Index, ushort SrcX, ushort SrcY, ushort SrcW, ushort SrcH)> SelectedPlacements = new();
        }

        private static PackResult TryAppendPack(
            List<(int X, int Y, int W, int H)> existingRects,
            List<MagickImage> selectedImages)
        {
            const int MaxSize = 4096;

            var free = new List<FreeRect> { new(0, 0, MaxSize, MaxSize) };

            foreach (var occ in existingRects)
                SplitFreeList(free, occ.X, occ.Y, occ.W, occ.H);
            PruneContained(free);

            var sorted = selectedImages
                .Select((img, i) => (Img: img, Index: i))
                .OrderByDescending(x => (long)x.Img.Width * x.Img.Height)
                .ThenByDescending(x => Math.Max(x.Img.Width, x.Img.Height))
                .ToList();

            var placements = new (int Index, ushort SrcX, ushort SrcY, ushort SrcW, ushort SrcH)[selectedImages.Count];
            bool[] placed = new bool[selectedImages.Count];
            int maxW = 0, maxH = 0;
            foreach (var r in existingRects)
            {
                maxW = Math.Max(maxW, r.X + r.W);
                maxH = Math.Max(maxH, r.Y + r.H);
            }

            foreach (var (img, origIdx) in sorted)
            {
                int w = (int)img.Width;
                int h = (int)img.Height;
                if (!TryInsertBSSF(free, w, h, out int bx, out int by))
                    return null;

                SplitFreeList(free, bx, by, w, h);
                if (free.Count > 128)
                    PruneContained(free);

                placements[origIdx] = (origIdx, (ushort)bx, (ushort)by, (ushort)w, (ushort)h);
                placed[origIdx] = true;
                maxW = Math.Max(maxW, bx + w);
                maxH = Math.Max(maxH, by + h);
            }

            int pw = NextPow2(maxW);
            int ph = NextPow2(maxH);
            if (pw > MaxSize || ph > MaxSize)
                return null;

            var result = new PackResult
            {
                PageWidth = pw,
                PageHeight = ph,
                PreservedExisting = true
            };
            for (int i = 0; i < existingRects.Count; i++)
                result.ExistingPlacements.Add((i, (ushort)existingRects[i].X, (ushort)existingRects[i].Y, (ushort)existingRects[i].W, (ushort)existingRects[i].H));
            for (int i = 0; i < placements.Length; i++)
                if (placed[i])
                    result.SelectedPlacements.Add(placements[i]);

            return result;
        }

        private static PackResult TryFullRepack(
            List<MagickImage> existingImages,
            List<MagickImage> selectedImages)
        {
            try
            {
                var packer = new TextureGroupPacker(maxTextureSize: 4096, border: 2, allowCrop: false);

                var existingPackerItems = new List<UndertaleTexturePageItem>();
                foreach (var img in existingImages)
                {
                    var clone = new MagickImage(img);
                    var item = packer.AddImage(clone, TextureGroupPacker.BorderFlags.None, false);
                    existingPackerItems.Add(item);
                }

                var selectedPackerItems = new List<UndertaleTexturePageItem>();
                foreach (var img in selectedImages)
                {
                    var clone = new MagickImage(img);
                    var item = packer.AddImage(clone, TextureGroupPacker.BorderFlags.None, false);
                    selectedPackerItems.Add(item);
                }

                packer.PackPages();

                var allItems = existingPackerItems.Concat(selectedPackerItems).ToList();
                if (allItems.Count == 0) return null;

                var firstPage = allItems[0].TexturePage;
                if (allItems.Any(x => x.TexturePage != firstPage)) return null;

                var result = new PackResult
                {
                    PageWidth = (int)firstPage.TextureData.Image.Width,
                    PageHeight = (int)firstPage.TextureData.Image.Height,
                    PreservedExisting = false
                };

                for (int i = 0; i < existingPackerItems.Count; i++)
                {
                    var p = existingPackerItems[i];
                    result.ExistingPlacements.Add((i, p.SourceX, p.SourceY, p.SourceWidth, p.SourceHeight));
                }
                for (int i = 0; i < selectedPackerItems.Count; i++)
                {
                    var p = selectedPackerItems[i];
                    result.SelectedPlacements.Add((i, p.SourceX, p.SourceY, p.SourceWidth, p.SourceHeight));
                }

                return result;
            }
            catch
            {
                return null;
            }
        }

        private static PackResult FindBestPack(
            List<(int X, int Y, int W, int H)> existingRects,
            List<MagickImage> existingImages,
            List<MagickImage> selectedImages)
        {
            var appendResult = TryAppendPack(existingRects, selectedImages);
            if (appendResult != null)
                return appendResult;

            return TryFullRepack(existingImages, selectedImages);
        }

        private static List<MagickImage> ExtractImages(TextureWorker worker, IEnumerable<UndertaleTexturePageItem> items)
        {
            var images = new List<MagickImage>();
            foreach (var item in items)
            {
                using var img = worker.GetTextureFor(item, null);
                if (img != null)
                    images.Add(new MagickImage(img));
            }
            return images;
        }

        private void CancelPreviewProcessing()
        {
            _previewCts?.Cancel();
            _previewCts?.Dispose();
            _previewCts = null;
        }

        private void UpdateButtonStates()
        {
            if (currentStep == 2)
            {
                ConfirmButton.IsEnabled = !_isProcessing;
                TargetPageCombo.IsEnabled = !_isProcessing;
            }
        }

        private static BitmapSource CreateFrozenBitmapSource(MagickImage magickImage)
        {
            using var temp = new MagickImage(magickImage);
            var gmImage = GMImage.FromMagickImage(temp);
            int stride = gmImage.Width * 4;
            byte[] pixelData = gmImage.GetRawImageDataArray();
            var bitmap = BitmapSource.Create(gmImage.Width, gmImage.Height, 96, 96, PixelFormats.Bgra32, null, pixelData, stride);
            bitmap.Freeze();
            return bitmap;
        }

        private async Task StartPreviewUpdate()
        {
            var selected = SelectedEntries;
            bool allSelected = selected.Count == sourceItems.Length;

            SourcePreviewPanel.Visibility = allSelected ? Visibility.Collapsed : Visibility.Visible;

            if (selected.Count == 0)
            {
                PreviewImage.Source = null;
                PreviewInfoText.Text = "";
                SourcePreviewImage.Source = null;
                SourcePreviewInfoText.Text = "";
                return;
            }

            CancelPreviewProcessing();
            _previewCts = new CancellationTokenSource();
            var ct = _previewCts.Token;

            var targetComboItem = TargetPageCombo.SelectedItem as ComboBoxItem;
            var existingTarget = targetComboItem?.Tag as UndertaleEmbeddedTexture;

            int version = ++_previewVersion;

            IsProcessing = true;
            PreviewInfoText.Text = LocalizationSource.GetString("MergeTP_Processing");

            try
            {
                await UpdatePreviewAsync(selected, allSelected, existingTarget, ct);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                if (version == _previewVersion)
                {
                    PreviewImage.Source = null;
                    PreviewInfoText.Text = "";
                    SourcePreviewImage.Source = null;
                    SourcePreviewInfoText.Text = "";
                }
            }
            finally
            {
                if (version == _previewVersion)
                {
                    IsProcessing = false;
                }
            }
        }

        private async Task UpdatePreviewAsync(List<EntryItem> selected, bool allSelected,
            UndertaleEmbeddedTexture existingTarget, CancellationToken ct)
        {
            var selectedItems = selected.Select(e => e.Item).ToList();

            await Task.Run(() =>
            {
                using var worker = new TextureWorker();

                var existingItemsOnTarget = new List<UndertaleTexturePageItem>();
                var existingImages = new List<MagickImage>();
                var selectedImages = new List<MagickImage>();
                var remainingImages = new List<MagickImage>();

                try
                {
                    if (existingTarget != null)
                    {
                        existingItemsOnTarget = mainWindow.Data.TexturePageItems
                            .Where(x => x.TexturePage == existingTarget)
                            .ToList();
                        existingImages = ExtractImages(worker, existingItemsOnTarget);
                    }
                    ct.ThrowIfCancellationRequested();

                    selectedImages = ExtractImages(worker, selectedItems);
                    ct.ThrowIfCancellationRequested();

                    if (!allSelected)
                    {
                        var remainingItems = sourceItems
                            .Where(si => !selectedItems.Any(se => se == si))
                            .ToList();
                        remainingImages = ExtractImages(worker, remainingItems);

                        var remainingPack = TryFullRepack(remainingImages, new List<MagickImage>());
                        if (remainingPack != null && remainingPack.SelectedPlacements.Count == 0)
                        {
                            using var srcPreviewImg = new MagickImage(MagickColors.Transparent,
                                (uint)remainingPack.PageWidth, (uint)remainingPack.PageHeight);

                            foreach (var p in remainingPack.ExistingPlacements)
                            {
                                if (p.Index < remainingImages.Count)
                                {
                                    using var clone = new MagickImage(remainingImages[p.Index]);
                                    srcPreviewImg.Composite(clone, p.SrcX, p.SrcY, CompositeOperator.Copy);
                                }
                            }

                            var srcBitmap = CreateFrozenBitmapSource(srcPreviewImg);
                            Dispatcher.InvokeAsync(() =>
                            {
                                if (!IsLoaded) return;
                                SourcePreviewImage.Source = srcBitmap;
                                SourcePreviewInfoText.Text = string.Format(
                                    LocalizationSource.GetString("MergeTP_PreviewSize"),
                                    remainingPack.PageWidth, remainingPack.PageHeight);
                            });
                        }
                        else
                        {
                            Dispatcher.InvokeAsync(() =>
                            {
                                if (!IsLoaded) return;
                                SourcePreviewImage.Source = null;
                                SourcePreviewInfoText.Text = "";
                            });
                        }
                    }
                    else
                    {
                        Dispatcher.InvokeAsync(() =>
                        {
                            if (!IsLoaded) return;
                            SourcePreviewImage.Source = null;
                            SourcePreviewInfoText.Text = "";
                        });
                    }

                    ct.ThrowIfCancellationRequested();

                    var existingRects = new List<(int X, int Y, int W, int H)>();
                    for (int i = 0; i < existingItemsOnTarget.Count; i++)
                    {
                        var item = existingItemsOnTarget[i];
                        existingRects.Add((item.SourceX, item.SourceY, item.SourceWidth, item.SourceHeight));
                    }

                    var packResult = TryAppendPackWithPreview(existingRects, existingImages, selectedImages, ct);

                    if (packResult == null)
                    {
                        Dispatcher.InvokeAsync(() =>
                        {
                            if (!IsLoaded) return;
                            PreviewInfoText.Text = LocalizationSource.GetString("MergeTP_FullRepacking");
                        });

                        packResult = TryFullRepack(existingImages, selectedImages);
                        ct.ThrowIfCancellationRequested();

                        if (packResult != null)
                        {
                            using var previewImg = new MagickImage(MagickColors.Transparent,
                                (uint)packResult.PageWidth, (uint)packResult.PageHeight);

                            foreach (var p in packResult.ExistingPlacements)
                            {
                                if (p.Index < existingImages.Count)
                                {
                                    using var clone = new MagickImage(existingImages[p.Index]);
                                    previewImg.Composite(clone, p.SrcX, p.SrcY, CompositeOperator.Copy);
                                }
                            }

                            foreach (var p in packResult.SelectedPlacements)
                            {
                                if (p.Index < selectedImages.Count)
                                {
                                    using var clone = new MagickImage(selectedImages[p.Index]);
                                    previewImg.Composite(clone, p.SrcX, p.SrcY, CompositeOperator.Copy);
                                }
                            }

                            var bitmap = CreateFrozenBitmapSource(previewImg);
                            Dispatcher.InvokeAsync(() =>
                            {
                                if (!IsLoaded) return;
                                PreviewImage.Source = bitmap;
                                PreviewInfoText.Text = string.Format(
                                    LocalizationSource.GetString("MergeTP_PreviewSize"),
                                    packResult.PageWidth, packResult.PageHeight);
                            });
                        }
                        else
                        {
                            Dispatcher.InvokeAsync(() =>
                            {
                                if (!IsLoaded) return;
                                PreviewImage.Source = null;
                                PreviewInfoText.Text = "";
                            });
                        }
                    }
                }
                finally
                {
                    foreach (var img in existingImages) img.Dispose();
                    foreach (var img in selectedImages) img.Dispose();
                    foreach (var img in remainingImages) img.Dispose();
                }
            }, ct);
        }

        private PackResult TryAppendPackWithPreview(
            List<(int X, int Y, int W, int H)> existingRects,
            List<MagickImage> existingImages,
            List<MagickImage> selectedImages,
            CancellationToken ct)
        {
            const int MaxSize = 4096;

            var free = new List<FreeRect> { new(0, 0, MaxSize, MaxSize) };
            foreach (var occ in existingRects)
                SplitFreeList(free, occ.X, occ.Y, occ.W, occ.H);
            PruneContained(free);

            var sorted = selectedImages
                .Select((img, i) => (Img: img, Index: i))
                .OrderByDescending(x => (long)x.Img.Width * x.Img.Height)
                .ThenByDescending(x => Math.Max(x.Img.Width, x.Img.Height))
                .ToList();

            var placements = new (int Index, ushort SrcX, ushort SrcY, ushort SrcW, ushort SrcH)[selectedImages.Count];
            bool[] placed = new bool[selectedImages.Count];
            int maxW = 0, maxH = 0;
            foreach (var r in existingRects)
            {
                maxW = Math.Max(maxW, r.X + r.W);
                maxH = Math.Max(maxH, r.Y + r.H);
            }

            using var previewCanvas = new MagickImage(MagickColors.Transparent, (uint)MaxSize, (uint)MaxSize);

            for (int i = 0; i < existingRects.Count && i < existingImages.Count; i++)
            {
                using var clone = new MagickImage(existingImages[i]);
                previewCanvas.Composite(clone, existingRects[i].X, existingRects[i].Y, CompositeOperator.Copy);
            }

            var initialBitmap = CreateFrozenBitmapSource(previewCanvas);
            int totalToPlace = sorted.Count;
            Dispatcher.InvokeAsync(() =>
            {
                if (!IsLoaded) return;
                PreviewImage.Source = initialBitmap;
                PreviewInfoText.Text = string.Format(
                    LocalizationSource.GetString("MergeTP_PlacingItem"), 0, totalToPlace);
            });

            var stopwatch = Stopwatch.StartNew();
            int placedCount = 0;

            foreach (var (img, origIdx) in sorted)
            {
                ct.ThrowIfCancellationRequested();

                int w = (int)img.Width;
                int h = (int)img.Height;
                if (!TryInsertBSSF(free, w, h, out int bx, out int by))
                    return null;

                SplitFreeList(free, bx, by, w, h);
                if (free.Count > 128)
                    PruneContained(free);

                placements[origIdx] = (origIdx, (ushort)bx, (ushort)by, (ushort)w, (ushort)h);
                placed[origIdx] = true;
                placedCount++;
                maxW = Math.Max(maxW, bx + w);
                maxH = Math.Max(maxH, by + h);

                using var itemClone = new MagickImage(img);
                previewCanvas.Composite(itemClone, bx, by, CompositeOperator.Copy);

                if (stopwatch.ElapsedMilliseconds >= 150)
                {
                    stopwatch.Restart();
                    var bitmap = CreateFrozenBitmapSource(previewCanvas);
                    int capturedCount = placedCount;
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (!IsLoaded) return;
                        PreviewImage.Source = bitmap;
                        PreviewInfoText.Text = string.Format(
                            LocalizationSource.GetString("MergeTP_PlacingItem"), capturedCount, totalToPlace);
                    });
                }
            }

            ct.ThrowIfCancellationRequested();

            int pw = NextPow2(maxW);
            int ph = NextPow2(maxH);
            if (pw > MaxSize || ph > MaxSize)
                return null;

            var result = new PackResult
            {
                PageWidth = pw,
                PageHeight = ph,
                PreservedExisting = true
            };
            for (int i = 0; i < existingRects.Count; i++)
                result.ExistingPlacements.Add((i, (ushort)existingRects[i].X, (ushort)existingRects[i].Y, (ushort)existingRects[i].W, (ushort)existingRects[i].H));
            for (int i = 0; i < placements.Length; i++)
                if (placed[i])
                    result.SelectedPlacements.Add(placements[i]);

            using var finalImg = new MagickImage(MagickColors.Transparent, (uint)pw, (uint)ph);
            finalImg.Composite(previewCanvas, 0, 0, CompositeOperator.Copy);
            var finalBitmap = CreateFrozenBitmapSource(finalImg);
            Dispatcher.InvokeAsync(() =>
            {
                if (!IsLoaded) return;
                PreviewImage.Source = finalBitmap;
                PreviewInfoText.Text = string.Format(
                    LocalizationSource.GetString("MergeTP_PreviewSize"), pw, ph);
            });

            return result;
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (currentStep == 1)
            {
                if (SelectedEntries.Count == 0)
                {
                    this.ShowWarning(LocalizationSource.GetString("MergeTP_NoSelection"));
                    return;
                }
                currentStep = 2;
                UpdateStepUI();
            }
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (currentStep == 2)
            {
                CancelPreviewProcessing();
                currentStep = 1;
                UpdateStepUI();
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            CancelPreviewProcessing();
            DialogResult = false;
            Close();
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing) return;
            PerformMerge();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var entry in Entries)
                entry.IsSelected = true;
            UpdateOverlayColors();
            SyncListBoxSelection();
            UpdateSelectionCount();
        }

        private void DeselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var entry in Entries)
                entry.IsSelected = false;
            UpdateOverlayColors();
            SyncListBoxSelection();
            UpdateSelectionCount();
        }

        private void InvertSelection_Click(object sender, RoutedEventArgs e)
        {
            foreach (var entry in Entries)
                entry.IsSelected = !entry.IsSelected;
            UpdateOverlayColors();
            SyncListBoxSelection();
            UpdateSelectionCount();
        }

        private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible || IsLoaded)
                return;

            if (Settings.Instance.EnableDarkMode)
                MainWindow.SetDarkTitleBarForWindow(this, true, false);
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            CancelPreviewProcessing();
        }

        private void Step1Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(Step1OverlayCanvas);
            foreach (var entry in Entries)
            {
                var item = entry.Item;
                if (pos.X >= item.SourceX && pos.X <= item.SourceX + item.SourceWidth
                    && pos.Y >= item.SourceY && pos.Y <= item.SourceY + item.SourceHeight)
                {
                    entry.IsSelected = !entry.IsSelected;
                    UpdateOverlayColors();
                    SyncListBoxSelection();
                    UpdateSelectionCount();
                    return;
                }
            }
        }

        private void Step1Canvas_MouseMove(object sender, MouseEventArgs e)
        {
        }

        private void Step1Viewbox_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            var mousePos = e.GetPosition(Step1Viewbox);
            var transform = Step1Viewbox.LayoutTransform as MatrixTransform;
            var matrix = transform?.Matrix ?? Matrix.Identity;
            var pow = Math.Pow(2, 1.0 / 8.0);
            var scale = e.Delta >= 0 ? pow : (1.0 / pow);

            if ((matrix.M11 > 0.001 || (matrix.M11 <= 0.001 && scale > 1)) &&
                (matrix.M11 < 1000 || (matrix.M11 >= 1000 && scale < 1)))
            {
                matrix.ScaleAtPrepend(scale, scale, mousePos.X, mousePos.Y);
            }
            Step1Viewbox.LayoutTransform = new MatrixTransform(matrix);
        }

        private void Step1Scroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ExtentHeightChange != 0 || e.ExtentWidthChange != 0)
            {
                double xMousePos = Mouse.GetPosition(Step1ScrollViewer).X;
                double yMousePos = Mouse.GetPosition(Step1ScrollViewer).Y;
                double offsetX = e.HorizontalOffset + xMousePos;
                double offsetY = e.VerticalOffset + yMousePos;

                double oldExtentW = e.ExtentWidth - e.ExtentWidthChange;
                double oldExtentH = e.ExtentHeight - e.ExtentHeightChange;

                double relx = oldExtentW != 0 ? offsetX / oldExtentW : 0;
                double rely = oldExtentH != 0 ? offsetY / oldExtentH : 0;

                offsetX = Math.Max(relx * e.ExtentWidth - xMousePos, 0);
                offsetY = Math.Max(rely * e.ExtentHeight - yMousePos, 0);

                Step1ScrollViewer.ScrollToHorizontalOffset(offsetX);
                Step1ScrollViewer.ScrollToVerticalOffset(offsetY);
            }
        }

        private void PreviewViewbox_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            var mousePos = e.GetPosition(PreviewViewbox);
            var transform = PreviewViewbox.LayoutTransform as MatrixTransform;
            var matrix = transform?.Matrix ?? Matrix.Identity;
            var pow = Math.Pow(2, 1.0 / 8.0);
            var scale = e.Delta >= 0 ? pow : (1.0 / pow);

            if ((matrix.M11 > 0.001 || (matrix.M11 <= 0.001 && scale > 1)) &&
                (matrix.M11 < 1000 || (matrix.M11 >= 1000 && scale < 1)))
            {
                matrix.ScaleAtPrepend(scale, scale, mousePos.X, mousePos.Y);
            }
            PreviewViewbox.LayoutTransform = new MatrixTransform(matrix);
        }

        private void PreviewScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ExtentHeightChange != 0 || e.ExtentWidthChange != 0)
            {
                double xMousePos = Mouse.GetPosition(PreviewScrollViewer).X;
                double yMousePos = Mouse.GetPosition(PreviewScrollViewer).Y;
                double offsetX = e.HorizontalOffset + xMousePos;
                double offsetY = e.VerticalOffset + yMousePos;

                double oldExtentW = e.ExtentWidth - e.ExtentWidthChange;
                double oldExtentH = e.ExtentHeight - e.ExtentHeightChange;

                double relx = oldExtentW != 0 ? offsetX / oldExtentW : 0;
                double rely = oldExtentH != 0 ? offsetY / oldExtentH : 0;

                offsetX = Math.Max(relx * e.ExtentWidth - xMousePos, 0);
                offsetY = Math.Max(rely * e.ExtentHeight - yMousePos, 0);

                PreviewScrollViewer.ScrollToHorizontalOffset(offsetX);
                PreviewScrollViewer.ScrollToVerticalOffset(offsetY);
            }
        }

        private void PerformMerge()
        {
            var selected = SelectedEntries;
            if (selected.Count == 0) return;

            var targetComboItem = TargetPageCombo.SelectedItem as ComboBoxItem;
            bool isNewPage = targetComboItem?.Tag is null;
            UndertaleEmbeddedTexture targetPage = targetComboItem?.Tag as UndertaleEmbeddedTexture;

            if (!isNewPage && targetPage == sourcePage)
            {
                this.ShowError(LocalizationSource.GetString("MergeTP_SamePage"));
                return;
            }

            var originalTargets = new Dictionary<string, (ushort tx, ushort ty, ushort tw, ushort th, ushort bw, ushort bh)>();
            foreach (var entry in selected)
            {
                originalTargets[entry.DisplayName] = (
                    entry.Item.TargetX, entry.Item.TargetY,
                    entry.Item.TargetWidth, entry.Item.TargetHeight,
                    entry.Item.BoundingWidth, entry.Item.BoundingHeight
                );
            }

            using var worker = new TextureWorker();

            var existingItemsOnTarget = new List<UndertaleTexturePageItem>();
            var existingImages = new List<MagickImage>();
            var selectedImages = new List<MagickImage>();

            try
            {
                if (!isNewPage && targetPage != null)
                {
                    existingItemsOnTarget = mainWindow.Data.TexturePageItems
                        .Where(x => x.TexturePage == targetPage)
                        .ToList();
                    existingImages = ExtractImages(worker, existingItemsOnTarget);
                }

                selectedImages = ExtractImages(worker, selected.Select(e => e.Item));

                var existingRects = new List<(int X, int Y, int W, int H)>();
                for (int i = 0; i < existingItemsOnTarget.Count; i++)
                {
                    var item = existingItemsOnTarget[i];
                    existingRects.Add((item.SourceX, item.SourceY, item.SourceWidth, item.SourceHeight));
                }

                var packResult = FindBestPack(existingRects, existingImages, selectedImages);

                if (packResult == null || packResult.SelectedPlacements.Count == 0)
                {
                    this.ShowError(LocalizationSource.GetString("MergeTP_PackFailed"));
                    return;
                }

                using var finalImage = new MagickImage(MagickColors.Transparent, (uint)packResult.PageWidth, (uint)packResult.PageHeight);

                if (!packResult.PreservedExisting)
                {
                    foreach (var p in packResult.ExistingPlacements)
                    {
                        if (p.Index < existingItemsOnTarget.Count)
                        {
                            var existingItem = existingItemsOnTarget[p.Index];
                            existingItem.SourceX = p.SrcX;
                            existingItem.SourceY = p.SrcY;
                            existingItem.SourceWidth = p.SrcW;
                            existingItem.SourceHeight = p.SrcH;
                        }
                        if (p.Index < existingImages.Count)
                        {
                            using var clone = new MagickImage(existingImages[p.Index]);
                            finalImage.Composite(clone, p.SrcX, p.SrcY, CompositeOperator.Copy);
                        }
                    }
                }
                else
                {
                    foreach (var p in packResult.ExistingPlacements)
                    {
                        if (p.Index < existingImages.Count)
                        {
                            using var clone = new MagickImage(existingImages[p.Index]);
                            finalImage.Composite(clone, p.SrcX, p.SrcY, CompositeOperator.Copy);
                        }
                    }
                }

                foreach (var p in packResult.SelectedPlacements)
                {
                    if (p.Index < selected.Count)
                    {
                        var entry = selected[p.Index];
                        var item = entry.Item;
                        item.SourceX = p.SrcX;
                        item.SourceY = p.SrcY;
                        item.SourceWidth = p.SrcW;
                        item.SourceHeight = p.SrcH;
                        if (originalTargets.TryGetValue(entry.DisplayName, out var orig))
                        {
                            item.TargetX = orig.tx;
                            item.TargetY = orig.ty;
                            item.TargetWidth = orig.tw;
                            item.TargetHeight = orig.th;
                            item.BoundingWidth = orig.bw;
                            item.BoundingHeight = orig.bh;
                        }
                        item.TexturePage = targetPage;
                    }
                    if (p.Index < selectedImages.Count)
                    {
                        using var clone = new MagickImage(selectedImages[p.Index]);
                        finalImage.Composite(clone, p.SrcX, p.SrcY, CompositeOperator.Copy);
                    }
                }

                if (isNewPage)
                {
                    targetPage = new UndertaleEmbeddedTexture();
                    targetPage.Name = new UndertaleString($"Texture {mainWindow.Data.EmbeddedTextures.Count}");
                    targetPage.TextureData = new UndertaleEmbeddedTexture.TexData();
                    targetPage.TextureData.Image = GMImage.FromMagickImage(finalImage)
                        .ConvertToFormat(GMImage.ImageFormat.Png);
                    mainWindow.Data.EmbeddedTextures.Add(targetPage);
                }
                else
                {
                    targetPage.TextureData.Image = GMImage.FromMagickImage(finalImage)
                        .ConvertToFormat(targetPage.TextureData.Image?.Format ?? GMImage.ImageFormat.Png);
                }

                foreach (var entry in selected)
                {
                    entry.Item.TexturePage = targetPage;
                }

                bool allSelected = selected.Count == sourceItems.Length;

                if (allSelected)
                {
                    using (MagickImage placeholder = new MagickImage(MagickColors.Transparent, 1, 1))
                    {
                        byte[] pngBytes = placeholder.ToByteArray(MagickFormat.Png);
                        sourcePage.TextureData = new UndertaleEmbeddedTexture.TexData();
                        sourcePage.TextureData.Image = GMImage.FromPng(pngBytes);
                    }
                }
                else
                {
                    var remainingItems = sourceItems
                        .Where(si => !selected.Any(se => se.Item == si))
                        .ToList();

                    var remainingImages = ExtractImages(worker, remainingItems);
                    try
                    {
                        var remainingPack = TryFullRepack(remainingImages, new List<MagickImage>());

                        if (remainingPack != null && remainingPack.SelectedPlacements.Count == 0)
                        {
                            using var repackedImg = new MagickImage(MagickColors.Transparent,
                                (uint)remainingPack.PageWidth, (uint)remainingPack.PageHeight);

                            for (int i = 0; i < remainingPack.ExistingPlacements.Count; i++)
                            {
                                var p = remainingPack.ExistingPlacements[i];
                                if (p.Index < remainingItems.Count)
                                {
                                    remainingItems[p.Index].SourceX = p.SrcX;
                                    remainingItems[p.Index].SourceY = p.SrcY;
                                    remainingItems[p.Index].SourceWidth = p.SrcW;
                                    remainingItems[p.Index].SourceHeight = p.SrcH;
                                }
                                if (p.Index < remainingImages.Count)
                                {
                                    using var clone = new MagickImage(remainingImages[p.Index]);
                                    repackedImg.Composite(clone, p.SrcX, p.SrcY, CompositeOperator.Copy);
                                }
                            }

                            sourcePage.TextureData.Image = GMImage.FromMagickImage(repackedImg)
                                .ConvertToFormat(sourcePage.TextureData.Image?.Format ?? GMImage.ImageFormat.Png);
                        }
                    }
                    finally
                    {
                        foreach (var img in remainingImages) img.Dispose();
                    }
                }

                string targetName = targetPage.Name?.Content ?? targetPage.ToString();
                this.ShowMessage(string.Format(LocalizationSource.GetString("MergeTP_Success"),
                    selected.Count, targetName));

                DialogResult = true;
                Close();
            }
            finally
            {
                foreach (var img in existingImages) img.Dispose();
                foreach (var img in selectedImages) img.Dispose();
            }
        }
    }

    public class EntryItem : INotifyPropertyChanged
    {
        public UndertaleTexturePageItem Item { get; }
        public string DisplayName { get; }
        public Border Border { get; set; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public EntryItem(UndertaleTexturePageItem item, string displayName)
        {
            Item = item;
            DisplayName = displayName;
            _isSelected = true;
        }
    }
}
