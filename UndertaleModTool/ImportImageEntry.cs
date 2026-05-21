using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using UndertaleModLib.Models;
using UndertaleModTool.Localization;

namespace UndertaleModTool
{
    public class ImportImageEntry : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string SpriteName { get; set; }
        public int FrameIndex { get; set; }
        public SpriteType ImageType { get; set; }
        public int ImageWidth { get; set; }
        public int ImageHeight { get; set; }
        public int? TargetWidth { get; set; }
        public int? TargetHeight { get; set; }
        public int? SourceWidth { get; set; }
        public int? SourceHeight { get; set; }
        public bool CanReplaceInPlace { get; set; }
        public bool NeedsRepack => SourceWidth.HasValue && SourceHeight.HasValue &&
            (ImageWidth > SourceWidth.Value || ImageHeight > SourceHeight.Value);
        public bool SpriteExists { get; set; }
        public string StatusText { get; set; }
        public int? OriginalTexturePageIndex { get; set; }
        public int? EstimatedTexturePageIndex { get; set; }

        private bool _isSelected = true;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        private ImportMode _importMode = ImportMode.NewTexturePages;
        public ImportMode ImportMode
        {
            get => _importMode;
            set
            {
                _importMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ImportModeDisplay));
                OnPropertyChanged(nameof(SelectedModeIndex));
                OnPropertyChanged(nameof(TexturePageInfo));
            }
        }

        public List<ImportMode> AvailableModes
        {
            get
            {
                var modes = new List<ImportMode>();
                if (SpriteExists && CanReplaceInPlace)
                    modes.Add(ImportMode.ReplaceInPlace);
                modes.Add(ImportMode.NewTexturePages);
                if (SpriteExists)
                    modes.Add(ImportMode.KeepOriginalTexturePage);
                return modes;
            }
        }

        public List<string> AvailableModeDisplays
        {
            get
            {
                var displays = new List<string>();
                if (SpriteExists && CanReplaceInPlace)
                    displays.Add(LocalizationSource.GetString("ImportGraphics_ReplaceInPlace"));
                displays.Add(LocalizationSource.GetString("ImportGraphics_NewTexturePages"));
                if (SpriteExists)
                    displays.Add(LocalizationSource.GetString("ImportGraphics_KeepOriginalTexturePage"));
                return displays;
            }
        }

        public int SelectedModeIndex
        {
            get => AvailableModes.IndexOf(ImportMode);
            set
            {
                if (value >= 0 && value < AvailableModes.Count)
                    ImportMode = AvailableModes[value];
            }
        }

        private int _originPosition = 0;
        public int OriginPosition
        {
            get => _originPosition;
            set { _originPosition = value; OnPropertyChanged(); }
        }

        private float _animationSpeed = 1f;
        public float AnimationSpeed
        {
            get => _animationSpeed;
            set { _animationSpeed = value; OnPropertyChanged(); }
        }

        private int _playbackType = 1;
        public int PlaybackType
        {
            get => _playbackType;
            set { _playbackType = value; OnPropertyChanged(); }
        }

        private bool _isSpecialType = false;
        public bool IsSpecialType
        {
            get => _isSpecialType;
            set { _isSpecialType = value; OnPropertyChanged(); }
        }

        private uint _specialVersion = 1;
        public uint SpecialVersion
        {
            get => _specialVersion;
            set { _specialVersion = value; OnPropertyChanged(); }
        }

        public string ImageTypeDisplay => ImageType switch
        {
            SpriteType.Sprite => LocalizationSource.GetString("ImportGraphics_TypeSprite"),
            SpriteType.Background => LocalizationSource.GetString("ImportGraphics_TypeBackground"),
            SpriteType.Font => LocalizationSource.GetString("ImportGraphics_TypeFont"),
            _ => LocalizationSource.GetString("ImportGraphics_TypeUnknown")
        };

        public string ImportModeDisplay => ImportMode switch
        {
            ImportMode.ReplaceInPlace => LocalizationSource.GetString("ImportGraphics_ReplaceInPlace"),
            ImportMode.NewTexturePages => LocalizationSource.GetString("ImportGraphics_NewTexturePages"),
            ImportMode.KeepOriginalTexturePage => LocalizationSource.GetString("ImportGraphics_KeepOriginalTexturePage"),
            _ => "Unknown"
        };

        public string DimensionInfo
        {
            get
            {
                string img = $"{ImageWidth}x{ImageHeight}";
                if (TargetWidth.HasValue && TargetHeight.HasValue)
                    return $"{img} -> {TargetWidth}x{TargetHeight}";
                return img;
            }
        }

        public string TexturePageInfo
        {
            get
            {
                if (!OriginalTexturePageIndex.HasValue)
                    return "-";

                string original = $"#{OriginalTexturePageIndex.Value}";

                if (ImportMode == ImportMode.ReplaceInPlace)
                {
                    return $"{original} → {original}";
                }

                if (ImportMode == ImportMode.KeepOriginalTexturePage)
                {
                    if (CanReplaceInPlace)
                        return $"{original} → {original}";
                    if (!NeedsRepack)
                        return $"{original} → {original} (composite)";
                    return $"{original} → {original} (repack)";
                }

                if (EstimatedTexturePageIndex.HasValue)
                    return $"{original} → #{EstimatedTexturePageIndex.Value} (new)";

                return $"{original} → new";
            }
        }
    }
}
