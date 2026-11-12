using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using UndertaleModLib;
using UndertaleModLib.Diff;
using UndertaleModLib.Models;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Compares the currently open data file against another data file selected by the user.
/// </summary>
public partial class DataFileCompareViewModel : ObservableObject, IDisposable
{
    private readonly IView _view;
    private IStorageFile _compareFile;

    private CancellationTokenSource? _cts;
    private UndertaleData? _baseData;
    private UndertaleData? _compareData;
    private DataFileDiffResult? _result;
    private CodeDiffService? _codeDiffService;
    private Dictionary<string, string> _categoryTitles = new(StringComparer.Ordinal);
    private int _detailsVersion;
    private bool _disposed;
    private bool _disposeWhenIdle;

    public MainViewModel MainVM { get; }

    public static IReadOnlyList<string> StatusFilters { get; } =
        ["Differences", "All", "Added", "Removed", "Changed"];

    [ObservableProperty]
    public partial string BaseLabel { get; set; } = "Base: current data";

    [ObservableProperty]
    public partial string CompareLabel { get; set; } = "Compare: -";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial string WarningsText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasWarnings { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<CompareCategoryRow> Categories { get; set; } = [];

    [ObservableProperty]
    public partial CompareCategoryRow? SelectedCategory { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<CompareEntryRow> Entries { get; set; } = [];

    [ObservableProperty]
    public partial CompareEntryRow? SelectedEntry { get; set; }

    [ObservableProperty]
    public partial string FilterText { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedStatusFilter { get; set; } = "Differences";

    public IReadOnlyList<string> StatusFilterOptions => StatusFilters;

    [ObservableProperty]
    public partial string DetailsTitle { get; set; } = "Select a resource to see details";
    [ObservableProperty]
    public partial int DetailsTabIndex { get; set; }
    [ObservableProperty]
    public partial string DetailsNote { get; set; } = "";

    [ObservableProperty]
    public partial bool IsDetailsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsCodeDetails { get; set; }

    [ObservableProperty]
    public partial bool IsPropertyDetails { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<ComparePropertyRow> PropertyDiffs { get; set; } = [];

    [ObservableProperty]
    public partial string CodeDiffText { get; set; } = "";

    public DataFileCompareViewModel(IServiceProvider serviceProvider, IView view, IStorageFile compareFile)
    {
        MainVM = serviceProvider.GetRequiredService<MainViewModel>();
        _view = view;
        _compareFile = compareFile;
        CompareLabel = "Compare: " + compareFile.Name;
    }

    public async Task StartAsync()
    {
        if (_disposed || MainVM.Data is null)
            return;

        _baseData = MainVM.Data;
        BaseLabel = "Base: " + (MainVM.DataPath ?? "current data (unsaved)");
        CompareLabel = "Compare: " + _compareFile.Name;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken cancellationToken = _cts.Token;

        _detailsVersion++;
        _codeDiffService = null;
        DisposeCompareData();
        _result = null;
        Categories = [];
        Entries = [];
        SelectedCategory = null;
        SelectedEntry = null;
        ClearDetails();
        IsDetailsBusy = false;

        IsBusy = true;
        IsProgressIndeterminate = true;
        ProgressPercent = 0;
        HasWarnings = false;
        WarningsText = "";
        StatusText = "Loading " + _compareFile.Name + "...";

        try
        {
            List<string> warnings = [];
            UndertaleData compareData;
            using (System.IO.Stream stream = await _compareFile.OpenReadAsync())
            {
                compareData = await Task.Run(() => UndertaleIO.Read(stream,
                    (string warning, bool isImportant) =>
                    {
                        lock (warnings)
                            warnings.Add(warning);
                    },
                    _ => { }), cancellationToken);
            }

            _compareData = compareData;
            cancellationToken.ThrowIfCancellationRequested();
            _codeDiffService = new CodeDiffService(_baseData, compareData);

            if (warnings.Count > 0)
            {
                lock (warnings)
                {
                    WarningsText = string.Join(Environment.NewLine, warnings);
                    HasWarnings = true;
                }
            }

            IsProgressIndeterminate = false;
            StatusText = "Comparing...";

            Progress<DataFileDiffProgress> progress = new(p =>
            {
                ProgressPercent = p.Percent;
                StatusText = p.Completed >= p.Total
                    ? "Finishing..."
                    : "Comparing " + p.Message + "...";
            });

            DataFileDiffer differ = new(_baseData, compareData);
            DataFileDiffResult result = await Task.Run(
                () => differ.Compare(progress, cancellationToken), cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            _result = result;
            ApplyResult(result);

            StatusText = $"Done: {result.AddedCount} added, {result.RemovedCount} removed, " +
                         $"{result.ChangedCount} changed, {result.UnchangedCount} unchanged.";
            ProgressPercent = 100;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Comparison cancelled.";
        }
        catch (Exception e)
        {
            StatusText = "Comparison failed: " + e.Message;
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            DisposeIfIdle();
        }
    }

    private void ApplyResult(DataFileDiffResult result)
    {
        List<CompareCategoryRow> rows = [];
        int totalAdded = 0;
        int totalRemoved = 0;
        int totalChanged = 0;
        int totalUnchanged = 0;
        List<AssetDiffEntry> allEntries = [];

        Dictionary<string, string> categoryTitles = new(StringComparer.Ordinal);
        foreach (AssetCategoryDiff category in result.Categories)
        {
            categoryTitles[category.Id] = category.Title;
            allEntries.AddRange(category.Entries);
            totalAdded += category.AddedCount;
            totalRemoved += category.RemovedCount;
            totalChanged += category.ChangedCount;
            totalUnchanged += category.UnchangedCount;

            if (category.Entries.Count > 0)
                rows.Add(new CompareCategoryRow(category));
        }

        _categoryTitles = categoryTitles;
        rows.Insert(0, new CompareCategoryRow("All resources",
            allEntries, totalAdded, totalRemoved, totalChanged, totalUnchanged));

        Categories = new ObservableCollection<CompareCategoryRow>(rows);
        SelectedCategory = Categories[0];
    }

    partial void OnSelectedCategoryChanged(CompareCategoryRow? value)
    {
        RebuildEntries();
    }

    partial void OnFilterTextChanged(string value)
    {
        RebuildEntries();
    }

    partial void OnSelectedStatusFilterChanged(string value)
    {
        RebuildEntries();
    }

    private void RebuildEntries()
    {
        IEnumerable<AssetDiffEntry> source = SelectedCategory?.Entries ?? _result?.Categories
            .SelectMany(c => c.Entries) ?? Enumerable.Empty<AssetDiffEntry>();

        source = SelectedStatusFilter switch
        {
            "All" => source,
            "Added" => source.Where(e => e.Change == AssetChangeKind.Added),
            "Removed" => source.Where(e => e.Change == AssetChangeKind.Removed),
            "Changed" => source.Where(e => e.Change == AssetChangeKind.Changed),
            _ => source.Where(e => e.Change != AssetChangeKind.Unchanged),
        };

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            string filter = FilterText.Trim();
            source = source.Where(e => e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        Entries = new ObservableCollection<CompareEntryRow>(
            source.Select(e => new CompareEntryRow(e,
                _categoryTitles.GetValueOrDefault(e.TypeId, e.TypeId))));
        SelectedEntry = Entries.FirstOrDefault();
    }

    partial void OnSelectedEntryChanged(CompareEntryRow? value)
    {
        _ = UpdateDetailsAsync(value);
    }

    private async Task UpdateDetailsAsync(CompareEntryRow? row)
    {
        int version = ++_detailsVersion;
        ClearDetails();

        if (row is null)
            return;

        AssetDiffEntry entry = row.Entry;
        DetailsTitle = row.Name;

        if (entry.BaseResource is null || entry.CompareResource is null)
        {
            DetailsNote = entry.BaseResource is null
                ? "This resource only exists in the compared file."
                : "This resource only exists in the base file.";
            IsPropertyDetails = true;
            return;
        }

        if (entry.BaseResource is not UndertaleCode baseCode ||
            entry.CompareResource is not UndertaleCode compareCode)
        {
            IsDetailsBusy = true;
            try
            {
                PropertyDiffResult diffs = await Task.Run(
                    () => AssetPropertyDiff.Compare(entry.BaseResource, entry.CompareResource));

                if (version != _detailsVersion)
                    return;

                PropertyDiffs = new ObservableCollection<ComparePropertyRow>(
                    diffs.Differences.Select(d => new ComparePropertyRow(d.Path, d.BaseValue, d.CompareValue)));
                DetailsNote = diffs.Truncated
                    ? "Only the first 5000 differences are shown."
                    : PropertyDiffs.Count == 0 ? "No property differences." : "";
                IsPropertyDetails = true;
                DetailsTabIndex = 0;
            }
            catch (Exception e)
            {
                if (version == _detailsVersion)
                    DetailsNote = "Failed to compare properties: " + e.Message;
            }
            finally
            {
                IsDetailsBusy = false;
                DisposeIfIdle();
            }
            return;
        }

        IsDetailsBusy = true;
        try
        {
            CodeDiffService? service = _codeDiffService;
            if (service is null)
                return;

            CodeDiffResult diff = await Task.Run(() => service.Compare(baseCode, compareCode));
            if (version != _detailsVersion)
                return;

            IsCodeDetails = true;
            DetailsTabIndex = 1;
            CodeDiffText = diff.DiffText;

            List<string> notes = [];
            if (diff.BaseDecompileFailed || diff.CompareDecompileFailed)
                notes.Add("Decompilation failed; showing disassembly.");
            if (!diff.HasTextDifferences)
                notes.Add("No textual differences after decompilation (bytecode layout may differ).");
            if (diff.MetadataDifferences.Count > 0)
            {
                PropertyDiffs = new ObservableCollection<ComparePropertyRow>(
                    diff.MetadataDifferences.Select(d => new ComparePropertyRow(d.Path, d.BaseValue, d.CompareValue)));
                notes.Add("Code metadata differences are listed in the Properties tab.");
            }
            DetailsNote = string.Join(" ", notes);
        }
        catch (Exception e)
        {
            if (version == _detailsVersion)
                DetailsNote = "Failed to compare code: " + e.Message;
        }
        finally
        {
            IsDetailsBusy = false;
            DisposeIfIdle();
        }
    }

    private void ClearDetails()
    {
        DetailsTitle = "Select a resource to see details";
        DetailsNote = "";
        IsCodeDetails = false;
        IsPropertyDetails = false;
        DetailsTabIndex = 0;
        PropertyDiffs = [];
        CodeDiffText = "";
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        IReadOnlyList<IStorageFile> files = await _view.OpenFileDialog(new FilePickerOpenOptions
        {
            Title = "Select a data file to compare",
            FileTypeFilter = FilePickerFileTypes.Data,
        });
        if (files.Count != 1)
            return;

        _compareFile = files[0];
        await StartAsync();
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _detailsVersion++;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _codeDiffService = null;
        _result = null;
        _categoryTitles.Clear();

        if (IsBusy || IsDetailsBusy)
        {
            _disposeWhenIdle = true;
            return;
        }

        DisposeCompareData();
    }

    private void DisposeIfIdle()
    {
        if (!_disposeWhenIdle || IsBusy || IsDetailsBusy)
            return;

        _disposeWhenIdle = false;
        DisposeCompareData();
    }

    private void DisposeCompareData()
    {
        _compareData?.Dispose();
        _compareData = null;
    }
}

/// <summary>One category row in the comparison window.</summary>
public sealed class CompareCategoryRow
{
    public string Title { get; }
    public IReadOnlyList<AssetDiffEntry> Entries { get; }
    public string CountsText { get; }

    public CompareCategoryRow(AssetCategoryDiff category)
        : this(category.Title, category.Entries,
               category.AddedCount, category.RemovedCount, category.ChangedCount, category.UnchangedCount)
    {
    }

    public CompareCategoryRow(string displayTitle, IReadOnlyList<AssetDiffEntry> entries,
                              int added, int removed, int changed, int unchanged)
    {
        Title = displayTitle;
        Entries = entries;

        List<string> parts = [];
        if (added > 0)
            parts.Add($"+{added}");
        if (removed > 0)
            parts.Add($"-{removed}");
        if (changed > 0)
            parts.Add($"~{changed}");
        if (parts.Count == 0)
            parts.Add("no differences");
        CountsText = string.Join(" ", parts);
    }
}

/// <summary>One resource row in the comparison window.</summary>
public sealed class CompareEntryRow
{
    public AssetDiffEntry Entry { get; }
    public string Name => Entry.Name;
    public string TypeTitle { get; }
    public string StatusText => Entry.Change switch
    {
        AssetChangeKind.Added => "Added",
        AssetChangeKind.Removed => "Removed",
        AssetChangeKind.Changed => "Changed",
        _ => "Unchanged",
    };

    public CompareEntryRow(AssetDiffEntry entry, string typeTitle)
    {
        Entry = entry;
        TypeTitle = typeTitle;
    }
}

/// <summary>One property row in the details grid.</summary>
public sealed record ComparePropertyRow(string Path, string BaseValue, string CompareValue);
