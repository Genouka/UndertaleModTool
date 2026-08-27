using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UndertaleModLib.Wad;

namespace UndertaleModTool.Wad
{
    /// <summary>
    /// Base view model for one parsed wad chunk. Concrete subclasses build the entry rows
    /// for their chunk type; the shared <see cref="Editors.WadChunkEditor"/> renders the entries,
    /// the reflected property readout and the raw-byte / STRG previews.
    /// </summary>
    public abstract class WadChunkViewModel : ObservableObject
    {
        private WadEntryViewModel _selectedEntry;
        private readonly List<WadPropertyViewModel> _properties = new();

        protected WadChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk)
        {
            Wad = wad ?? throw new ArgumentNullException(nameof(wad));
            Chunk = chunk;
            Name = header.Name;
            OffsetText = $"0x{header.Offset:X8}";
            LengthText = header.Length.ToString("N0", CultureInfo.InvariantCulture);
            KindText = chunk is null ? "unknown" : chunk is WadRawChunk ? "raw" : "parsed";
            EntriesCountText = chunk is null
                ? "—"
                : EntryCountOf(chunk)?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";

            string fileBase = string.IsNullOrEmpty(wad.FilePath) ? "wad" : Path.GetFileNameWithoutExtension(wad.FilePath);
            Title = $"{fileBase}/{Name}";

            Entries = BuildEntries() ?? new ObservableCollection<WadEntryViewModel>();
            Info = BuildInfo();
            HexText = BuildHexText();
            StringsText = BuildStringsText();
        }

        /// <summary>The owning wad (kept alive by the editor tab).</summary>
        protected UndertaleWadFile Wad { get; }

        /// <summary>The parsed chunk (null for a missing/unknown chunk).</summary>
        protected WadChunk Chunk { get; }

        public string Name { get; }
        public string OffsetText { get; }
        public string LengthText { get; }
        public string KindText { get; }
        public string EntriesCountText { get; }

        /// <summary>Tab title for the per-chunk editors (file base + chunk name).</summary>
        public string Title { get; }

        /// <summary>Entry rows of this chunk.</summary>
        public ObservableCollection<WadEntryViewModel> Entries { get; }

        /// <summary>Key/value fields shown in the chunk header.</summary>
        public IReadOnlyList<WadInfoItem> Info { get; }

        /// <summary>Hex preview of the chunk payload (first 8 KiB).</summary>
        public string HexText { get; }

        /// <summary>STRG string pool listing (empty for non-STRG chunks).</summary>
        public string StringsText { get; }

        /// <summary>Reflected properties of the selected entry.</summary>
        public IReadOnlyList<WadPropertyViewModel> Properties => _properties;

        public WadEntryViewModel SelectedEntry
        {
            get => _selectedEntry;
            set
            {
                if (SetProperty(ref _selectedEntry, value))
                    RebuildProperties(value);
            }
        }

        protected abstract ObservableCollection<WadEntryViewModel> BuildEntries();

        protected virtual IReadOnlyList<WadInfoItem> BuildInfo()
        {
            return new List<WadInfoItem>
            {
                new("Offset", OffsetText),
                new("Length", LengthText),
                new("Kind", KindText),
                new("Entries", Entries.Count.ToString("N0", CultureInfo.InvariantCulture)),
            };
        }

