using Microsoft.Win32;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using UndertaleModLib.Wad;

namespace UndertaleModTool
{
    /// <summary>
    /// THIS IS A TEMP TOOL, AND WILL BE DELETED IN THE FUTURE!!!
    /// Editor for parsed GameMaker GMRT <c>.wad</c> files (see
    /// <see cref="UndertaleWadFile"/>). Shows the chunk table, the entries of every parsed
    /// chunk, a reflection-based property readout of the selected entry, the STRG string pool,
    /// and a raw-byte preview of the selected chunk.
    /// </summary>
    public partial class WadEditor : DataUserControl
    {
        private UndertaleWadFile _wad;

        public WadEditor()
        {
            InitializeComponent();
        }

        private void WadEditor_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            _wad = DataContext as UndertaleWadFile;
            Refresh();
        }

        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new()
            {
                Title = "Open GameMaker WAD file",
                Filter = "WAD files (*.wad)|*.wad|All files (*.*)|*.*",
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                return;
            try
            {
                _wad?.Dispose();
                _wad = UndertaleWadFile.Load(dialog.FileName);
                Refresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), $"Could not open the WAD file:\n{ex.Message}",
                    "WAD Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Refresh()
        {
            ChunkList.ItemsSource = null;
            EntriesGrid.ItemsSource = null;
            EntriesTitle.Text = "Entries";
            PropsList.ItemsSource = null;
            HexBox.Text = "";
            StringsBox.Text = "";

            if (_wad is null)
            {
                FileLabel.Text = "";
                SummaryLabel.Text = "";
                return;
            }

            FileLabel.Text = string.IsNullOrEmpty(_wad.FilePath) ? "(WAD)" : Path.GetFileName(_wad.FilePath);
            SummaryLabel.Text = string.Format(CultureInfo.InvariantCulture,
                "FORM {0:N0} bytes · {1} chunks · {2:N0} strings",
                _wad.FormLength, _wad.ChunkHeaders.Count, _wad.Strings?.RecordOffsets?.Count ?? 0);

            var rows = new List<WadChunkRow>();
            foreach (WadChunkHeader header in _wad.ChunkHeaders)
            {
                _wad.Chunks.TryGetValue(header.Name, out WadChunk chunk);
                rows.Add(WadChunkRow.From(header, chunk));
            }
            ChunkList.ItemsSource = rows;
        }

        private void ChunkList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            EntriesGrid.ItemsSource = null;
            PropsList.ItemsSource = null;

            if (ChunkList.SelectedItem is not WadChunkRow row || _wad is null)
            {
                EntriesTitle.Text = "Entries";
                HexBox.Text = "";
                StringsBox.Text = "";
                return;
            }

            List<WadEntryRow> entries = BuildEntries(row.Chunk);
            EntriesGrid.ItemsSource = entries;
            EntriesTitle.Text = entries is null
                ? "Entries — (raw chunk)"
                : $"Entries — {entries.Count:N0}";
            HexBox.Text = HexDump(row.Chunk);
            StringsBox.Text = BuildStringsText(row.Chunk);
        }

