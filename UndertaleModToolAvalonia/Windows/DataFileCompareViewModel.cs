using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using UndertaleModLib;
using UndertaleModLib.Diff;
using UndertaleModLib.Models;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Compares the currently open data file against another data file selected by the user,
/// and can apply selected changes back to the open data.
/// </summary>
public partial class DataFileCompareViewModel : ObservableObject, IDisposable
{
    private readonly IView _view;
    private readonly HashSet<AssetDiffEntry> _selectedEntries = new(ReferenceEqualityComparer.Instance);
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

    [ObservableProperty]
    public partial string BaseLabel { get; set; } = "";

    [ObservableProperty]
    public partial string CompareLabel { get; set; } = "";

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

    /// <summary>0 = differences, 1 = all, 2 = added, 3 = removed, 4 = changed.</summary>
    [ObservableProperty]
    public partial int StatusFilterIndex { get; set; }

    [ObservableProperty]
    public partial string EntriesCountText { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedCountText { get; set; } = "";

    [ObservableProperty]
    public partial bool DeleteRemoved { get; set; }

    [ObservableProperty]
    public partial string DetailsTitle { get; set; } = "";

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
        ClearDetails();
        UpdateCounts();
    }

    private static string Loc(string key) => LocalizationSource.GetString(key);
    private static string LocalizeCategoryTitle(string id, string fallback)
    {
        return id switch
        {
            "GeneralInfo" => Loc("Tree_GeneralInfo"),
            "AudioGroups" => Loc("Tree_AudioGroups"),
            "Sounds" => Loc("Tree_Sounds"),
            "Sprites" => Loc("Tree_Sprites"),
            "Backgrounds" => Loc("Tree_BackgroundsTilesets"),
            "Paths" => Loc("Tree_Paths"),
            "Scripts" => Loc("Tree_Scripts"),
            "Shaders" => Loc("Tree_Shaders"),
            "Fonts" => Loc("Tree_Fonts"),
            "Timelines" => Loc("Tree_Timelines"),
            "GameObjects" => Loc("Tree_GameObjects"),
            "Rooms" => Loc("Tree_Rooms"),
            "Extensions" => Loc("Tree_Extensions"),
            "Sequences" => Loc("Compare_Cat_Sequences"),
            "AnimationCurves" => Loc("Tree_AnimationCurves"),
            "ParticleSystems" => Loc("Tree_ParticleSystems"),
            "ParticleSystemEmitters" => Loc("Tree_ParticleSystemEmitters"),
            "FilterEffects" => Loc("Compare_Cat_FilterEffects"),
            "UINodes" => Loc("Compare_Cat_UINodes"),
            "Code" => Loc("Tree_Code"),
            _ => fallback,
        };
    }


    public async Task StartAsync()
    {
        if (_disposed || MainVM.Data is null)
            return;

        _baseData = MainVM.Data;
        BaseLabel = string.Format(Loc("Compare_Base"), MainVM.DataPath ?? Loc("Compare_BaseCurrent"));
        CompareLabel = string.Format(Loc("Compare_Compare"), _compareFile.Name);

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken cancellationToken = _cts.Token;

        _detailsVersion++;
        _codeDiffService = null;
        DisposeCompareData();
        _result = null;
        _selectedEntries.Clear();
        Categories = [];
        Entries = [];
        SelectedCategory = null;
        SelectedEntry = null;
        ClearDetails();
        IsDetailsBusy = false;
        UpdateCounts();

        IsBusy = true;
        IsProgressIndeterminate = true;
        ProgressPercent = 0;
        HasWarnings = false;
        WarningsText = "";
        StatusText = string.Format(Loc("Compare_Loading"), _compareFile.Name);

        try
        {
            List<string> warnings = [];
            UndertaleData compareData;
            using (Stream stream = await _compareFile.OpenReadAsync())
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

            await RunComparisonAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            StatusText = Loc("Compare_Cancelled");
        }
        catch (Exception e)
        {
            StatusText = string.Format(Loc("Compare_ComparisonFailed"), e.Message);
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            DisposeIfIdle();
        }
    }

