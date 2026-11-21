// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace QiuForms;

/// <summary>A numeric field with a range and optional integer-only mode.</summary>
public sealed class QiuNumberBox : QiuField<double>
{
    /// <summary>Creates a numeric field.</summary>
    /// <param name="label">The label shown next to the editor.</param>
    /// <param name="value">The initial value.</param>
    /// <param name="minimum">Smallest accepted value.</param>
    /// <param name="maximum">Largest accepted value.</param>
    /// <param name="decimals">Digits kept after the decimal point when <paramref name="integer"/> is <see langword="false"/>.</param>
    /// <param name="integer">Whether only whole numbers are accepted.</param>
    public QiuNumberBox(string label, double value,
                        double minimum = double.NegativeInfinity, double maximum = double.PositiveInfinity,
                        int decimals = 2, bool integer = false)
        : base(label, value)
    {
        if (double.IsNaN(minimum) || double.IsNaN(maximum))
            throw new ArgumentException("The bounds of a numeric field cannot be NaN.");
        if (minimum > maximum)
            throw new ArgumentException("The minimum of a numeric field cannot be greater than its maximum.");

        Minimum = minimum;
        Maximum = maximum;
        Decimals = decimals;
        Integer = integer;

        // Re-apply the value now that the bounds are known, so the stored value is normalized.
        SetValueFromHost(value);
    }

    /// <summary>Smallest accepted value.</summary>
    public double Minimum { get; }

    /// <summary>Largest accepted value.</summary>
    public double Maximum { get; }

    /// <summary>Digits kept after the decimal point, or a negative value to keep full precision.</summary>
    public int Decimals { get; }

    /// <summary>Whether only whole numbers are accepted.</summary>
    public bool Integer { get; }

    /// <summary>
    /// Whether an empty (NaN) value is allowed. When <see langword="false"/>, a NaN value is
    /// reported as invalid even if <see cref="QiuField.Required"/> is not set.
    /// </summary>
    public bool AllowEmpty { get; set; }

    /// <summary>Message shown when the value falls outside the range. <c>{0}</c> and <c>{1}</c> are the bounds.</summary>
    public string RangeMessage { get; set; } = "Value must be between {0} and {1}.";

    /// <summary>Message shown when the value is empty and <see cref="AllowEmpty"/> is <see langword="false"/>.</summary>
    public string EmptyMessage { get; set; } = "Enter a number.";

    /// <inheritdoc/>
    protected override bool IsEmpty => double.IsNaN(Value);

    /// <inheritdoc/>
    protected override double Normalize(double newValue)
    {
        if (double.IsNaN(newValue))
            return newValue;

        if (newValue < Minimum)
            newValue = Minimum;
        if (newValue > Maximum)
            newValue = Maximum;

        return Integer ? Math.Round(newValue)
            : Decimals < 0 ? newValue
            : Math.Round(newValue, Decimals);
    }

    internal override string? Check(string requiredMessage)
    {
        string? error = base.Check(requiredMessage);
        if (error is not null)
            return error;

        if (double.IsNaN(Value))
            return AllowEmpty ? null : EmptyMessage;

        if (Value < Minimum || Value > Maximum)
            return string.Format(RangeMessage, Minimum, Maximum);

        return null;
    }
}
