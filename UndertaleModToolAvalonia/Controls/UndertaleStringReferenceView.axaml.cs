using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactions.DragAndDrop;
using Microsoft.Extensions.DependencyInjection;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

public partial class UndertaleStringReferenceView : UserControl
{
    /// <summary>Matches <see cref="ReferenceAutoComplete.MaxSuggestions"/>.</summary>
    const int SuggestionLimit = 100;

    public static readonly StyledProperty<UndertaleString> ReferenceProperty = AvaloniaProperty.Register<UndertaleStringReferenceView, UndertaleString>(
        nameof(Reference), defaultBindingMode: BindingMode.TwoWay);
    public UndertaleString Reference
    {
        get { return GetValue(ReferenceProperty); }
        set { SetValue(ReferenceProperty, value); }
    }

    readonly MainViewModel mainVM = App.Services.GetRequiredService<MainViewModel>();
    readonly ReferenceAutoComplete autoComplete;

    public UndertaleStringReferenceView()
    {
        InitializeComponent();

        UpdateTextBoxWatermark();

        autoComplete = new ReferenceAutoComplete(ReferenceTextBox, SuggestionsPopup, SuggestionsListBox)
        {
            SuggestionProvider = GetSuggestions,
            SuggestionAccepted = AcceptSuggestion,
            TextCommitted = UpdateReferenceToText,
            // Enter has to insert a line break here, so it only applies the text with Ctrl held
            // (or when an entry of the dropdown is highlighted).
            AutoSelectFirstSuggestion = false,
            CommitKeyFilter = modifiers => modifiers.HasFlag(KeyModifiers.Control),
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ReferenceProperty)
        {
            UpdateTextBoxWatermark();
        }
    }

    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromLogicalTree(e);

        UpdateReferenceToText();
    }

    private void TextBox_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Middle
            && ((e.Source as Visual)?.GetTransformedBounds()?.Contains(e.GetPosition(null)) ?? false))
        {
            OpenInNewTab();
        }
    }

    /// <summary>Builds the dropdown entries for the text typed so far.</summary>
    IReadOnlyList<ReferenceSuggestion> GetSuggestions(string query)
    {
        UndertaleData? data = mainVM.Data;
        if (data?.Strings is null)
            return [];

        List<ReferenceSuggestion> result = [];
        List<ReferenceSuggestion>? tail = null;
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (UndertaleString str in data.Strings)
        {
            string? content = str.Content;
            if (string.IsNullOrEmpty(content))
                continue;

            if (content.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                if (result.Count >= SuggestionLimit)
                    break;
                if (seen.Add(content))
                    result.Add(new ReferenceSuggestion(ToSingleLine(content), str));
            }
            else if (content.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                tail ??= [];
                if (tail.Count < SuggestionLimit && seen.Add(content))
                    tail.Add(new ReferenceSuggestion(ToSingleLine(content), str));
            }
        }

        if (tail is not null)
        {
            foreach (ReferenceSuggestion suggestion in tail)
            {
                if (result.Count >= SuggestionLimit)
                    break;
                result.Add(suggestion);
            }
        }

        return result;
    }

    void AcceptSuggestion(ReferenceSuggestion suggestion)
    {
        if (suggestion.Value is UndertaleString str)
            Reference = str;
    }

    public void Add()
    {
        if (mainVM.Data is not null)
            Reference = mainVM.Data.Strings.MakeString("");
    }

    public void Open()
    {
        _ = mainVM.TabOpen(Reference);
    }

    public void OpenInNewTab()
    {
        _ = mainVM.TabOpen(Reference, inNewTab: true);
    }

    void UpdateReferenceToText()
    {
        if (mainVM.Data is not null && ReferenceTextBox.Text is not null)
        {
            Reference = mainVM.Data.Strings.MakeString(ReferenceTextBox.Text);
        }
    }

    void UpdateTextBoxWatermark()
    {
        ReferenceTextBox.PlaceholderText = (Reference is null) ? LocalizationSource.GetString("RefTooltip_StringRefPlaceholder") : "";
    }

    /// <summary>Flattens a string content into a single, reasonably short dropdown line.</summary>
    static string ToSingleLine(string content, int maxLength = 120)
    {
        string text = content.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length > maxLength)
            text = text[..(maxLength - 3)] + "...";
        return text;
    }
}

public class UndertaleStringDropHandler : DropHandlerBase
{
    public override bool Validate(object? sender, DragEventArgs e, object? sourceContext, object? targetContext, object? state)
    {
        if (targetContext is UndertaleStringReferenceView vm)
        {
            if (sourceContext is DataExplorerViewModel.Item item && item.Value is UndertaleString resource)
            {
                return true;
            }
        }
        return false;
    }
    public override bool Execute(object? sender, DragEventArgs e, object? sourceContext, object? targetContext, object? state)
    {
        if (targetContext is UndertaleStringReferenceView vm)
        {
            if (sourceContext is DataExplorerViewModel.Item item && item.Value is UndertaleString resource)
            {
                vm.Reference = resource;
                return true;
            }
        }
        return false;
    }
}