    private async Task RunComparisonAsync(CancellationToken cancellationToken)
    {
        if (_baseData is null || _compareData is null)
            return;

        IsProgressIndeterminate = false;
        ProgressPercent = 0;
        StatusText = Loc("Compare_Finishing");

        Progress<DataFileDiffProgress> progress = new(p =>
        {
            ProgressPercent = p.Percent;
            StatusText = p.Completed >= p.Total
                ? Loc("Compare_Finishing")
                : string.Format(Loc("Compare_Comparing"), p.Message);
        });

        DataFileDiffer differ = new(_baseData, _compareData);
        DataFileDiffResult result = await Task.Run(
            () => differ.Compare(progress, cancellationToken), cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        _result = result;
        ApplyResult(result);

        StatusText = string.Format(Loc("Compare_Done"),
            result.AddedCount, result.RemovedCount, result.ChangedCount, result.UnchangedCount);
        ProgressPercent = 100;
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
            string title = LocalizeCategoryTitle(category.Id, category.Title);
            categoryTitles[category.Id] = title;
            allEntries.AddRange(category.Entries);
            totalAdded += category.AddedCount;
            totalRemoved += category.RemovedCount;
            totalChanged += category.ChangedCount;
            totalUnchanged += category.UnchangedCount;

            if (category.Entries.Count > 0)
                rows.Add(new CompareCategoryRow(title, category.Entries,
                    category.AddedCount, category.RemovedCount, category.ChangedCount, category.UnchangedCount));
        }

        _categoryTitles = categoryTitles;
        rows.Insert(0, new CompareCategoryRow(Loc("Compare_AllResources"),
            allEntries, totalAdded, totalRemoved, totalChanged, totalUnchanged));

        _selectedEntries.Clear();
        Categories = new ObservableCollection<CompareCategoryRow>(rows);
        SelectedCategory = Categories[0];
        UpdateCounts();
    }

    partial void OnSelectedCategoryChanged(CompareCategoryRow? value)
    {
        RebuildEntries();
    }

    partial void OnFilterTextChanged(string value)
    {
        RebuildEntries();
    }

    partial void OnStatusFilterIndexChanged(int value)
    {
        RebuildEntries();
    }

    partial void OnDeleteRemovedChanged(bool value)
    {
        UpdateCounts();
    }

