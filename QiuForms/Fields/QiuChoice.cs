// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace QiuForms;

/// <summary>A choice field, shown as a drop-down list or a radio group.</summary>
public sealed class QiuChoice : QiuField<int>
{
    private readonly List<string> items;

    /// <summary>Creates a choice field.</summary>
    /// <param name="label">The label shown next to the editor.</param>
    /// <param name="items">The selectable items. Must contain at least one entry.</param>
    /// <param name="selectedIndex">The initially selected index.</param>
    public QiuChoice(string label, IEnumerable<string> items, int selectedIndex = 0)
        : base(label, selectedIndex)
    {
        ArgumentNullException.ThrowIfNull(items);
        this.items = new List<string>(items);
        if (this.items.Count == 0)
            throw new ArgumentException("A choice field needs at least one item.", nameof(items));

        SetValueFromHost(selectedIndex);
    }

    /// <summary>The selectable items.</summary>
    public IReadOnlyList<string> Items => items;

    /// <summary>The selected index. Alias of <see cref="QiuField{T}.Value"/>.</summary>
    public int SelectedIndex
    {
        get => Value;
        set => Value = value;
    }

    /// <summary>The selected item's text.</summary>
    public string SelectedItem => items[Value];

    /// <summary>Whether the host should use a radio group instead of a drop-down list.</summary>
    public bool UseRadioGroup { get; set; }

    /// <inheritdoc/>
    protected override bool IsEmpty => false;

    /// <inheritdoc/>
    protected override int Normalize(int newValue) => Math.Clamp(newValue, 0, items.Count - 1);
}
