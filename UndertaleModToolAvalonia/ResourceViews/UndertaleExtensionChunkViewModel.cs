using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using UndertaleModLib;

namespace UndertaleModToolAvalonia;

public partial class UndertaleExtensionChunkViewModel : ObservableObject, ITabContent
{
    public UndertaleChunkEXTN ExtensionChunk { get; }

    /// <summary>
    /// Editable view over <see cref="UndertaleChunkEXTN.productIdData"/>. The wrappers reference the
    /// original byte arrays, so in-place edits and list operations are written back automatically.
    /// </summary>
    public ObservableCollection<ByteArrayWrapper> ProductIdData { get; }

    public UndertaleExtensionChunkViewModel(UndertaleChunkEXTN extensionChunk)
    {
        ExtensionChunk = extensionChunk;

        ProductIdData = new ObservableCollection<ByteArrayWrapper>(
            extensionChunk.productIdData.Select(static x => new ByteArrayWrapper(x, x)));

        ProductIdData.CollectionChanged += (_, _) =>
        {
            ExtensionChunk.productIdData = ProductIdData.Select(static x => x.ByteArray).ToList();
        };
    }

    public static ByteArrayWrapper CreateByteArray() => new byte[16];
}