    private void RebuildEntries()
    {
        AssetDiffEntry? previous = SelectedEntry?.Entry;

        IEnumerable<AssetDiffEntry> source = SelectedCategory?.Entries ?? Enumerable.Empty<AssetDiffEntry>();
        source = StatusFilterIndex switch
        {
            1 => source,
            2 => source.Where(e => e.Change == AssetChangeKind.Added),
            3 => source.Where(e => e.Change == AssetChangeKind.Removed),
            4 => source.Where(e => e.Change == AssetChangeKind.Changed),
            _ => source.Where(e => e.Change != AssetChangeKind.Unchanged),
        };

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            string filter = FilterText.Trim();
            source = source.Where(e => e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        Entries = new ObservableCollection<CompareEntryRow>(
            source.Select(e => new CompareEntryRow(e,
                _categoryTitles.GetValueOrDefault(e.TypeId, e.TypeId),
                _selectedEntries.Contains(e),
                OnRowSelectionChanged)));

        SelectedEntry = Entries.FirstOrDefault(r => ReferenceEquals(r.Entry, previous))
                        ?? Entries.FirstOrDefault();
        UpdateCounts();
    }

    private void OnRowSelectionChanged(CompareEntryRow row)
    {
        if (row.IsSelected)
            _selectedEntries.Add(row.Entry);
        else
            _selectedEntries.Remove(row.Entry);

        UpdateCounts();
    }

    private void UpdateCounts()
    {
        EntriesCountText = string.Format(Loc("Compare_ResourcesCount"), Entries.Count);
        SelectedCountText = string.Format(Loc("Compare_SelectedCount"), _selectedEntries.Count);
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (CompareEntryRow row in Entries)
            row.IsSelected = true;
        UpdateCounts();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (CompareEntryRow row in Entries)
            row.IsSelected = false;

        _selectedEntries.Clear();
        UpdateCounts();
    }

    [RelayCommand]
    private async Task ExportStructuredDiffAsync()
    {
        if (_result is null || _baseData is null || _compareData is null)
            return;

        List<AssetDiffEntry> entries = GetSelectedDifferencesOrAll();
        if (entries.Count == 0)
        {
            await _view.MessageDialog(Loc("Compare_NothingToExport"));
            return;
        }

        IStorageFile? file = await _view.SaveFileDialog(new FilePickerSaveOptions
        {
            Title = Loc("Compare_ExportDialogTitle"),
            SuggestedFileName = "datafile-diff.json",
            FileTypeChoices = FilePickerFileTypes.JSON,
            DefaultExtension = ".json",
        });
        if (file is null)
            return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusText = Loc("Compare_Exporting");
        try
        {
            string json = await Task.Run(() => DiffReportJson.Serialize(
                _result, _baseData, _compareData, BaseLabel, CompareLabel, entries));

            await using Stream stream = await file.OpenWriteAsync();
            await using StreamWriter writer = new(stream, new UTF8Encoding(false));
            await writer.WriteAsync(json);

            StatusText = Loc("Compare_ExportDone");
        }
        catch (Exception e)
        {
            StatusText = string.Format(Loc("Compare_ExportFailed"), e.Message);
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            DisposeIfIdle();
        }
    }

    [RelayCommand]
    private async Task SavePatchAsync()
    {
        if (_result is null || _baseData is null || _compareData is null)
            return;

        IStorageFile? file = await _view.SaveFileDialog(new FilePickerSaveOptions
        {
            Title = Loc("Compare_SavePatchDialogTitle"),
            SuggestedFileName = Path.GetFileNameWithoutExtension(_compareFile.Name) + "." + DataFilePatch.FileExtension,
            FileTypeChoices = [FilePickerFileTypes.Patch],
            DefaultExtension = DataFilePatch.FileExtension,
        });
        if (file is null)
            return;

        string? patchPath = file.TryGetLocalPath();
        if (patchPath is null)
        {
            await _view.MessageDialog(string.Format(Loc("Compare_PatchSaveFailed"), file.Name));
            return;
        }

        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusText = Loc("Compare_SavingPatch");
        try
        {
            string? comparePath = _compareFile.TryGetLocalPath();
            string baseLabel = BaseLabel;
            string compareLabel = CompareLabel;
            DataFileDiffResult result = _result;
            await Task.Run(() =>
            {
                DataFilePatchBuilder builder = new(_baseData, _compareData, comparePath, null,
                    action => Dispatcher.UIThread.Invoke(action));
                builder.Build(result, patchPath, baseLabel, compareLabel);
            });

            StatusText = string.Format(Loc("Compare_PatchSaved"),
                result.AddedCount, result.RemovedCount, result.ChangedCount);
        }
        catch (Exception e)
        {
            StatusText = string.Format(Loc("Compare_PatchSaveFailed"), e.Message);
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            DisposeIfIdle();
        }
    }

    [RelayCommand]
    private async Task MergeSelectedAsync()
    {
        if (_result is null || _baseData is null || _compareData is null)
            return;

        List<AssetDiffEntry> selected = _result.Categories
            .SelectMany(c => c.Entries)
            .Where(e => _selectedEntries.Contains(e) && e.Change != AssetChangeKind.Unchanged)
            .ToList();

        if (selected.Count == 0)
        {
            await _view.MessageDialog(Loc("Compare_MergeNothing"));
            return;
        }

        List<AssetDiffEntry> mergeable = selected
            .Where(e => e.Change is AssetChangeKind.Added or AssetChangeKind.Changed && DataFileMerger.CanMerge(e))
            .ToList();
        List<AssetDiffEntry> removed = DeleteRemoved
            ? selected.Where(e => e.Change == AssetChangeKind.Removed && DataFileMerger.CanMerge(e)).ToList()
            : [];

        if (mergeable.Count == 0 && removed.Count == 0)
        {
            await _view.MessageDialog(Loc("Compare_MergeNothing"));
            return;
        }

        int unsupported = selected.Count(e => e.Change is AssetChangeKind.Added or AssetChangeKind.Changed &&
                                               !DataFileMerger.CanMerge(e));

        string message = string.Format(Loc("Compare_MergeConfirm"), mergeable.Count + removed.Count);
        if (removed.Count > 0)
            message += Environment.NewLine + Environment.NewLine + Loc("Compare_DeleteRemovedWarning");
        if (unsupported > 0)
            message += Environment.NewLine + Environment.NewLine + string.Format(Loc("Compare_MergeUnsupported"), unsupported);

        MessageWindow.Result confirmation = await _view.MessageDialog(message,
            title: Loc("Compare_MergeConfirmTitle"), buttons: MessageWindow.Buttons.YesNo);
        if (confirmation != MessageWindow.Result.Yes)
            return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusText = Loc("Compare_Merging");
        try
        {
            DataFileMergeResult mergeResult = await Task.Run(() => DataFileMerger.Merge(
                _baseData, _compareData, mergeable.Concat(removed),
                new DataFileMergeOptions { DeleteRemoved = DeleteRemoved },
                action => Dispatcher.UIThread.Invoke(action)));

            string summary = string.Format(Loc("Compare_MergeResult"),
                mergeResult.Import.ImportedCount,
                mergeResult.Import.OverwrittenCount,
                mergeResult.DeletedCount,
                mergeResult.WarningCount,
                mergeResult.ErrorCount);

            List<string> details = [];
            if (mergeResult.Unsupported.Count > 0)
                details.Add(string.Format(Loc("Compare_MergeUnsupported"), mergeResult.Unsupported.Count));
            details.AddRange(mergeResult.Import.Errors);
            details.AddRange(mergeResult.DeleteErrors);
            if (details.Count > 0)
                summary += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, details);

            await _view.MessageDialog(summary, title: Loc("Compare_MergeConfirmTitle"));
            StatusText = summary.Split(Environment.NewLine)[0];

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _codeDiffService = new CodeDiffService(_baseData, _compareData);
            await RunComparisonAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = Loc("Compare_Cancelled");
        }
        catch (Exception e)
        {
            StatusText = string.Format(Loc("Compare_MergeFailed"), e.Message);
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            DisposeIfIdle();
        }
    }

