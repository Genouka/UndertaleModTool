// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace QiuForms;

/// <summary>A single-selection list box, optionally with an activation event (double click).</summary>
public sealed class QiuListBox : QiuElement
{
    private readonly List<string> items = [];
    private int selectedIndex = -1;

    /// <summary>Optional caption shown above the list.</summary>
    public string? Caption { get; set; }

    /// <summary>Preferred height in device-independent units, or <c>0</c> for the host default.</summary>
    public double Height { get; set; }

    /// <summary>Preferred width in device-independent units, or <c>0</c> for the host default.</summary>
    public double Width { get; set; }

    /// <summary>The current items.</summary>
    public IReadOnlyList<string> Items => items;

    /// <summary>The selected index, or <c>-1</c> when nothing is selected.</summary>
    public int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            int clamped = items.Count == 0 ? -1 : Math.Clamp(value, -1, items.Count - 1);
            if (clamped == selectedIndex)
                return;

            selectedIndex = clamped;
            SelectionChanged?.Invoke(this);
        }
    }

    /// <summary>The selected item, or <see langword="null"/>.</summary>
    public string? SelectedItem => selectedIndex >= 0 && selectedIndex < items.Count ? items[selectedIndex] : null;

    /// <summary>Raised after <see cref="SetItems"/> changed the list.</summary>
    public event Action<QiuListBox>? ItemsChanged;

    /// <summary>Raised after the selection changed, including changes made in the host.</summary>
    public event Action<QiuListBox>? SelectionChanged;

    /// <summary>Raised when the user double-clicks an item or presses Enter on it.</summary>
    public event Action<QiuListBox>? ItemActivated;

    /// <summary>Replaces the items. Keeps the selection when it is still in range.</summary>
    public void SetItems(IEnumerable<string> newItems)
    {
        ArgumentNullException.ThrowIfNull(newItems);

        items.Clear();
        items.AddRange(newItems);

        if (selectedIndex >= items.Count)
            selectedIndex = items.Count == 0 ? -1 : items.Count - 1;

        ItemsChanged?.Invoke(this);
    }

    /// <summary>Raises <see cref="ItemActivated"/>. Called by hosts.</summary>
    public void RaiseItemActivated() => ItemActivated?.Invoke(this);
}
