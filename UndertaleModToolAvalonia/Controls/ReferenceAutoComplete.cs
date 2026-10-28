using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using UndertaleModLib;

namespace UndertaleModToolAvalonia;

/// <summary>
/// One entry of a reference autocomplete dropdown.
/// </summary>
public sealed class ReferenceSuggestion
{
    public ReferenceSuggestion(string text, object? value)
    {
        Text = text;
        Value = value;
    }

    /// <summary>The text shown in the dropdown.</summary>
    public string Text { get; }

    /// <summary>The object which gets referenced when this entry is picked.</summary>
    public object? Value { get; }

    public override string ToString() => Text;
}

/// <summary>
/// Builds the entries offered by the reference autocomplete dropdowns.
/// </summary>
public static class ReferenceSuggestions
{
    /// <summary>
    /// Resolves the resource list of <paramref name="resourceType"/>, or <see langword="null"/> when
    /// the data file has no such list (or when the type can hold any resource).
    /// </summary>
    public static IList? GetResourceList(UndertaleData data, Type resourceType)
    {
        if (resourceType == typeof(UndertaleResource))
            return null;

        try
        {
            return data[resourceType];
        }
        catch (Exception e) when (e is NotSupportedException or MissingMemberException)
        {
            return null;
        }
    }

    /// <summary>
    /// Enumerates every resource list of the data file. Used for references which are not restricted
    /// to a single resource type.
    /// </summary>
    public static IEnumerable<IList> EnumerateResourceLists(UndertaleData data)
    {
        foreach (PropertyInfo property in data.GetType().GetProperties())
        {
            if (!property.PropertyType.IsGenericType
                || property.PropertyType.GetGenericTypeDefinition() != typeof(IList<>)
                || !typeof(UndertaleResource).IsAssignableFrom(property.PropertyType.GetGenericArguments()[0]))
            {
                continue;
            }

            IList? list = null;
            try
            {
                list = property.GetValue(data) as IList;
            }
            catch
            {
                // A list which cannot be read simply offers no suggestions.
            }

            if (list is not null)
                yield return list;
        }
    }

    /// <summary>
    /// Appends the named resources of <paramref name="list"/> which match <paramref name="query"/>,
    /// ranking name-prefix matches before the remaining substring matches. At most <paramref name="limit"/>
    /// entries are added.
    /// </summary>
    /// <param name="skip">An entry which was already added by the caller and must not be duplicated.</param>
    public static void AddNameMatches(IList list, string query, List<ReferenceSuggestion> result, int limit, object? skip = null)
    {
        List<ReferenceSuggestion>? tail = null;

        for (int i = 0; i < list.Count && result.Count < limit; i++)
        {
            if (list[i] is not UndertaleNamedResource resource || ReferenceEquals(resource, skip))
                continue;

            string? name = resource.Name?.Content;
            if (string.IsNullOrEmpty(name))
                continue;

            if (query.Length == 0)
            {
                result.Add(new ReferenceSuggestion(name, resource));
            }
            else if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new ReferenceSuggestion(name, resource));
            }
            else if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                tail ??= [];
                if (tail.Count < limit)
                    tail.Add(new ReferenceSuggestion(name, resource));
            }
        }

        if (tail is null)
            return;

        foreach (ReferenceSuggestion suggestion in tail)
        {
            if (result.Count >= limit)
                return;
            result.Add(suggestion);
        }
    }
}

/// <summary>
/// Autocomplete for the reference views: while the user types in a text box, a dropdown offers the
/// matching resources, and picking an entry applies it to the reference. Plain Enter (or losing focus)
/// applies the typed text itself.
/// </summary>
public sealed class ReferenceAutoComplete
{
    /// <summary>Longer or multi-line content (text blobs such as shader source) never opens the dropdown.</summary>
    public int MaxQueryLength { get; set; } = 80;

    /// <summary>Maximum number of entries offered for a non-empty query.</summary>
    public int MaxSuggestions { get; set; } = 100;

    /// <summary>Maximum number of entries offered while the text box is still empty.</summary>
    public int MaxSuggestionsWhenEmpty { get; set; } = 50;

    /// <summary>Whether the best match is highlighted as soon as the dropdown opens.</summary>
    public bool AutoSelectFirstSuggestion { get; set; } = true;

    /// <summary>Returns the entries matching the given text, best match first.</summary>
    public Func<string, IReadOnlyList<ReferenceSuggestion>>? SuggestionProvider { get; set; }

    /// <summary>Called when an entry was picked from the dropdown.</summary>
    public Action<ReferenceSuggestion>? SuggestionAccepted { get; set; }

    /// <summary>Called when the text box content itself should be applied to the reference.</summary>
    public Action? TextCommitted { get; set; }

    /// <summary>Whether losing focus applies the text box content.</summary>
    public bool CommitOnLostFocus { get; set; } = true;

    /// <summary>
    /// Decides whether the current Enter press applies the text box content. Defaults to always;
    /// views where Enter is meaningful text input (the multi-line string box) can restrict it.
    /// </summary>
    public Func<KeyModifiers, bool>? CommitKeyFilter { get; set; }

    readonly TextBox textBox;
    readonly Popup popup;
    readonly ListBox listBox;
    readonly List<ReferenceSuggestion> shown = [];

    bool suppressRefresh;
    bool pointerInPopup;