    private List<AssetDiffEntry> GetSelectedDifferencesOrAll()
    {
        if (_result is null)
            return [];

        IEnumerable<AssetDiffEntry> source = _result.Categories.SelectMany(c => c.Entries);
        if (_selectedEntries.Count > 0)
            source = source.Where(e => _selectedEntries.Contains(e));

        return source.Where(e => e.Change != AssetChangeKind.Unchanged).ToList();
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
                ? Loc("Compare_OnlyInCompare")
                : Loc("Compare_OnlyInBase");
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
                    ? string.Format(Loc("Compare_Truncated"), 5000)
                    : PropertyDiffs.Count == 0 ? Loc("Compare_NoPropertyDifferences") : "";
                IsPropertyDetails = true;
                DetailsTabIndex = 0;
            }
            catch (Exception e)
            {
                if (version == _detailsVersion)
                    DetailsNote = string.Format(Loc("Compare_PropertyComparisonFailed"), e.Message);
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

            // Anonymous sub-entries have no GML source of their own; compare their
            // bytecode segments via disassembly instead.
            bool subEntry = baseCode.ParentEntry is not null || compareCode.ParentEntry is not null;
            CodeDiffResult diff = await Task.Run(() => subEntry
                ? service.CompareSegments(baseCode, compareCode)
                : service.Compare(baseCode, compareCode));
            if (version != _detailsVersion)
                return;

            IsCodeDetails = true;
            DetailsTabIndex = 1;
            CodeDiffText = diff.DiffText;

            List<string> notes = [];
            if (subEntry)
                notes.Add(Loc("Compare_SubCodeDisassembly"));
            if (diff.BaseDecompileFailed || diff.CompareDecompileFailed)
                notes.Add(Loc("Compare_DecompileFailed"));
            if (!diff.HasTextDifferences)
                notes.Add(Loc("Compare_NoTextDifference"));
            if (diff.MetadataDifferences.Count > 0)
            {
                PropertyDiffs = new ObservableCollection<ComparePropertyRow>(
                    diff.MetadataDifferences.Select(d => new ComparePropertyRow(d.Path, d.BaseValue, d.CompareValue)));
                notes.Add(Loc("Compare_CodeMetadataInProperties"));
            }
            DetailsNote = string.Join(" ", notes);
        }
        catch (Exception e)
        {
            if (version == _detailsVersion)
                DetailsNote = string.Format(Loc("Compare_CodeComparisonFailed"), e.Message);
        }
        finally
        {
            IsDetailsBusy = false;
            DisposeIfIdle();
        }
    }

    private void ClearDetails()
    {
        DetailsTitle = Loc("Compare_DetailsHint");
        DetailsNote = "";
        DetailsTabIndex = 0;
        IsCodeDetails = false;
        IsPropertyDetails = false;
        PropertyDiffs = [];
        CodeDiffText = "";
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        IReadOnlyList<IStorageFile> files = await _view.OpenFileDialog(new FilePickerOpenOptions
        {
            Title = Loc("Compare_SelectFile"),
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
        _selectedEntries.Clear();
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
            parts.Add(LocalizationSource.GetString("Compare_NoDifferences"));
        CountsText = string.Join(" ", parts);
    }
}

/// <summary>One resource row in the comparison window.</summary>
public sealed partial class CompareEntryRow : ObservableObject
{
    public AssetDiffEntry Entry { get; }
    public string Name => Entry.Name;
    public string TypeTitle { get; }
    public bool CanMerge => DataFileMerger.CanMerge(Entry);
    public Action<CompareEntryRow>? SelectionChanged { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public string StatusText => Entry.Change switch
    {
        AssetChangeKind.Added => LocalizationSource.GetString("Compare_StatusAdded"),
        AssetChangeKind.Removed => LocalizationSource.GetString("Compare_StatusRemoved"),
        AssetChangeKind.Changed => LocalizationSource.GetString("Compare_StatusChanged"),
        _ => LocalizationSource.GetString("Compare_StatusUnchanged"),
    };

    /// <summary>Row foreground color: green for added, red for removed, yellow for changed.</summary>
    public Avalonia.Media.IImmutableSolidColorBrush StatusBrush => Entry.Change switch
    {
        AssetChangeKind.Added => Avalonia.Media.Brushes.LightGreen,
        AssetChangeKind.Removed => Avalonia.Media.Brushes.IndianRed,
        AssetChangeKind.Changed => Avalonia.Media.Brushes.Khaki,
        _ => Avalonia.Media.Brushes.Transparent,
    };

    public CompareEntryRow(AssetDiffEntry entry, string typeTitle, bool isSelected,
                           Action<CompareEntryRow>? selectionChanged)
    {
        Entry = entry;
        TypeTitle = typeTitle;
        IsSelected = isSelected;
        SelectionChanged = selectionChanged;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        SelectionChanged?.Invoke(this);
    }
}

/// <summary>One property row in the details grid.</summary>
public sealed record ComparePropertyRow(string Path, string BaseValue, string CompareValue);
