// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace QiuForms;

/// <summary>
/// A toolkit-neutral form description. Build it in a script, then hand it to the host with
/// <see cref="IQiuFormsHost.ShowQiuForm"/>; the host renders it with its own UI toolkit.
/// </summary>
/// <example>
/// <code>
/// QiuForm form = new("Import options");
/// QiuCheckBox special = form.Check("Special sprite", false);
/// QiuNumberBox speed = form.Number("Animation speed", 1, min: 0, max: 100);
/// speed.EnabledWhen(() => special.Value);
///
/// if (ShowQiuForm(form) != QiuDialogResult.Submit)
///     return;
///
/// float value = (float)speed.Value;
/// </code>
/// </example>
public sealed class QiuForm
{
    /// <summary>Creates a form with the given window title.</summary>
    public QiuForm(string title)
    {
        Title = title ?? string.Empty;
    }

    /// <summary>The window title.</summary>
    public string Title { get; set; }

    /// <summary>Optional description shown above the fields.</summary>
    public string? Message { get; set; }

    /// <summary>
    /// Caption of the submit button. When <see langword="null"/> or empty, the host uses its own
    /// localized "OK" caption.
    /// </summary>
    public string? SubmitText { get; set; }

    /// <summary>
    /// Caption of the cancel button. When <see langword="null"/> or empty, the host uses its own
    /// localized "Cancel" caption.
    /// </summary>
    public string? CancelText { get; set; }

    /// <summary>Whether the host shows a cancel button. Defaults to <see langword="true"/>.</summary>
    public bool ShowCancelButton { get; set; } = true;

    /// <summary>Message used for fields whose <see cref="QiuField.Required"/> is set but empty.</summary>
    public string RequiredMessage { get; set; } = "This field is required.";

    /// <summary>The root container of the form.</summary>
    public QiuPanel Root { get; } = new();

    /// <summary>
    /// How the form was closed. Set by the host before <see cref="IQiuFormsHost.ShowQiuForm"/>
    /// returns; scripts normally use the return value instead.
    /// </summary>
    public QiuDialogResult Result { get; set; } = QiuDialogResult.Cancel;

    /// <summary>The first field that failed the last <see cref="Validate"/> call, if any.</summary>
    public QiuField? FirstInvalid { get; private set; }

    /// <summary>Whether the last <see cref="IQiuFormsHost.ShowQiuForm"/> call submitted the form.</summary>
    public bool WasSubmitted => Result == QiuDialogResult.Submit;

    /// <summary>Adds a text label.</summary>
    public QiuLabel Label(string text, bool heading = false) => Root.Label(text, heading);

    /// <summary>Adds a separator line.</summary>
    public QiuSeparator Separator() => Root.Separator();

    /// <summary>Adds a text field.</summary>
    public QiuTextBox Text(string label, string value = "", string? id = null, bool multiline = false)
        => Root.Text(label, value, id, multiline);

    /// <summary>Adds a numeric field.</summary>
    public QiuNumberBox Number(string label, double value,
                               double minimum = double.NegativeInfinity, double maximum = double.PositiveInfinity,
                               int decimals = 2, bool integer = false, string? id = null)
        => Root.Number(label, value, minimum, maximum, decimals, integer, id);

    /// <summary>Adds a check box.</summary>
    public QiuCheckBox Check(string label, bool value = false, string? id = null)
        => Root.Check(label, value, id);

    /// <summary>Adds a choice field.</summary>
    public QiuChoice Choice(string label, IEnumerable<string> items, int selectedIndex = 0, string? id = null)
        => Root.Choice(label, items, selectedIndex, id);

    /// <summary>Adds a button.</summary>
    public QiuButton Button(string text, Action<QiuForm>? onClick = null, string? id = null,
                            bool isDefault = false, bool isCancel = false)
        => Root.Button(text, onClick, id, isDefault, isCancel);

    /// <summary>Adds an image from encoded PNG bytes.</summary>
    public QiuImage Image(byte[] png, string? caption = null, string? id = null)
        => Root.Image(png, caption, id);

    /// <summary>Adds an image from a file.</summary>
    public QiuImage Image(string filePath, string? caption = null, string? id = null)
        => Root.Image(filePath, caption, id);

    /// <summary>Adds a nested panel.</summary>
    public QiuPanel Panel(QiuOrientation orientation = QiuOrientation.Vertical, string? id = null)
        => Root.Panel(orientation, id);

    /// <summary>Adds a titled group.</summary>
    public QiuGroup Group(string title, string? id = null) => Root.Group(title, id);

    /// <summary>Adds a list box.</summary>
    public QiuListBox List(string? caption = null, double height = 0, double width = 0, string? id = null)
        => Root.List(caption, height, width, id);

    /// <summary>Adds a tree view.</summary>
    public QiuTreeView Tree(string rootTitle, double height = 0, double width = 0, string? id = null)
        => Root.Tree(rootTitle, height, width, id);

    /// <summary>Adds a slider.</summary>
    public QiuSlider Slider(string label, int value, int minimum = 0, int maximum = 100, string? id = null)
        => Root.Slider(label, value, minimum, maximum, id);

    /// <summary>Adds a custom-drawn canvas.</summary>
    public QiuCanvas Canvas(double width, double height, string? id = null)
        => Root.Canvas(width, height, id);

    /// <summary>Enumerates every field of the form, in display order.</summary>
    public IEnumerable<QiuField> EnumerateFields() => EnumerateFields(Root);

    /// <summary>Finds an element by <see cref="QiuElement.Id"/>.</summary>
    public QiuElement? Find(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return FindIn(Root, id);
    }

    /// <summary>Finds a field by <see cref="QiuElement.Id"/>.</summary>
    public QiuField? FindField(string id) => Find(id) as QiuField;

    /// <summary>
    /// Runs validation for every visible, enabled field and stores the result in
    /// <see cref="QiuField.Error"/>. Hosts call this on submit and keep the form open when it
    /// returns <see langword="false"/>.
    /// </summary>
    public bool Validate()
    {
        bool valid = true;
        FirstInvalid = null;

        foreach (QiuField field in EnumerateFields())
        {
            string? error = field.Check(RequiredMessage);
            field.Error = error;

            if (error is not null)
            {
                valid = false;
                FirstInvalid ??= field;
            }
        }

        return valid;
    }

    private static IEnumerable<QiuField> EnumerateFields(QiuElement element)
    {
        if (element is QiuField field)
        {
            yield return field;
            yield break;
        }

        if (element is QiuPanel panel)
        {
            foreach (QiuElement child in panel.Children)
            {
                foreach (QiuField childField in EnumerateFields(child))
                    yield return childField;
            }
        }
    }

    private static QiuElement? FindIn(QiuElement element, string id)
    {
        if (string.Equals(element.Id, id, StringComparison.Ordinal))
            return element;

        if (element is QiuPanel panel)
        {
            foreach (QiuElement child in panel.Children)
            {
                QiuElement? found = FindIn(child, id);
                if (found is not null)
                    return found;
            }
        }

        return null;
    }
}
