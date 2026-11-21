// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace QiuForms;

/// <summary>A single-line or multi-line text field.</summary>
public sealed class QiuTextBox : QiuField<string>
{
    /// <summary>Creates a text field.</summary>
    /// <param name="label">The label shown next to the editor.</param>
    /// <param name="value">The initial value.</param>
    /// <param name="multiline">Whether the editor accepts line breaks.</param>
    public QiuTextBox(string label, string value = "", bool multiline = false)
        : base(label, value ?? string.Empty)
    {
        Multiline = multiline;
    }

    /// <summary>Whether the editor accepts line breaks. Enter submits a single-line field.</summary>
    public bool Multiline { get; set; }

    /// <summary>Placeholder text shown while the field is empty.</summary>
    public string? Placeholder { get; set; }

    /// <summary>Maximum number of characters, or <c>0</c> for no limit.</summary>
    public int MaxLength { get; set; }

    /// <inheritdoc/>
    protected override bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <inheritdoc/>
    protected override string Normalize(string newValue)
    {
        newValue ??= string.Empty;
        if (MaxLength > 0 && newValue.Length > MaxLength)
            newValue = newValue[..MaxLength];
        return newValue;
    }
}
