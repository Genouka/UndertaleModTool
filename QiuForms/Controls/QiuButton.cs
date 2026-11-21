// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace QiuForms;

/// <summary>A button inside the form.</summary>
/// <remarks>
/// Buttons do not have to close the form: <see cref="Click"/> runs on the host's UI thread while
/// the script is blocked inside <see cref="IQiuFormsHost.ShowQiuForm"/>, so the handler may update
/// other fields of the form and the script continues once the form is closed.
/// </remarks>
public sealed class QiuButton : QiuElement
{
    /// <summary>Creates a button.</summary>
    /// <param name="text">The caption.</param>
    public QiuButton(string text)
    {
        Text = text ?? string.Empty;
    }

    /// <summary>The caption.</summary>
    public string Text { get; set; }

    /// <summary>Invoked when the user presses the button. Runs on the host's UI thread.</summary>
    public Action<QiuForm>? Click { get; set; }

    /// <summary>Whether the host should treat this button as the default action (Enter).</summary>
    public bool IsDefault { get; set; }

    /// <summary>Whether the host should treat this button as the cancel action (Escape).</summary>
    public bool IsCancel { get; set; }
}
