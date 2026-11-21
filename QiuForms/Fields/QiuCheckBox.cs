// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace QiuForms;

/// <summary>A check box.</summary>
public sealed class QiuCheckBox : QiuField<bool>
{
    /// <summary>Creates a check box.</summary>
    /// <param name="label">The label shown next to the box.</param>
    /// <param name="value">The initial value.</param>
    public QiuCheckBox(string label, bool value = false)
        : base(label, value)
    {
    }

    /// <inheritdoc/>
    protected override bool IsEmpty => false;
}