        private void EntriesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (EntriesGrid.SelectedItem is not WadEntryRow row)
            {
                PropsList.ItemsSource = null;
                return;
            }
            PropsList.ItemsSource = BuildProperties(row.Object);
        }

        // ------------------------------------------------------------------
        // helpers

        private static string Snippet(string value, int max = 96)
        {
            if (string.IsNullOrEmpty(value))
                return value;
            return value.Length <= max ? value : value[..max] + "…";
        }

        private static string Shorten(string value, int max = 160)
        {
            if (string.IsNullOrEmpty(value))
                return value;
            return value.Length <= max ? value : value[..max] + "…";
        }

        private List<WadEntryRow> BuildEntries(WadChunk chunk)
        {
            if (chunk is null)
                return null;
            var list = new List<WadEntryRow>();

            switch (chunk)
            {
                case WadStrgChunk strg:
                {
                    int i = 0;
                    foreach (uint off in strg.RecordOffsets)
                        list.Add(new WadEntryRow(i++, $"0x{off:X8}", Snippet(_wad.ReadWadString(off)), new WadStringRow(off, _wad.ReadWadString(off))));
                    break;
                }
                case WadPrjtChunk prjt when prjt.Entry is not null:
                    list.Add(new WadEntryRow(0, prjt.Entry.ProjectName ?? "(project)",
                        $"{prjt.Entry.Width}x{prjt.Entry.Height} buildType={prjt.Entry.BuildType} startRoom=0x{prjt.Entry.StartRoomRef:X8} rooms={prjt.Entry.RoomOrderCount} files={prjt.Entry.FileList?.Count ?? 0}",
                        prjt.Entry));
                    break;
                case WadRrefChunk rref:
                {
                    int i = 0;
                    foreach (WadRrefEntry entry in rref.Entries)
                        list.Add(new WadEntryRow(i++, entry.Name, $"type={entry.ResourceType} key={entry.Key}", entry));
                    break;
                }
                case WadTagsChunk tags:
                {
                    int i = 0;
                    foreach (WadTagRecord tag in tags.Tags)
                        list.Add(new WadEntryRow(i++, $"#{i - 1}", $"type={tag.ResourceType} key={tag.Key} detail={HexSnippet(tag.Detail)}", tag));
                    break;
                }
                case WadEmbeddedImagesChunk embi:
                {
                    int i = 0;
                    foreach (WadEmbeddedImage image in embi.Images)
                        list.Add(new WadEntryRow(i++, image.Name, $"data=0x{image.DataOffset:X8}", image));
                    break;
                }
                case WadAudoChunk audo:
                {
                    int i = 0;
                    foreach (WadAudioBlob blob in audo.Audio)
                        list.Add(new WadEntryRow(i++, $"blob #{i - 1}", $"{blob.Data.Length:N0} bytes @0x{blob.Offset:X8}", blob));
                    break;
                }
                case WadTxTrChunk textures:
                {
                    int i = 0;
                    foreach (WadTextureEntry tex in textures.Textures)
                        list.Add(new WadEntryRow(i++, $"0x{tex.TexId:X8}", $"{tex.Width}x{tex.Height} fmt={tex.Format} blob={tex.BlobSize:N0} qoi={tex.QoiWidth}x{tex.QoiHeight}", tex));
                    break;
                }
                case WadTpagChunk pages:
                {
                    int i = 0;
                    foreach (WadTexturePage page in pages.Pages)
                        list.Add(new WadEntryRow(i++, $"#{page.PageX},{page.PageY}", $"{page.RectW}x{page.RectH} src=({page.SrcOffX},{page.SrcOffY}) tex=0x{page.PageTexId:X8}", page));
                    break;
                }
                case WadTginChunk groups:
                {
                    int i = 0;
                    foreach (WadTextureGroup group in groups.Groups)
                        list.Add(new WadEntryRow(i++, group.Name, $"comp='{group.Compression}' pages={group.Pages?.Count ?? 0} categories={group.Categories?.Count ?? 0}", group));
                    break;
                }
                case WadRoomChunk rooms:
                {
                    int i = 0;
                    foreach (WadRoomEntry room in rooms.Rooms)
                        list.Add(new WadEntryRow(i++, room.Name, $"{room.Width}x{room.Height} views={room.Views?.Count ?? 0} instances={room.Instances?.Count ?? 0} layers={room.Layers?.Count ?? 0}",
                            room.Error is null ? room : null));
                    if (rooms.Rooms.Any(r => r.Error is not null))
                        list.Add(new WadEntryRow(-1, "⚠ parse errors", string.Join("; ", rooms.Rooms.Where(r => r.Error is not null).Select(r => $"{r.Name}: {Shorten(r.Error.Message, 80)}")), null));
                    break;
                }
                case WadSeqnChunk seqn:
                {
                    int i = 0;
                    foreach (WadSeqnEntry seq in seqn.Entries)
                        list.Add(new WadEntryRow(i++, seq.Name, $"len={seq.Length} speed={seq.PlaybackSpeed} tracks={seq.Tracks?.Tracks?.Count ?? 0} moments={seq.Moments?.Keyframes?.Count ?? 0} end=0x{seq.ParseEndOffset:X8}",
                            seq.Error is null ? seq : null));
                    if (seqn.Entries.Any(s => s.Error is not null))
                        list.Add(new WadEntryRow(-1, "⚠ parse errors", string.Join("; ", seqn.Entries.Where(s => s.Error is not null).Select(s => $"{s.Name}: {Shorten(s.Error.Message, 80)}")), null));
                    break;
                }
                case WadAcrvChunk acrv:
                {
                    int i = 0;
                    foreach (WadAcrvCurve curve in acrv.Curves)
                        list.Add(new WadEntryRow(i++, curve.Name, $"graphType={curve.GraphType} channels={(curve.Channels?.Count ?? 0)}", curve));
                    break;
                }
                case WadTmlnChunk timelines:
                {
                    int i = 0;
                    foreach (WadTmlnEntry tl in timelines.Entries)
                        list.Add(new WadEntryRow(i++, tl.Name, $"moments={(tl.Moments?.Count ?? 0)}", tl));
                    break;
                }
                case WadPathChunk paths:
                {
                    int i = 0;
                    foreach (WadPathEntry path in paths.Entries)
                        list.Add(new WadEntryRow(i++, path.Name, $"kind={path.Kind} closed={path.Closed} precision={path.Precision} points={(path.Points?.Count ?? 0)}", path));
                    break;
                }
                case WadPsysChunk systems:
                {
                    int i = 0;
                    foreach (WadPsysEntry part in systems.Entries)
                        list.Add(new WadEntryRow(i++, part.Name, $"origin=({part.OriginX},{part.OriginY}) order={part.DrawOrder} emitters={(part.Emitters?.Count ?? 0)}", part));
                    break;
                }
                case WadShdrChunk shaders:
                {
                    int i = 0;
                    foreach (WadShdrEntry sh in shaders.Entries)
                        list.Add(new WadEntryRow(i++, sh.Name, $"vertex={sh.VertexLen:N0}B fragment={sh.FragmentLen:N0}B", sh));
                    break;
                }
                case WadObjtChunk objects:
                {
                    int i = 0;
                    foreach (WadObjtEntry obj in objects.Entries)
                        list.Add(new WadEntryRow(i++, obj.Name, $"parent={obj.ParentIndex} persistent={obj.Persistent} events={(obj.Events?.Count ?? 0)} components={(obj.Components?.Count ?? 0)}", obj));
                    break;
                }
                case WadOptnChunk options:
                {
                    int i = 0;
                    foreach (WadOptnEntry opt in options.Entries)
                        list.Add(new WadEntryRow(i++, $"options #{i - 1}", $"version={opt.Version} flags=0x{opt.FlagsA:X8}/0x{opt.FlagsB:X8} depth={opt.ColorDepth} freq={opt.Frequency}", opt));
                    break;
                }
                case WadUilrChunk uilr:
                {
                    int i = 0;
                    foreach (WadUilrLayer layer in uilr.Layers)
                        list.Add(new WadEntryRow(i++, layer.Name, $"type={layer.Type} children={(layer.Children?.Count ?? 0)} end=0x{layer.ChildrenRegionEnd:X8}", layer));
                    break;
                }
                case WadExtnChunk extn:
                {
                    int i = 0;
                    foreach (WadExtnEntry ext in extn.Entries)
                        list.Add(new WadEntryRow(i++, ext.Name, $"version='{ext.Version}' class='{ext.ClassName}' files={(ext.Files?.Count ?? 0)} options={(ext.Options?.Count ?? 0)}", ext));
                    break;
                }
                case WadFedsChunk feds:
                {
                    int i = 0;
                    foreach (WadFedsElement el in feds.Elements)
                        list.Add(new WadEntryRow(i++, el.Name, $"regionEnd=0x{el.RegionEnd:X8}", el));
                    break;
                }
                case WadResourceChunk resources:
                {
                    int i = 0;
                    foreach (WadResourceEntry res in resources.Entries)
                        list.Add(new WadEntryRow(i++, res.Name ?? $"(entry {i - 1})", res switch
                        {
                            WadScriptEntry script => $"code=0x{script.CodeRef:X8}",
                            // _ => res.Error is not null ? $"error: {Shorten(res.Error.Message, 90)}" : "unparsed",
                        }, res));
                    break;
                }
                case WadSprtChunk sprites:
                {
                    int i = 0;
                    foreach (WadSprtEntry spr in sprites.Entries)
                        list.Add(new WadEntryRow(i++, spr.Name, $"{spr.Width}x{spr.Height} bbox=({spr.BBoxLeft},{spr.BBoxTop})..({spr.BBoxRight},{spr.BBoxBottom}) frames={spr.FrameCount} nine=0x{spr.NineSliceOffset:X8}", spr));
                    break;
                }
                case WadBgndChunk tilesets:
                {
                    int i = 0;
                    foreach (WadBgndEntry bg in tilesets.Entries)
                        list.Add(new WadEntryRow(i++, bg.Name, $"{bg.TileWidth}x{bg.TileHeight} cols={bg.Columns} frames={bg.Frames} count={bg.TileCount}", bg));
                    break;
                }
                case WadSondChunk sounds:
                {
                    int i = 0;
                    foreach (WadSondEntry snd in sounds.Entries)
                        list.Add(new WadEntryRow(i++, snd.Name, $"fmt={snd.FormatCode} '{snd.FileName}' vol={snd.Volume}", snd));
                    break;
                }
                case WadAgrpChunk groups2:
                {
                    int i = 0;
                    foreach (WadAgrpEntry ag in groups2.Entries)
                        list.Add(new WadEntryRow(i++, ag.Name, ag.ExportDir, ag));
                    break;
                }
                case WadFontChunk fonts:
                {
                    int i = 0;
                    foreach (WadFontEntry font in fonts.Entries)
                        list.Add(new WadEntryRow(i++, font.Name, $"size={font.Size} bold={font.Bold} italic={font.Italic}", font));
                    break;
                }
            }

            return list.Count == 0 ? null : list;
        }

        private static List<PropRow> BuildProperties(object obj)
        {
            var props = new List<PropRow>();
            if (obj is null)
            {
                props.Add(new PropRow("(no object)", ""));
                return props;
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
                props.Add(new PropRow(info.Name, FormatValue(value, 0)));
            }
            return props;
        }

        /// <summary>Flattens a reflected value into a readable string.</summary>
        private static string FormatValue(object value, int depth)
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

        private static string HexSnippet(byte[] data, int max = 16)
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

        private string HexDump(WadChunk chunk)
        {
            if (chunk is null || _wad is null)
                return "";
            const int maxBytes = 8192;
            byte[] data = _wad.GetChunkBytes(chunk.Name, maxBytes);
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
            if (chunk.Length > data.Length)
                sb.AppendLine($"… {chunk.Length - data.Length:N0} more bytes not shown");
            return sb.ToString();
        }

        private string BuildStringsText(WadChunk chunk)
        {
            if (chunk is not WadStrgChunk strg || _wad is null)
                return "";
            var sb = new StringBuilder();
            sb.AppendLine($"Count hint: {strg.CountHint}, flags: {strg.Flags}, records: {strg.RecordOffsets.Count}");
            sb.AppendLine();
            foreach (uint off in strg.RecordOffsets)
                sb.AppendLine($"0x{off:X8}: {_wad.ReadWadString(off)}");
            return sb.ToString();
        }
    }

    /// <summary>A chunk row shown in the chunk list.</summary>
    public sealed class WadChunkRow
    {
        public string Name { get; }
        public string OffsetText { get; }
        public string LengthText { get; }
        public string EntriesCountText { get; }
        public string KindText { get; }
        public WadChunk Chunk { get; }

        private WadChunkRow(string name, string offsetText, string lengthText, string entriesCountText, string kindText, WadChunk chunk)
        {
            Name = name;
            OffsetText = offsetText;
            LengthText = lengthText;
            EntriesCountText = entriesCountText;
            KindText = kindText;
            Chunk = chunk;
        }

        public static WadChunkRow From(WadChunkHeader header, WadChunk chunk)
        {
            uint? count = EntryCountOf(chunk);
            return new WadChunkRow(
                header.Name,
                $"0x{header.Offset:X8}",
                header.Length.ToString("N0", CultureInfo.InvariantCulture),
                count.HasValue ? count.Value.ToString("N0", CultureInfo.InvariantCulture) : "—",
                chunk is null ? "unknown" : chunk is WadRawChunk ? "raw" : "parsed",
                chunk);
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
    }

    /// <summary>An entry row shown in the entries grid.</summary>
    public sealed class WadEntryRow
    {
        public int Index { get; }
        public string Name { get; }
        public string Summary { get; }
        public object Object { get; }

        public WadEntryRow(int index, string name, string summary, object obj)
        {
            Index = index;
            Name = name;
            Summary = summary;
            Object = obj;
        }
    }

    /// <summary>A reflected property row shown in the property grid.</summary>
    public sealed class PropRow
    {
        public string Name { get; }
        public string ValueText { get; }

        public PropRow(string name, string valueText)
        {
            Name = name;
            ValueText = valueText;
        }
    }

    /// <summary>One STRG string record (for the property pane).</summary>
    public sealed class WadStringRow
    {
        public uint Offset { get; }
        public string Value { get; }

        public WadStringRow(uint offset, string value)
        {
            Offset = offset;
            Value = value;
        }
    }
}