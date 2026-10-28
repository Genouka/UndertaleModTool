using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactions.DragAndDrop;
using Microsoft.Extensions.DependencyInjection;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

using AddFuncType = Func<object?, Task<UndertaleResource?>>;

public partial class UndertaleResourceReferenceView : UserControl
{
    /// <summary>Matches <see cref="ReferenceAutoComplete.MaxSuggestions"/>.</summary>
    const int SuggestionLimit = 100;

    public static readonly StyledProperty<UndertaleResource?> ReferenceProperty = AvaloniaProperty.Register<UndertaleResourceReferenceView, UndertaleResource?>(
        nameof(Reference), defaultBindingMode: BindingMode.TwoWay);
    public UndertaleResource? Reference
    {
        get { return GetValue(ReferenceProperty); }
        set { SetValue(ReferenceProperty, value); }
    }

    public static readonly StyledProperty<Type> ReferenceTypeProperty = AvaloniaProperty.Register<UndertaleResourceReferenceView, Type>(
        nameof(ReferenceType));
    public Type ReferenceType
    {
        get { return GetValue(ReferenceTypeProperty); }
        set { SetValue(ReferenceTypeProperty, value); }
    }

    public static readonly StyledProperty<AddFuncType?> AddFuncProperty = AvaloniaProperty.Register<UndertaleResourceReferenceView, AddFuncType?>(
        nameof(AddFunc));
    public AddFuncType? AddFunc
    {
        get { return GetValue(AddFuncProperty); }
        set { SetValue(AddFuncProperty, value); }
    }

    public static readonly StyledProperty<object?> AddFuncArgumentProperty = AvaloniaProperty.Register<UndertaleResourceReferenceView, object?>(
        nameof(AddFuncArgument));
    public object? AddFuncArgument
    {
        get { return GetValue(AddFuncArgumentProperty); }
        set { SetValue(AddFuncArgumentProperty, value); }
    }

    readonly MainViewModel mainVM = App.Services.GetRequiredService<MainViewModel>();
    readonly ReferenceAutoComplete autoComplete;

    public UndertaleResourceReferenceView()
    {
        InitializeComponent();

        autoComplete = new ReferenceAutoComplete(ReferenceTextBox, SuggestionsPopup, SuggestionsListBox)
        {
            SuggestionProvider = GetSuggestions,
            SuggestionAccepted = AcceptSuggestion,
            TextCommitted = UpdateReferenceToText,
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ReferenceTypeProperty)
        {
            Type? referenceType = ReferenceType;
            if (referenceType is null)
                return;

            string name = referenceType.Name;
            if (name[.."Undertale".Length] == "Undertale")
            {
                name = name["Undertale".Length..];
            }
            ReferenceTextBox.PlaceholderText = string.Format(LocalizationSource.GetString("RefTooltip_ObjRefPlaceholder"), name);
        }
    }

    private void TextBox_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Middle
            && ((e.Source as Visual)?.GetTransformedBounds()?.Contains(e.GetPosition(null)) ?? false))
        {
            OpenInNewTab();
        }
    }

    private void TextBox_DoubleTapped(object? sender, TappedEventArgs e)
    {
        Open();
    }

    /// <summary>Builds the dropdown entries for the text typed so far.</summary>
    IReadOnlyList<ReferenceSuggestion> GetSuggestions(string query)
    {
        UndertaleData? data = mainVM.Data;
        if (data is null || ReferenceType is null)
            return [];

        List<ReferenceSuggestion> result = [];

        if (ReferenceType == typeof(UndertaleResource))
        {
            // A reference which may point at any resource type (e.g. the "find references" window):
            // offer names from every resource list of the data file.
            foreach (IList list in ReferenceSuggestions.EnumerateResourceLists(data))
                ReferenceSuggestions.AddNameMatches(list, query, result, SuggestionLimit);
            return result;
        }

        IList? resourceList = ReferenceSuggestions.GetResourceList(data, ReferenceType);
        if (resourceList is null)
            return [];

        // An ID typed by the user refers to the resource list itself, so it is offered first.
        object? byIndex = null;
        if (int.TryParse(query, out int index) && index >= 0 && index < resourceList.Count)
        {
            byIndex = resourceList[index];
            if (byIndex is UndertaleResource resource)
                result.Add(new ReferenceSuggestion(GetResourceName(resource) ?? $"#{index}", resource));
        }

        ReferenceSuggestions.AddNameMatches(resourceList, query, result, SuggestionLimit, byIndex);

        return result;
    }

    void AcceptSuggestion(ReferenceSuggestion suggestion)
    {
        if (suggestion.Value is UndertaleResource resource)
            Reference = resource;
    }

    void UpdateReferenceToText()
    {
        if (mainVM.Data is null)
            return;

        string text = ReferenceTextBox.Text ?? "";

        if (string.IsNullOrEmpty(text))
        {
            Reference = null;
        }
        else if (ParseResourceText(text) is UndertaleResource reference)
        {
            Reference = reference;
        }

        // Update text box to reflect current reference value
        autoComplete.SuppressRefresh(() =>
            BindingOperations.GetBindingExpressionBase(ReferenceTextBox, TextBox.TextProperty)?.UpdateTarget());
    }

    UndertaleResource? ParseResourceText(string text)
    {
        UndertaleData? data = mainVM.Data;
        if (data is null || ReferenceType is null)
            return null;

        if (ReferenceType == typeof(UndertaleResource))
        {
            foreach (IList list in ReferenceSuggestions.EnumerateResourceLists(data))
            {
                if (FindByName(list, text) is UndertaleResource resource)
                    return resource;
            }
            return null;
        }

        IList? resourceList = ReferenceSuggestions.GetResourceList(data, ReferenceType);
        if (resourceList is null)
            return null;

        if (int.TryParse(text, out int id) && id >= 0 && id < resourceList.Count)
            return resourceList[id] as UndertaleResource;

        return FindByName(resourceList, text);
    }

    static UndertaleResource? FindByName(IList list, string text)
    {
        return list
            .OfType<UndertaleNamedResource>()
            .FirstOrDefault(x => x.Name?.Content?.Equals(text, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    static string? GetResourceName(UndertaleResource resource)
    {
        return (resource as UndertaleNamedResource)?.Name?.Content;
    }

    public async void Add()
    {
        if (AddFunc is not null)
        {
            UndertaleResource? reference = await AddFunc(AddFuncArgument);
            if (reference is not null)
                Reference = reference;
        }
    }

    public void Open()
    {
        _ = mainVM.TabOpen(Reference);
    }

    public void OpenInNewTab()
    {
        _ = mainVM.TabOpen(Reference, inNewTab: true);
    }

    public void Remove()
    {
        Reference = null;
    }
}

public class UndertaleReferenceDropHandler : DropHandlerBase
{
    public override bool Validate(object? sender, DragEventArgs e, object? sourceContext, object? targetContext, object? state)
    {
        if (targetContext is UndertaleResourceReferenceView vm)
        {
            if (sourceContext is DataExplorerViewModel.Item item && item.Value is UndertaleResource resource && vm.ReferenceType.IsInstanceOfType(resource))
            {
                return true;
            }
        }
        return false;
    }
    public override bool Execute(object? sender, DragEventArgs e, object? sourceContext, object? targetContext, object? state)
    {
        if (targetContext is UndertaleResourceReferenceView vm)
        {
            if (sourceContext is DataExplorerViewModel.Item item && item.Value is UndertaleResource resource && vm.ReferenceType.IsInstanceOfType(resource))
            {
                vm.Reference = resource;
                return true;
            }
        }
        return false;
    }
}
