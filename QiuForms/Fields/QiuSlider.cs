// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace QiuForms;

/// <summary>A slider for whole numbers.</summary>
public sealed class QiuSlider : QiuField<int>
{
    /// <summary>Creates a slider.</summary>
    /// <param name="label">The label shown next to the slider.</param>
    /// <param name="value">The initial value.</param>
    /// <param name="minimum">Smallest accepted value.</param>
    /// <param name="maximum">Largest accepted value.</param>
    public QiuSlider(string label, int value, int minimum = 0, int maximum = 100)
        : base(label, value)
    {
        if (minimum > maximum)
            throw new ArgumentException("The minimum of a slider cannot be greater than its maximum.");

        Minimum = minimum;
        Maximum = maximum;
        SetValueFromHost(value);
    }

    /// <summary>Smallest accepted value.</summary>
    public int Minimum { get; }

    /// <summary>Largest accepted value.</summary>
    public int Maximum { get; }

    /// <inheritdoc/>
    protected override bool IsEmpty => false;

    /// <inheritdoc/>
    protected override int Normalize(int newValue) => Math.Clamp(newValue, Minimum, Maximum);
}
