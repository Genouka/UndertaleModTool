// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace QiuForms;

/// <summary>A panel with a title, drawn as a group box by the host.</summary>
public class QiuGroup : QiuPanel
{
    /// <summary>Creates a group.</summary>
    /// <param name="title">The title shown above the group's content.</param>
    public QiuGroup(string title)
    {
        Title = title ?? string.Empty;
    }

    /// <summary>The title shown above the group's content.</summary>
    public string Title { get; set; }
}
