// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace QiuForms;

/// <summary>
/// Base class for input fields: an element that shows a label, edits a value, and carries
/// validation state.
/// </summary>
public abstract class QiuField : QiuElement
{
    private readonly List<Func<QiuField, string?>> validators = new();

    /// <summary>Creates a field with the given label.</summary>
    protected QiuField(string label)
    {
        Label = label ?? throw new ArgumentNullException(nameof(label));
    }

    /// <summary>The label shown next to the editor.</summary>
    public string Label { get; set; }

    /// <summary>Whether the host shows <see cref="Label"/>. Set to <see langword="false"/> for search boxes.</summary>
    public bool ShowLabel { get; set; } = true;

    /// <summary>Whether the field currently has a validation error.</summary>
    public bool HasError => Error is not null;

    /// <summary>
    /// Raised after the field's value changed, including edits made in a host. Unlike the typed
    /// <see cref="QiuField{T}.Changed"/> event, this one is usable when the field's type is unknown.
    /// </summary>
    public event Action<QiuField>? ValueChanged;

    /// <summary>Raises <see cref="ValueChanged"/> for a derived field.</summary>
    protected void RaiseValueChanged() => ValueChanged?.Invoke(this);

    /// <summary>
    /// The validation error shown under the field, or <see langword="null"/> when valid.
    /// Set by <see cref="QiuForm.Validate"/>.
    /// </summary>
    public string? Error { get; internal set; }

    /// <summary>Whether an empty value is rejected on submit.</summary>
    public bool Required { get; set; }


    /// <summary>Marks the field as required.</summary>
    public QiuField IsRequired()
    {
        Required = true;
        return this;
    }

    /// <summary>
    /// Adds a validator. The validator returns an error message, or <see langword="null"/> when the
    /// value is acceptable. Validators run in the order they were added, after the required check.
    /// </summary>
    public QiuField Validate(Func<QiuField, string?> validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        validators.Add(validator);
        return this;
    }

    /// <summary>Clears <see cref="Error"/>. Hosts call this when the user edits the field.</summary>
    public void ClearError() => Error = null;

    /// <summary>
    /// Runs the required check and every validator. Invisible and disabled fields are skipped.
    /// </summary>
    /// <returns>The error message, or <see langword="null"/> when the value is acceptable.</returns>
    internal virtual string? Check(string requiredMessage)
    {
        if (!EffectiveIsVisible || !EffectiveIsEnabled)
            return null;

        if (Required && IsEmpty)
            return requiredMessage;

        foreach (Func<QiuField, string?> validator in validators)
        {
            string? message = validator(this);
            if (!string.IsNullOrEmpty(message))
                return message;
        }

        return null;
    }

    /// <summary>Whether the field holds no value.</summary>
    protected abstract bool IsEmpty { get; }
}

/// <summary>Base class for a field holding a typed value.</summary>
/// <typeparam name="T">The value type.</typeparam>
public abstract class QiuField<T> : QiuField
{
    private T value;

    /// <summary>Creates a field with the given label and initial value.</summary>
    protected QiuField(string label, T value)
        : base(label)
    {
        this.value = value;
    }

    /// <summary>
    /// The current value. Setting it applies <see cref="Normalize"/> and raises <see cref="Changed"/>.
    /// </summary>
    public T Value
    {
        get => value;
        set
        {
            T normalized = Normalize(value);
            if (EqualityComparer<T>.Default.Equals(this.value, normalized))
                return;

            this.value = normalized;
            Changed?.Invoke(this, normalized);
            RaiseValueChanged();
        }
    }

    /// <summary>Raised after <see cref="Value"/> changed, including edits made in the host.</summary>
    public event EventHandler<T>? Changed;

    /// <summary>
    /// Sets the value as the result of a user interaction in a host.
    /// Equivalent to assigning <see cref="Value"/>; provided for host implementers.
    /// </summary>
    public void SetValueFromHost(T newValue) => Value = newValue;

    /// <summary>Constrains a value to what this field accepts (range, length, ...).</summary>
    protected virtual T Normalize(T newValue) => newValue;
}
