// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace QiuForms;

/// <summary>A run of text shown in the form.</summary>
public sealed class QiuLabel : QiuElement
{
    /// <summary>Creates a label.</summary>
    /// <param name="text">The text to show.</param>
    /// <param name="heading">Whether the host should render the text as a section heading.</param>
    public QiuLabel(string text, bool heading = false)
    {
        Text = text ?? string.Empty;
        Heading = heading;
    }

    private string text = string.Empty;

    /// <summary>The text to show. Assigning raises <see cref="TextChanged"/> so hosts can refresh.</summary>
    public string Text
    {
        get => text;
        set
        {
            value ??= string.Empty;
            if (text == value)
                return;

            text = value;
            TextChanged?.Invoke(this);
        }
    }

    /// <summary>Raised after <see cref="Text"/> changed.</summary>
    public event Action<QiuLabel>? TextChanged;

    /// <summary>Whether the host should render the text as a section heading.</summary>
    public bool Heading { get; set; }
}
