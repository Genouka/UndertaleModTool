// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace QiuForms;

/// <summary>
/// A container that stacks its children vertically or horizontally.
/// </summary>
/// <remarks>
/// Layout is intentionally flow-based (no absolute positions), so the same form works on a
/// desktop window and on a phone screen. The host decides the final pixel sizes.
/// </remarks>
public class QiuPanel : QiuElement
{
    private readonly List<QiuElement> children = new();

    /// <summary>Creates a panel.</summary>
    public QiuPanel(QiuOrientation orientation = QiuOrientation.Vertical)
    {
        Orientation = orientation;
    }

    /// <summary>Direction in which the children are arranged.</summary>
    public QiuOrientation Orientation { get; set; }

    /// <summary>Space between children, in device-independent units.</summary>
    public int Spacing { get; set; } = 6;

    /// <summary>The children, in display order.</summary>
    public IReadOnlyList<QiuElement> Children => children;

    /// <summary>Adds an element to the panel and returns it.</summary>
    public T Add<T>(T element)
        where T : QiuElement
    {
        ArgumentNullException.ThrowIfNull(element);
        if (ReferenceEquals(element, this))
            throw new ArgumentException("A panel cannot contain itself.", nameof(element));

        children.Add(element);
        element.Parent = this;
        return element;
    }

    /// <summary>Adds a text label.</summary>
    public QiuLabel Label(string text, bool heading = false) => Add(new QiuLabel(text, heading));

    /// <summary>Adds a separator line.</summary>
    public QiuSeparator Separator() => Add(new QiuSeparator());

    /// <summary>Adds a text field.</summary>
    public QiuTextBox Text(string label, string value = "", string? id = null, bool multiline = false)
        => Add(new QiuTextBox(label, value, multiline) { Id = id });

    /// <summary>Adds a numeric field.</summary>
    public QiuNumberBox Number(string label, double value,
                               double minimum = double.NegativeInfinity, double maximum = double.PositiveInfinity,
                               int decimals = 2, bool integer = false, string? id = null)
        => Add(new QiuNumberBox(label, value, minimum, maximum, decimals, integer) { Id = id });

    /// <summary>Adds a check box.</summary>
    public QiuCheckBox Check(string label, bool value = false, string? id = null)
        => Add(new QiuCheckBox(label, value) { Id = id });

    /// <summary>Adds a choice field.</summary>
    public QiuChoice Choice(string label, IEnumerable<string> items, int selectedIndex = 0, string? id = null)
        => Add(new QiuChoice(label, items, selectedIndex) { Id = id });

    /// <summary>Adds a button.</summary>
    public QiuButton Button(string text, Action<QiuForm>? onClick = null, string? id = null,
                            bool isDefault = false, bool isCancel = false)
        => Add(new QiuButton(text) { Click = onClick, Id = id, IsDefault = isDefault, IsCancel = isCancel });

    /// <summary>Adds an image from encoded PNG bytes.</summary>
    public QiuImage Image(byte[] png, string? caption = null, string? id = null)
        => Add(new QiuImage(png) { Caption = caption, Id = id });

    /// <summary>Adds an image from a file.</summary>
    public QiuImage Image(string filePath, string? caption = null, string? id = null)
        => Add(new QiuImage(filePath) { Caption = caption, Id = id });

    /// <summary>Adds a nested panel.</summary>
    public QiuPanel Panel(QiuOrientation orientation = QiuOrientation.Vertical, string? id = null)
        => Add(new QiuPanel(orientation) { Id = id });

    /// <summary>Adds a titled group.</summary>
    public QiuGroup Group(string title, string? id = null) => Add(new QiuGroup(title) { Id = id });

    /// <summary>Adds a list box.</summary>
    public QiuListBox List(string? caption = null, double height = 0, double width = 0, string? id = null)
        => Add(new QiuListBox { Caption = caption, Height = height, Width = width, Id = id });

    /// <summary>Adds a tree view.</summary>
    public QiuTreeView Tree(string rootTitle, double height = 0, double width = 0, string? id = null)
        => Add(new QiuTreeView(rootTitle) { Height = height, Width = width, Id = id });

    /// <summary>Adds a slider.</summary>
    public QiuSlider Slider(string label, int value, int minimum = 0, int maximum = 100, string? id = null)
        => Add(new QiuSlider(label, value, minimum, maximum) { Id = id });

    /// <summary>Adds a custom-drawn canvas.</summary>
    public QiuCanvas Canvas(double width, double height, string? id = null)
        => Add(new QiuCanvas { Width = width, Height = height, Id = id });
}
