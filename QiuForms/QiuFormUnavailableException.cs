// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace QiuForms;

/// <summary>
/// Thrown when a script asks a host to display a form, but the host has no user interface.
/// Scripts that want to support such hosts should check <see cref="IQiuFormsHost.SupportsQiuForms"/> first.
/// </summary>
public sealed class QiuFormUnavailableException : InvalidOperationException
{
    /// <summary>Creates the exception with the default message.</summary>
    public QiuFormUnavailableException()
        : base("The current host cannot display QiuForms dialogs. " +
               "Check IQiuFormsHost.SupportsQiuForms before calling ShowQiuForm.")
    {
    }

    /// <summary>Creates the exception with a custom message.</summary>
    public QiuFormUnavailableException(string message)
        : base(message)
    {
    }
}