    public ReferenceAutoComplete(TextBox textBox, Popup popup, ListBox listBox)
    {
        this.textBox = textBox;
        this.popup = popup;
        this.listBox = listBox;

        textBox.TextChanged += (_, _) => OnTextChanged();
        textBox.GotFocus += (_, _) => OnGotFocus();
        textBox.LostFocus += (_, _) => OnLostFocus();
        textBox.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        // A press inside the dropdown must not be mistaken for "the user left the text box",
        // otherwise the text typed so far would be applied instead of the clicked entry.
        if (popup.Child is not null)
            popup.Child.AddHandler(InputElement.PointerPressedEvent, OnPopupPointerPressed, RoutingStrategies.Tunnel);
        listBox.AddHandler(InputElement.PointerPressedEvent, OnPopupPointerPressed, RoutingStrategies.Tunnel);
        listBox.PointerReleased += OnListPointerReleased;
        popup.Closed += (_, _) => pointerInPopup = false;
    }

    public bool IsOpen => popup.IsOpen;

    /// <summary>
    /// Runs <paramref name="update"/> without refreshing the dropdown, for text box updates which
    /// do not come from the user typing.
    /// </summary>
    public void SuppressRefresh(Action update)
    {
        bool previous = suppressRefresh;
        suppressRefresh = true;
        try
        {
            update();
        }
        finally
        {
            suppressRefresh = previous;
        }
    }

    /// <summary>Recomputes the dropdown content for the current text box content.</summary>
    public void Refresh()
    {
        if (suppressRefresh)
            return;

        string text = textBox.Text ?? "";
        if (SuggestionProvider is null || !CanSuggest(text))
        {
            Close();
            return;
        }

        IReadOnlyList<ReferenceSuggestion> suggestions = SuggestionProvider(text) ?? [];
        int limit = (text.Length == 0) ? MaxSuggestionsWhenEmpty : MaxSuggestions;

        shown.Clear();
        for (int i = 0; i < suggestions.Count && shown.Count < limit; i++)
            shown.Add(suggestions[i]);

        if (shown.Count == 0)
        {
            Close();
            return;
        }

        listBox.ItemsSource = shown.ToArray();
        listBox.SelectedIndex = (text.Length > 0 && AutoSelectFirstSuggestion) ? 0 : -1;
        popup.IsOpen = true;
        if (listBox.SelectedIndex >= 0)
            listBox.ScrollIntoView(shown[0]);
    }

    /// <summary>Applies the highlighted entry, if there is one.</summary>
    public bool AcceptSuggestion()
    {
        if (listBox.SelectedItem is not ReferenceSuggestion suggestion)
            return false;

        suppressRefresh = true;
        try
        {
            Close();
            SuggestionAccepted?.Invoke(suggestion);
            // The click may have moved focus to the dropdown; typing should continue in the text box.
            textBox.Focus();
        }
        finally
        {
            suppressRefresh = false;
        }

        return true;
    }

    public void Close()
    {
        if (popup.IsOpen)
            popup.IsOpen = false;
        listBox.SelectedIndex = -1;
    }

    bool CanSuggest(string text)
    {
        return text.Length <= MaxQueryLength
            && text.IndexOf('\n') < 0
            && text.IndexOf('\r') < 0;
    }

    void CommitText()
    {
        suppressRefresh = true;
        try
        {
            Close();
            TextCommitted?.Invoke();
        }
        finally
        {
            suppressRefresh = false;
        }
    }

    void OnTextChanged()
    {
        if (suppressRefresh)
            return;

        // Only typing opens the dropdown; text changes from bindings (drag & drop, "add", undo)
        // must not pop it open.
        if (!textBox.IsFocused)
        {
            Close();
            return;
        }

        Refresh();
    }

    void OnGotFocus()
    {
        pointerInPopup = false;

        // Offers what is available when the text box is empty; never re-opens over existing content.
        if (string.IsNullOrEmpty(textBox.Text))
            Refresh();
    }

    void OnLostFocus()
    {
        // An entry of the dropdown is being clicked; the click applies it, so do not apply the typed text.
        if (pointerInPopup)
            return;

        if (CommitOnLostFocus)
            CommitText();
        else
            Close();
    }

    void OnPopupPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        pointerInPopup = true;
    }

    void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        bool pressedInsideDropdown = pointerInPopup;
        pointerInPopup = false;

        if (e.InitialPressMouseButton != MouseButton.Left)
            return;

        Visual? source = e.Source as Visual;

        // Scrolling the dropdown must not pick anything.
        if (source?.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null)
            return;

        if (source?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } item
            && (item.Content as ReferenceSuggestion ?? item.DataContext as ReferenceSuggestion) is { } suggestion)
        {
            e.Handled = true;
            listBox.SelectedItem = suggestion;
            AcceptSuggestion();
            return;
        }

        // The press landed on the dropdown itself but not on an entry: treat it as leaving the text box.
        if (pressedInsideDropdown)
            CommitText();
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when popup.IsOpen:
                e.Handled = MoveSelection(1);
                break;

            case Key.Up when popup.IsOpen:
                e.Handled = MoveSelection(-1);
                break;

            case Key.Escape when popup.IsOpen:
                e.Handled = true;
                Close();
                break;

            case Key.Enter:
                if (popup.IsOpen && listBox.SelectedIndex >= 0)
                {
                    e.Handled = true;
                    AcceptSuggestion();
                }
                else if (CommitKeyFilter?.Invoke(e.KeyModifiers) ?? true)
                {
                    e.Handled = true;
                    CommitText();
                }
                break;
        }
    }

    bool MoveSelection(int delta)
    {
        if (shown.Count == 0)
            return false;

        int index = listBox.SelectedIndex;
        index = (index < 0) ? (delta > 0 ? 0 : shown.Count - 1) : index + delta;
        index = Math.Clamp(index, 0, shown.Count - 1);

        listBox.SelectedIndex = index;
        listBox.ScrollIntoView(shown[index]);
        return true;
    }
}
