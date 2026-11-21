// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace QiuForms;

/// <summary>How a <see cref="QiuForm"/> was closed.</summary>
public enum QiuDialogResult
{
    /// <summary>The user submitted the form. Field values are valid.</summary>
    Submit,

    /// <summary>The user cancelled the form.</summary>
    Cancel,

    /// <summary>The form was closed without submitting or cancelling (for example with the window close button).</summary>
    Closed,
}
