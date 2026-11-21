// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace QiuForms;

/// <summary>
/// Base class for everything that can be placed in a <see cref="QiuForm"/>.
/// </summary>
public abstract class QiuElement
{
    private readonly List<Func<bool>> enabledConditions = new();
    private readonly List<Func<bool>> visibleConditions = new();

    /// <summary>Optional identifier, used with <see cref="QiuForm.Find(string)"/>.</summary>
    public string? Id { get; set; }

    /// <summary>Whether the element is enabled. Defaults to <see langword="true"/>.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Whether the element is visible. Defaults to <see langword="true"/>.</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>Optional tooltip text shown by the host.</summary>
    public string? ToolTip { get; set; }

    /// <summary>
    /// <see langword="true"/> when <see cref="IsEnabled"/> is set and every
    /// <see cref="EnabledWhen"/> condition currently holds.
    /// </summary>
    /// <remarks>Hosts re-read this after every field change, so conditions may reference other fields.</remarks>
    public bool EffectiveIsEnabled => IsEnabled && Evaluate(enabledConditions) && (Parent?.EffectiveIsEnabled ?? true);

    /// <summary>
    /// <see langword="true"/> when <see cref="IsVisible"/> is set and every
    /// <see cref="VisibleWhen"/> condition currently holds.
    /// </summary>
    public bool EffectiveIsVisible => IsVisible && Evaluate(visibleConditions) && (Parent?.EffectiveIsVisible ?? true);

    /// <summary>The panel this element was added to, if any. Used to inherit enabled/visible state.</summary>
    internal QiuPanel? Parent { get; set; }

    /// <summary>Adds a condition that controls whether the element is enabled.</summary>
    /// <param name="condition">Evaluated whenever a field of the form changes.</param>
    public QiuElement EnabledWhen(Func<bool> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        enabledConditions.Add(condition);
        return this;
    }

    /// <summary>Adds a condition that controls whether the element is visible.</summary>
    /// <param name="condition">Evaluated whenever a field of the form changes.</param>
    public QiuElement VisibleWhen(Func<bool> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        visibleConditions.Add(condition);
        return this;
    }

    private static bool Evaluate(List<Func<bool>> conditions)
    {
        foreach (Func<bool> condition in conditions)
        {
            if (!condition())
                return false;
        }
        return true;
    }
}
