using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Wraps a <see cref="byte"/> array so it can be used as a data grid item with a settable property.
/// </summary>
public class ByteArrayWrapper : INotifyPropertyChanged
{
    byte[] byteArray;
    byte[]? original;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ByteArrayWrapper(byte[] value, byte[]? original = null)
    {
        byteArray = value;
        this.original = original;
    }

    /// <summary>
    /// The wrapped byte array. Writing back into the array that was used to create this wrapper
    /// is done on assignment, so in-place edits are persisted to the underlying data.
    /// </summary>
    public byte[] ByteArray
    {
        get => byteArray;
        set
        {
            if (ReferenceEquals(byteArray, value))
                return;

            if (original is not null && value.Length == original.Length)
                value.CopyTo(original, 0);
            else
                original = value;

            byteArray = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ByteArray)));
        }
    }

    public static implicit operator byte[](ByteArrayWrapper wrapper) => wrapper.ByteArray;
    public static implicit operator ByteArrayWrapper(byte[] value) => new(value);
}