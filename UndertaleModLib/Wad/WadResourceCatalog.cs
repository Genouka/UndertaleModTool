using System;
using System.Collections.Generic;

namespace UndertaleModLib.Wad
{
    /// <summary>
    /// Name index over a wad's resource chunks. Lets editors present friendly names
    /// instead of raw u32 indices/offsets (the "references handled internally" layer):
    /// e.g. an object's <c>ParentIndex</c> is rendered as a pick list of object names.
    /// </summary>
    public sealed class WadResourceCatalog
    {
        public IReadOnlyList<WadResourceRef> Objects { get; }
        public IReadOnlyList<WadResourceRef> Sprites { get; }
        public IReadOnlyList<WadResourceRef> Rooms { get; }
        public IReadOnlyList<WadResourceRef> Sounds { get; }
        public IReadOnlyList<WadResourceRef> Paths { get; }
        public IReadOnlyList<WadResourceRef> Fonts { get; }
        public IReadOnlyList<WadResourceRef> Scripts { get; }

        public WadResourceCatalog(UndertaleWadFile wad)
        {
            if (wad is null)
                throw new ArgumentNullException(nameof(wad));
            Objects = Collect<WadObjtChunk, WadObjtEntry>(wad, "OBJT", c => c.Entries, e => e.Name);
            Sprites = Collect<WadSprtChunk, WadSprtEntry>(wad, "SPRT", c => c.Entries, e => e.Name);
            Rooms = Collect<WadRoomChunk, WadRoomEntry>(wad, "ROOM", c => c.Rooms, e => e.Name);
            Sounds = Collect<WadSondChunk, WadSondEntry>(wad, "SOND", c => c.Entries, e => e.Name);
            Paths = Collect<WadPathChunk, WadPathEntry>(wad, "PATH", c => c.Entries, e => e.Name);
            Fonts = Collect<WadFontChunk, WadFontEntry>(wad, "FONT", c => c.Entries, e => e.Name);
            Scripts = CollectResourceScripts(wad, "SCPT");
        }

        private static IReadOnlyList<WadResourceRef> Collect<TChunk, TEntry>(UndertaleWadFile wad, string chunkName,
            Func<TChunk, IReadOnlyList<TEntry>> entriesOf, Func<TEntry, string> nameOf)
            where TChunk : WadChunk
        {
            var list = new List<WadResourceRef>();
            if (wad.Chunks.TryGetValue(chunkName, out WadChunk chunk) && chunk is TChunk typed)
            {
                IReadOnlyList<TEntry> entries = entriesOf(typed);
                for (int i = 0; i < entries.Count; i++)
                    list.Add(new WadResourceRef(i, nameOf(entries[i]) ?? $"{chunkName}#{i}"));
            }
            return list;
        }

        // Generic {count, offsets} resource chunks (SCPT, GLTF, …): entries are
        // WadResourceEntry subclasses carrying a resolved Name.
        private static IReadOnlyList<WadResourceRef> CollectResourceScripts(UndertaleWadFile wad, string chunkName)
        {
            var list = new List<WadResourceRef>();
            if (wad.Chunks.TryGetValue(chunkName, out WadChunk chunk) && chunk is WadResourceChunk rc)
            {
                for (int i = 0; i < rc.Entries.Count; i++)
                    list.Add(new WadResourceRef(i, rc.Entries[i].Name ?? $"{chunkName}#{i}"));
            }
            return list;
        }
    }

    /// <summary>One catalog entry: index within its chunk + resolved name.</summary>
    public readonly struct WadResourceRef
    {
        public int Index { get; }
        public string Name { get; }

        public WadResourceRef(int index, string name)
        {
            Index = index;
            Name = name;
        }

        public override string ToString() => Name;
    }
}