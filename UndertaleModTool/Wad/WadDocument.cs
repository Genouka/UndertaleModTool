using System;
using UndertaleModLib.Wad;

namespace UndertaleModTool.Wad
{
    /// <summary>
    /// Document handle for one opened wad file: the parsed file, its edit session
    /// (byte-level patches + STRG-append renames) and the resource name catalog used
    /// by the editors to render references as friendly names.
    /// </summary>
    public sealed class WadDocument : IDisposable
    {
        public UndertaleWadFile Wad { get; }
        public WadEditSession Session { get; }
        public WadResourceCatalog Catalog { get; }

        public bool HasChanges => Session.HasChanges;

        public WadDocument(UndertaleWadFile wad)
        {
            Wad = wad ?? throw new ArgumentNullException(nameof(wad));
            Session = new WadEditSession(wad);
            Catalog = new WadResourceCatalog(wad);
        }

        public void Dispose() => Wad.Dispose();
    }
}