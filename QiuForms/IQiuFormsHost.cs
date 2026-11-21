// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace QiuForms;

/// <summary>
/// A host that can display <see cref="QiuForm"/> dialogs.
/// </summary>
/// <remarks>
/// <para>
/// The interface has default implementations, so existing hosts keep compiling without changes.
/// A host without a user interface (command line, cross-app API, project builds, tests) simply
/// leaves <see cref="SupportsQiuForms"/> at <see langword="false"/>.
/// </para>
/// <para>
/// Implementations must be safe to call from any thread: they are responsible for creating and
/// showing the window on their own UI thread, and for blocking the caller until the form closes.
/// </para>
/// </remarks>
public interface IQiuFormsHost
{
    /// <summary>
    /// Whether this host can display forms. When <see langword="false"/>,
    /// <see cref="ShowQiuForm"/> throws <see cref="QiuFormUnavailableException"/>.
    /// </summary>
    bool SupportsQiuForms => false;

    /// <summary>
    /// Shows <paramref name="form"/> and blocks until it is closed.
    /// </summary>
    /// <param name="form">The form to display.</param>
    /// <returns>How the form was closed. Also stored in <see cref="QiuForm.Result"/>.</returns>
    /// <exception cref="QiuFormUnavailableException">The host has no user interface.</exception>
    QiuDialogResult ShowQiuForm(QiuForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        throw new QiuFormUnavailableException();
    }
}