        private void RebuildProperties(WadEntryViewModel entry)
        {
            _properties.Clear();
            if (entry is null)
            {
                OnPropertyChanged(nameof(Properties));
                return;
            }
            object obj = entry.Payload;
            if (obj is null)
            {
                _properties.Add(new WadPropertyViewModel("(no object)", ""));
                OnPropertyChanged(nameof(Properties));
                return;
            }
            foreach (PropertyInfo info in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod is not null)
                         .OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                object value;
                try
                {
                    value = info.GetValue(obj);
                }
                catch (Exception ex)
                {
                    value = $"<{ex.GetType().Name}>";
                }
                _properties.Add(new WadPropertyViewModel(info.Name, FormatValue(value, 0)));
            }
            OnPropertyChanged(nameof(Properties));
        }

        // ------------------------------------------------------------------
        // shared formatting / browsing helpers

        /// <summary>Flattens a reflected value into a readable string.</summary>
        internal static string FormatValue(object value, int depth)
        {
            switch (value)
            {
                case null:
                    return "(null)";
                case string s:
                    return Shorten(s);
                case byte[] bytes:
                    return $"byte[{bytes.Length}] {HexSnippet(bytes, 32)}";
                case bool b:
                    return b ? "True" : "False";
                case sbyte or byte or short or ushort or int or uint or long or ulong:
                    return Convert.ToString(value, CultureInfo.InvariantCulture);
                case float f:
                    return f.ToString("R", CultureInfo.InvariantCulture);
                case double d:
                    return d.ToString("R", CultureInfo.InvariantCulture);
                case decimal m:
                    return m.ToString(CultureInfo.InvariantCulture);
                case ICollection collection:
                {
                    int count = collection.Count;
                    var sb = new StringBuilder();
                    sb.Append('[').Append(count).Append("] ");
                    int shown = 0;
                    foreach (object item in collection)
                    {
                        if (shown >= 6)
                        {
                            sb.Append($"… +{count - shown} more");
                            break;
                        }
                        if (shown > 0)
                            sb.Append(", ");
                        sb.Append(FormatValue(item, depth + 1));
                        shown++;
                    }
                    return Shorten(sb.ToString(), 240);
                }
                default:
                {
                    string text;
                    try
                    {
                        text = Convert.ToString(value, CultureInfo.InvariantCulture);
                    }
                    catch
                    {
                        text = value.GetType().Name;
                    }
                    return Shorten(text, 200);
                }
            }
        }

        internal static string Shorten(string value, int max = 200)
        {
            if (string.IsNullOrEmpty(value))
                return value;
            return value.Length <= max ? value : value[..max] + "…";
        }

        internal static string Snippet(string value, int max = 96)
        {
            if (string.IsNullOrEmpty(value))
                return value;
            return value.Length <= max ? value : value[..max] + "…";
        }

        internal static string HexSnippet(byte[] data, int max = 16)
        {
            if (data is null)
                return "—";
            int take = Math.Min(data.Length, max);
            var sb = new StringBuilder(take * 3);
            for (int i = 0; i < take; i++)
            {
                if (i > 0)
                    sb.Append(' ');
                sb.Append(data[i].ToString("X2"));
            }
            if (data.Length > take)
                sb.Append(" …");
            return sb.ToString();
        }

        private string BuildHexText()
        {
            if (Chunk is null || Wad is null)
                return "";
            const int maxBytes = 8192;
            byte[] data = Wad.GetChunkBytes(Name, maxBytes);
            if (data is null)
                return "(no data)";

            var sb = new StringBuilder(data.Length * 4 + 16);
            for (int i = 0; i < data.Length; i += 16)
            {
                sb.Append($"{i:X8}  ");
                int rowEnd = Math.Min(i + 16, data.Length);
                for (int j = i; j < rowEnd; j++)
                    sb.Append(data[j].ToString("X2")).Append(' ');
                sb.Append(new string(' ', (16 - (rowEnd - i)) * 3));
                for (int j = i; j < rowEnd; j++)
                {
                    byte b = data[j];
                    sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                }
                sb.AppendLine();
            }
            if (Chunk.Length > data.Length)
                sb.AppendLine($"… {Chunk.Length - data.Length:N0} more bytes not shown");
            return sb.ToString();
        }

        private string BuildStringsText()
        {
            if (Chunk is not WadStrgChunk strg || Wad is null)
                return "";
            var sb = new StringBuilder();
            sb.AppendLine($"Count hint: {strg.CountHint}, flags: {strg.Flags}, records: {strg.RecordOffsets.Count}");
            sb.AppendLine();
            foreach (uint off in strg.RecordOffsets)
                sb.AppendLine($"0x{off:X8}: {Wad.ReadWadString(off)}");
            return sb.ToString();
        }

        private static uint? EntryCountOf(WadChunk chunk) => chunk switch
        {
            WadStrgChunk c => (uint)c.RecordOffsets.Count,
            WadPrjtChunk c => c.Entry is null ? 0U : 1U,
            WadRrefChunk c => (uint)c.Entries.Count,
            WadTagsChunk c => (uint)c.Tags.Count,
            WadEmbeddedImagesChunk c => c.Count,
            WadAudoChunk c => c.Count,
            WadTxTrChunk c => c.Count,
            WadTpagChunk c => c.Count,
            WadTginChunk c => c.Count,
            WadRoomChunk c => c.Count,
            WadSeqnChunk c => c.Count,
            WadAcrvChunk c => c.Count,
            WadTmlnChunk c => c.Count,
            WadPathChunk c => c.Count,
            WadPsysChunk c => c.Count,
            WadShdrChunk c => c.Count,
            WadObjtChunk c => c.Count,
            WadOptnChunk c => c.Count,
            WadUilrChunk c => c.Count,
            WadExtnChunk c => c.Count,
            WadFedsChunk c => c.Count,
            WadResourceChunk c => c.Count,
            WadSprtChunk c => c.Count,
            WadBgndChunk c => c.Count,
            WadSondChunk c => c.Count,
            WadAgrpChunk c => c.Count,
            WadFontChunk c => c.Count,
            _ => null,
        };

        /// <summary>Creates the per-type view model for the given chunk.</summary>
        public static WadChunkViewModel Create(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk)
        {
            if (chunk is null)
                return new WadRawChunkViewModel(wad, header, null);
            switch (chunk)
            {
                case WadStrgChunk c: return new WadStrgChunkViewModel(wad, header, c);
                case WadPrjtChunk c: return new WadPrjtChunkViewModel(wad, header, c);
                case WadRrefChunk c: return new WadRrefChunkViewModel(wad, header, c);
                case WadTagsChunk c: return new WadTagsChunkViewModel(wad, header, c);
                case WadEmbeddedImagesChunk c: return new WadEmbeddedImagesChunkViewModel(wad, header, c);
                case WadAudoChunk c: return new WadAudoChunkViewModel(wad, header, c);
                case WadTxTrChunk c: return new WadTxTrChunkViewModel(wad, header, c);
                case WadTpagChunk c: return new WadTpagChunkViewModel(wad, header, c);
                case WadTginChunk c: return new WadTginChunkViewModel(wad, header, c);
                case WadRoomChunk c: return new WadRoomChunkViewModel(wad, header, c);
                case WadSeqnChunk c: return new WadSeqnChunkViewModel(wad, header, c);
                case WadAcrvChunk c: return new WadAcrvChunkViewModel(wad, header, c);
                case WadTmlnChunk c: return new WadTmlnChunkViewModel(wad, header, c);
                case WadPathChunk c: return new WadPathChunkViewModel(wad, header, c);
                case WadPsysChunk c: return new WadPsysChunkViewModel(wad, header, c);
                case WadShdrChunk c: return new WadShdrChunkViewModel(wad, header, c);
                case WadObjtChunk c: return new WadObjtChunkViewModel(wad, header, c);
                case WadOptnChunk c: return new WadOptnChunkViewModel(wad, header, c);
                case WadUilrChunk c: return new WadUilrChunkViewModel(wad, header, c);
                case WadExtnChunk c: return new WadExtnChunkViewModel(wad, header, c);
                case WadFedsChunk c: return new WadFedsChunkViewModel(wad, header, c);
                case WadResourceChunk c: return new WadScriptChunkViewModel(wad, header, c);
                case WadSprtChunk c: return new WadSprtChunkViewModel(wad, header, c);
                case WadBgndChunk c: return new WadBgndChunkViewModel(wad, header, c);
                case WadSondChunk c: return new WadSondChunkViewModel(wad, header, c);
                case WadAgrpChunk c: return new WadAgrpChunkViewModel(wad, header, c);
                case WadFontChunk c: return new WadFontChunkViewModel(wad, header, c);
                default: return new WadRawChunkViewModel(wad, header, chunk);
            }
        }
    }

    /// <summary>Fallback view model for unknown or unparsed chunks.</summary>
    public sealed class WadRawChunkViewModel : WadChunkViewModel
    {
        internal WadRawChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries() => null;
    }

    /// <summary>STRG: the string pool.</summary>
    public sealed class WadStrgChunkViewModel : WadChunkViewModel
    {
        internal WadStrgChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadStrgChunk strg)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (uint off in strg.RecordOffsets)
                list.Add(new WadEntryViewModel(i++, $"0x{off:X8}", Snippet(Wad.ReadWadString(off)), new WadStringRecord(off, Wad.ReadWadString(off))));
            return new ObservableCollection<WadEntryViewModel>(list);
        }

        protected override IReadOnlyList<WadInfoItem> BuildInfo()
        {
            var info = new List<WadInfoItem>(base.BuildInfo());
            if (Chunk is WadStrgChunk strg)
            {
                info.Add(new WadInfoItem("Count hint", strg.CountHint.ToString()));
                info.Add(new WadInfoItem("Flags", strg.Flags.ToString()));
            }
            return info;
        }
    }

    /// <summary>One STRG record (for the property pane).</summary>
    public sealed class WadStringRecord
    {
        public WadStringRecord(uint offset, string value)
        {
            Offset = offset;
            Value = value;
        }

        public uint Offset { get; }
        public string Value { get; }
    }

    /// <summary>PRJT: project information.</summary>
    public sealed class WadPrjtChunkViewModel : WadChunkViewModel
    {
        internal WadPrjtChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadPrjtChunk prjt || prjt.Entry is null)
                return null;
            var entry = prjt.Entry;
            return new ObservableCollection<WadEntryViewModel>
            {
                new(0, entry.ProjectName ?? "(project)",
                    $"{entry.Width}x{entry.Height} buildType={entry.BuildType} startRoom=0x{entry.StartRoomRef:X8} rooms={entry.RoomOrderCount} files={entry.FileList?.Count ?? 0}",
                    entry),
            };
        }
    }

    /// <summary>RREF: resource reference registry.</summary>
    public sealed class WadRrefChunkViewModel : WadChunkViewModel
    {
        internal WadRrefChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadRrefChunk rref)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (WadRrefEntry entry in rref.Entries)
                list.Add(new WadEntryViewModel(i++, entry.Name, $"type={entry.ResourceType} key={entry.Key}", entry));
            return new ObservableCollection<WadEntryViewModel>(list);
        }
    }

    /// <summary>TAGS: tag manager records.</summary>
    public sealed class WadTagsChunkViewModel : WadChunkViewModel
    {
        internal WadTagsChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadTagsChunk tags)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (WadTagRecord tag in tags.Tags)
                list.Add(new WadEntryViewModel(i++, $"#{i - 1}", $"type={tag.ResourceType} key={tag.Key} detail={HexSnippet(tag.Detail)}", tag));
            return new ObservableCollection<WadEntryViewModel>(list);
        }
    }

    /// <summary>EMBI: embedded images.</summary>
    public sealed class WadEmbeddedImagesChunkViewModel : WadChunkViewModel
    {
        internal WadEmbeddedImagesChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadEmbeddedImagesChunk embi)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (WadEmbeddedImage image in embi.Images)
                list.Add(new WadEntryViewModel(i++, image.Name, $"data=0x{image.DataOffset:X8}", image));
            return new ObservableCollection<WadEntryViewModel>(list);
        }
    }

    /// <summary>AUDO: audio blobs.</summary>
    public sealed class WadAudoChunkViewModel : WadChunkViewModel
    {
        internal WadAudoChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadAudoChunk audo)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (WadAudioBlob blob in audo.Audio)
                list.Add(new WadEntryViewModel(i++, $"blob #{i - 1}", $"{blob.Data.Length:N0} bytes @0x{blob.Offset:X8}", blob));
            return new ObservableCollection<WadEntryViewModel>(list);
        }
    }

    /// <summary>TXTR: texture pages.</summary>
    public sealed class WadTxTrChunkViewModel : WadChunkViewModel
    {
        internal WadTxTrChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadTxTrChunk textures)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (WadTextureEntry tex in textures.Textures)
                list.Add(new WadEntryViewModel(i++, $"0x{tex.TexId:X8}", $"{tex.Width}x{tex.Height} fmt={tex.Format} blob={tex.BlobSize:N0} qoi={tex.QoiWidth}x{tex.QoiHeight}", tex));
            return new ObservableCollection<WadEntryViewModel>(list);
        }
    }

    /// <summary>TPAG: texture page region records.</summary>
    public sealed class WadTpagChunkViewModel : WadChunkViewModel
    {
        internal WadTpagChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadTpagChunk pages)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (WadTexturePage page in pages.Pages)
                list.Add(new WadEntryViewModel(i++, $"#{page.PageX},{page.PageY}", $"{page.RectW}x{page.RectH} src=({page.SrcOffX},{page.SrcOffY}) tex=0x{page.PageTexId:X8}", page));
            return new ObservableCollection<WadEntryViewModel>(list);
        }
    }

    /// <summary>TGIN: texture groups.</summary>
    public sealed class WadTginChunkViewModel : WadChunkViewModel
    {
        internal WadTginChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override IReadOnlyList<WadInfoItem> BuildInfo()
        {
            var info = new List<WadInfoItem>(base.BuildInfo());
            if (Chunk is WadTginChunk tg)
                info.Add(new WadInfoItem("Version", tg.Version.ToString()));
            return info;
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadTginChunk groups)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (WadTextureGroup group in groups.Groups)
                list.Add(new WadEntryViewModel(i++, group.Name, $"comp='{group.Compression}' pages={group.Pages?.Count ?? 0} categories={group.Categories?.Count ?? 0}", group));
            return new ObservableCollection<WadEntryViewModel>(list);
        }
    }

    /// <summary>ROOM: rooms with views/instances/layers/components.</summary>
    public sealed class WadRoomChunkViewModel : WadChunkViewModel
    {
        internal WadRoomChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadRoomChunk rooms)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (WadRoomEntry room in rooms.Rooms)
                list.Add(new WadEntryViewModel(i++, room.Name, $"{room.Width}x{room.Height} views={room.Views?.Count ?? 0} instances={room.Instances?.Count ?? 0} layers={room.Layers?.Count ?? 0}",
                    room.Error is null ? room : null));
            if (rooms.Rooms.Any(r => r.Error is not null))
                list.Add(new WadEntryViewModel(-1, "⚠ parse errors", string.Join("; ", rooms.Rooms.Where(r => r.Error is not null).Select(r => $"{r.Name}: {Shorten(r.Error.Message)}")), null));
            return new ObservableCollection<WadEntryViewModel>(list);
        }
    }

    /// <summary>SEQN: sequences with tracks and keyframes.</summary>
    public sealed class WadSeqnChunkViewModel : WadChunkViewModel
    {
        internal WadSeqnChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadSeqnChunk seqn)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (WadSeqnEntry seq in seqn.Entries)
                list.Add(new WadEntryViewModel(i++, seq.Name, $"len={seq.Length} speed={seq.PlaybackSpeed} tracks={seq.Tracks?.Tracks?.Count ?? 0} moments={seq.Moments?.Keyframes?.Count ?? 0} end=0x{seq.ParseEndOffset:X8}",
                    seq.Error is null ? seq : null));
            if (seqn.Entries.Any(s => s.Error is not null))
                list.Add(new WadEntryViewModel(-1, "⚠ parse errors", string.Join("; ", seqn.Entries.Where(s => s.Error is not null).Select(s => $"{s.Name}: {Shorten(s.Error.Message)}")), null));
            return new ObservableCollection<WadEntryViewModel>(list);
        }
    }

    /// <summary>ACRV: animation curves.</summary>
    public sealed class WadAcrvChunkViewModel : WadChunkViewModel
    {
        internal WadAcrvChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadAcrvChunk acrv)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (WadAcrvCurve curve in acrv.Curves)
                list.Add(new WadEntryViewModel(i++, curve.Name, $"graphType={curve.GraphType} channels={curve.Channels?.Count ?? 0}", curve));
            return new ObservableCollection<WadEntryViewModel>(list);
        }
    }

    /// <summary>TMLN: timelines.</summary>
    public sealed class WadTmlnChunkViewModel : WadChunkViewModel
    {
        internal WadTmlnChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadTmlnChunk timelines)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (WadTmlnEntry tl in timelines.Entries)
                list.Add(new WadEntryViewModel(i++, tl.Name, $"moments={tl.Moments?.Count ?? 0}", tl));
            return new ObservableCollection<WadEntryViewModel>(list);
        }
    }

    /// <summary>PATH: paths.</summary>
    public sealed class WadPathChunkViewModel : WadChunkViewModel
    {
        internal WadPathChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadPathChunk paths)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (WadPathEntry path in paths.Entries)
                list.Add(new WadEntryViewModel(i++, path.Name, $"kind={path.Kind} closed={path.Closed} precision={path.Precision} points={path.Points?.Count ?? 0}", path));
            return new ObservableCollection<WadEntryViewModel>(list);
        }
    }

    /// <summary>PSYS: particle systems.</summary>
    public sealed class WadPsysChunkViewModel : WadChunkViewModel
    {
        internal WadPsysChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadPsysChunk systems)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (WadPsysEntry part in systems.Entries)
                list.Add(new WadEntryViewModel(i++, part.Name, $"origin=({part.OriginX},{part.OriginY}) order={part.DrawOrder} emitters={part.Emitters?.Count ?? 0}", part));
            return new ObservableCollection<WadEntryViewModel>(list);
        }
    }

    /// <summary>SHDR: shaders.</summary>
    public sealed class WadShdrChunkViewModel : WadChunkViewModel
    {
        internal WadShdrChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadShdrChunk shaders)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (WadShdrEntry sh in shaders.Entries)
                list.Add(new WadEntryViewModel(i++, sh.Name, $"vertex={sh.VertexLen:N0}B fragment={sh.FragmentLen:N0}B", sh));
            return new ObservableCollection<WadEntryViewModel>(list);
        }
    }

    /// <summary>OBJT: game objects.</summary>
    public sealed class WadObjtChunkViewModel : WadChunkViewModel
    {
        internal WadObjtChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadObjtChunk objects)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (WadObjtEntry obj in objects.Entries)
                list.Add(new WadEntryViewModel(i++, obj.Name, $"parent={obj.ParentIndex} persistent={obj.Persistent} events={obj.Events?.Count ?? 0} components={obj.Components?.Count ?? 0}", obj));
            return new ObservableCollection<WadEntryViewModel>(list);
        }
    }

    /// <summary>OPTN: game options.</summary>
    public sealed class WadOptnChunkViewModel : WadChunkViewModel
    {
        internal WadOptnChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadOptnChunk options)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (WadOptnEntry opt in options.Entries)
                list.Add(new WadEntryViewModel(i++, $"options #{i - 1}", $"version={opt.Version} flags=0x{opt.FlagsA:X8}/0x{opt.FlagsB:X8} depth={opt.ColorDepth} freq={opt.Frequency}", opt));
            return new ObservableCollection<WadEntryViewModel>(list);
        }
    }

    /// <summary>UILR: UI layers.</summary>
    public sealed class WadUilrChunkViewModel : WadChunkViewModel
    {
        internal WadUilrChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadUilrChunk uilr)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (WadUilrLayer layer in uilr.Layers)
                list.Add(new WadEntryViewModel(i++, layer.Name, $"type={layer.Type} children={layer.Children?.Count ?? 0} end=0x{layer.ChildrenRegionEnd:X8}", layer));
            return new ObservableCollection<WadEntryViewModel>(list);
        }
    }

    /// <summary>EXTN: extensions.</summary>
    public sealed class WadExtnChunkViewModel : WadChunkViewModel
    {
        internal WadExtnChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadExtnChunk extn)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (WadExtnEntry ext in extn.Entries)
                list.Add(new WadEntryViewModel(i++, ext.Name, $"version='{ext.Version}' class='{ext.ClassName}' files={ext.Files?.Count ?? 0} options={ext.Options?.Count ?? 0}", ext));
            return new ObservableCollection<WadEntryViewModel>(list);
        }
    }

    /// <summary>FEDS: FEED resolver elements.</summary>
    public sealed class WadFedsChunkViewModel : WadChunkViewModel
    {
        internal WadFedsChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadFedsChunk feds)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (WadFedsElement el in feds.Elements)
                list.Add(new WadEntryViewModel(i++, el.Name, $"regionEnd=0x{el.RegionEnd:X8}", el));
            return new ObservableCollection<WadEntryViewModel>(list);
        }
    }

    /// <summary>SCPT (and other generic resource chunks): entries with names + payload refs.</summary>
    public sealed class WadScriptChunkViewModel : WadChunkViewModel
    {
        internal WadScriptChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadResourceChunk resources)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (WadResourceEntry res in resources.Entries)
            {
                string summary = res switch
                {
                    WadScriptEntry script => $"code=0x{script.CodeRef:X8}",
                    //_ => res.Error is not null ? $"error: {Shorten(res.Error.Message)}" : "unparsed",
                };
                list.Add(new WadEntryViewModel(i++, res.Name ?? $"(entry {i - 1})", summary, res));
            }
            return new ObservableCollection<WadEntryViewModel>(list);
        }
    }

    /// <summary>SPRT: sprites.</summary>
    public sealed class WadSprtChunkViewModel : WadChunkViewModel
    {
        internal WadSprtChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadSprtChunk sprites)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (WadSprtEntry spr in sprites.Entries)
                list.Add(new WadEntryViewModel(i++, spr.Name, $"{spr.Width}x{spr.Height} bbox=({spr.BBoxLeft},{spr.BBoxTop})..({spr.BBoxRight},{spr.BBoxBottom}) frames={spr.FrameCount} nine=0x{spr.NineSliceOffset:X8}", spr));
            return new ObservableCollection<WadEntryViewModel>(list);
        }
    }

    /// <summary>BGND: tilesets.</summary>
    public sealed class WadBgndChunkViewModel : WadChunkViewModel
    {
        internal WadBgndChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadBgndChunk tilesets)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (WadBgndEntry bg in tilesets.Entries)
                list.Add(new WadEntryViewModel(i++, bg.Name, $"{bg.TileWidth}x{bg.TileHeight} cols={bg.Columns} frames={bg.Frames} count={bg.TileCount}", bg));
            return new ObservableCollection<WadEntryViewModel>(list);
        }
    }

    /// <summary>SOND: sounds.</summary>
    public sealed class WadSondChunkViewModel : WadChunkViewModel
    {
        internal WadSondChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadSondChunk sounds)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (WadSondEntry snd in sounds.Entries)
                list.Add(new WadEntryViewModel(i++, snd.Name, $"fmt={snd.FormatCode} '{snd.FileName}' vol={snd.Volume}", snd));
            return new ObservableCollection<WadEntryViewModel>(list);
        }
    }

    /// <summary>AGRP: audio groups.</summary>
    public sealed class WadAgrpChunkViewModel : WadChunkViewModel
    {
        internal WadAgrpChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadAgrpChunk groups)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (WadAgrpEntry ag in groups.Entries)
                list.Add(new WadEntryViewModel(i++, ag.Name, ag.ExportDir, ag));
            return new ObservableCollection<WadEntryViewModel>(list);
        }
    }

    /// <summary>FONT: fonts.</summary>
    public sealed class WadFontChunkViewModel : WadChunkViewModel
    {
        internal WadFontChunkViewModel(UndertaleWadFile wad, WadChunkHeader header, WadChunk chunk) : base(wad, header, chunk)
        {
        }

        protected override ObservableCollection<WadEntryViewModel> BuildEntries()
        {
            if (Chunk is not WadFontChunk fonts)
                return null;
            var list = new List<WadEntryViewModel>();
            int i = 0;
            foreach (WadFontEntry font in fonts.Entries)
                list.Add(new WadEntryViewModel(i++, font.Name, $"size={font.Size} bold={font.Bold} italic={font.Italic}", font));
            return new ObservableCollection<WadEntryViewModel>(list);
        }
    }
}