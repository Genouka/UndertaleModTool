using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using UndertaleModLib;
using UndertaleModLib.Diff;
using UndertaleModLib.Models;
using UndertaleModTool.Localization;

namespace UndertaleModTool
{
    /// <summary>
    /// Compares the currently open data file against another data file selected by the user,
    /// and can apply selected changes back to the open data.
    /// The WPF counterpart of the Avalonia <c>DataFileCompareViewModel</c>.
    /// </summary>
    [PropertyChanged.AddINotifyPropertyChangedInterface]
    public class DataFileCompareViewModel : IDisposable
    {
        private readonly HashSet<AssetDiffEntry> _selectedEntries = new(ReferenceEqualityComparer.Instance);
        private string _compareFilePath;

        private CancellationTokenSource _cts;
        private UndertaleData _baseData;
        private UndertaleData _compareData;
        private DataFileDiffResult _result;
        private CodeDiffService _codeDiffService;
        private Dictionary<string, string> _categoryTitles = new(StringComparer.Ordinal);
        private int _detailsVersion;
        private bool _disposed;
        private bool _disposeWhenIdle;

        public MainWindow Main { get; }

        public string BaseLabel { get; set; } = "";
        public string CompareLabel { get; set; } = "";
        public bool IsBusy { get; set; }
        public bool IsProgressIndeterminate { get; set; }
        public double ProgressPercent { get; set; }
        public string StatusText { get; set; } = "";
        public string WarningsText { get; set; } = "";
        public bool HasWarnings { get; set; }

        public ObservableCollection<CompareCategoryRow> Categories { get; set; } = new();
        public CompareCategoryRow SelectedCategory { get; set; }
        public ObservableCollection<CompareEntryRow> Entries { get; set; } = new();
        public CompareEntryRow SelectedEntry { get; set; }
        public string FilterText { get; set; } = "";

        /// <summary>0 = differences, 1 = all, 2 = added, 3 = removed, 4 = changed.</summary>
        public int StatusFilterIndex { get; set; }
        public string EntriesCountText { get; set; } = "";
        public string SelectedCountText { get; set; } = "";
        public bool DeleteRemoved { get; set; }

        public string DetailsTitle { get; set; } = "";
        public int DetailsTabIndex { get; set; }
        public string DetailsNote { get; set; } = "";
        public bool IsDetailsBusy { get; set; }
        public bool IsCodeDetails { get; set; }
        public bool IsPropertyDetails { get; set; }
        public ObservableCollection<ComparePropertyRow> PropertyDiffs { get; set; } = new();
        public string CodeDiffText { get; set; } = "";

        /// <summary>Set to false while a long operation runs, so the view can disable commands.</summary>
        public bool IsNotBusy => !IsBusy;

        public DataFileCompareViewModel(MainWindow mainWindow, string compareFilePath)
        {
            Main = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
            _compareFilePath = compareFilePath;
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
            if (_disposed || Main.Data is null)
                return;

            _baseData = Main.Data;
            BaseLabel = string.Format(Loc("Compare_Base"), Main.FilePath ?? Loc("Compare_BaseCurrent"));
            CompareLabel = string.Format(Loc("Compare_Compare"), Path.GetFileName(_compareFilePath));

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            CancellationToken cancellationToken = _cts.Token;

            _detailsVersion++;
            _codeDiffService = null;
            DisposeCompareData();
            _result = null;
            _selectedEntries.Clear();
            Categories = new ObservableCollection<CompareCategoryRow>();
            Entries = new ObservableCollection<CompareEntryRow>();
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
            StatusText = string.Format(Loc("Compare_Loading"), Path.GetFileName(_compareFilePath));

            try
            {
                List<string> warnings = [];
                UndertaleData compareData;
                using (FileStream stream = new(_compareFilePath, FileMode.Open, FileAccess.Read))
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

            IProgress<DataFileDiffProgress> progress = new Progress<DataFileDiffProgress>(p =>
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

        public void OnSelectedCategoryChanged()
        {
            RebuildEntries();
        }

        public void OnFilterTextChanged()
        {
            RebuildEntries();
        }

        public void OnStatusFilterIndexChanged()
        {
            RebuildEntries();
        }

        public void OnDeleteRemovedChanged()
        {
            UpdateCounts();
        }

        private void RebuildEntries()
        {
            AssetDiffEntry previous = SelectedEntry?.Entry;

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
                    HandleRowSelectionChanged)));

            SelectedEntry = Entries.FirstOrDefault(r => ReferenceEquals(r.Entry, previous))
                            ?? Entries.FirstOrDefault();
            UpdateCounts();
        }

        private void HandleRowSelectionChanged(CompareEntryRow row)
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

        public void SelectAll()
        {
            foreach (CompareEntryRow row in Entries)
                row.IsSelected = true;
            UpdateCounts();
        }

        public void ClearSelection()
        {
            foreach (CompareEntryRow row in Entries)
                row.IsSelected = false;

            _selectedEntries.Clear();
            UpdateCounts();
        }

        public async Task ExportStructuredDiffAsync(Window owner)
        {
            if (_result is null || _baseData is null || _compareData is null)
                return;

            List<AssetDiffEntry> entries = GetSelectedDifferencesOrAll();
            if (entries.Count == 0)
            {
                owner.ShowMessage(Loc("Compare_NothingToExport"));
                return;
            }

            SaveFileDialog dialog = new()
            {
                Title = Loc("Compare_ExportDialogTitle"),
                FileName = "datafile-diff.json",
                DefaultExt = "json",
                Filter = LocalizationSource.GetString("Filter_ProjectFiles") + "|*.json|" +
                         LocalizationSource.GetString("Filter_AllFiles") + "|*"
            };
            if (dialog.ShowDialog(owner) != true)
                return;

            IsBusy = true;
            IsProgressIndeterminate = true;
            StatusText = Loc("Compare_Exporting");
            try
            {
                string json = await Task.Run(() => DiffReportJson.Serialize(
                    _result, _baseData, _compareData, BaseLabel, CompareLabel, entries));

                await using (FileStream stream = new(dialog.FileName, FileMode.Create, FileAccess.Write))
                await using (StreamWriter writer = new(stream, new UTF8Encoding(false)))
                {
                    await writer.WriteAsync(json);
                }

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

        /// <summary>
        /// Writes the complete diff as a data file patch archive. Unlike the JSON report, a
        /// patch is applicable: the base file plus this patch reproduces the compared file.
        /// </summary>
        public async Task SavePatchAsync(Window owner)
        {
            if (_result is null || _baseData is null || _compareData is null)
                return;

            SaveFileDialog dialog = new()
            {
                Title = Loc("Compare_SavePatchDialogTitle"),
                FileName = Path.GetFileNameWithoutExtension(_compareFilePath) + ".utmt-patch",
                DefaultExt = DataFilePatch.FileExtension,
                Filter = LocalizationSource.GetString("Filter_PatchFiles") + "|*." + DataFilePatch.FileExtension + "|" +
                         LocalizationSource.GetString("Filter_AllFiles") + "|*"
            };
            if (dialog.ShowDialog(owner) != true)
                return;

            IsBusy = true;
            IsProgressIndeterminate = true;
            StatusText = Loc("Compare_SavingPatch");
            try
            {
                string patchPath = dialog.FileName;
                DataFilePatchBuilder builder = new(_baseData, _compareData, _compareFilePath, Main.MainThreadAction);
                await Task.Run(() => builder.Build(_result, patchPath, BaseLabel, CompareLabel));

                StatusText = string.Format(Loc("Compare_PatchSaved"),
                    _result.AddedCount, _result.RemovedCount, _result.ChangedCount);
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

        public async Task MergeSelectedAsync(Window owner)
        {
            if (_result is null || _baseData is null || _compareData is null)
                return;

            List<AssetDiffEntry> selected = _result.Categories
                .SelectMany(c => c.Entries)
                .Where(e => _selectedEntries.Contains(e) && e.Change != AssetChangeKind.Unchanged)
                .ToList();

            if (selected.Count == 0)
            {
                owner.ShowMessage(Loc("Compare_MergeNothing"));
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
                owner.ShowMessage(Loc("Compare_MergeNothing"));
                return;
            }

            int unsupported = selected.Count(e => e.Change is AssetChangeKind.Added or AssetChangeKind.Changed &&
                                                  !DataFileMerger.CanMerge(e));

            string message = string.Format(Loc("Compare_MergeConfirm"), mergeable.Count + removed.Count);
            if (removed.Count > 0)
                message += Environment.NewLine + Environment.NewLine + Loc("Compare_DeleteRemovedWarning");
            if (unsupported > 0)
                message += Environment.NewLine + Environment.NewLine + string.Format(Loc("Compare_MergeUnsupported"), unsupported);

            if (owner.ShowQuestion(message, MessageBoxImage.Question, Loc("Compare_MergeConfirmTitle")) != MessageBoxResult.Yes)
                return;

            IsBusy = true;
            IsProgressIndeterminate = true;
            StatusText = Loc("Compare_Merging");
            try
            {
                DataFileMergeResult mergeResult = await Task.Run(() => DataFileMerger.Merge(
                    _baseData, _compareData, mergeable.Concat(removed),
                    new DataFileMergeOptions { DeleteRemoved = DeleteRemoved },
                    Main.MainThreadAction));

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

                owner.ShowMessage(summary, Loc("Compare_MergeConfirmTitle"));
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

        public void OnSelectedEntryChanged()
        {
            _ = UpdateDetailsAsync(SelectedEntry);
        }

        private async Task UpdateDetailsAsync(CompareEntryRow row)
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
                CodeDiffService service = _codeDiffService;
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
            PropertyDiffs = new ObservableCollection<ComparePropertyRow>();
            CodeDiffText = "";
        }

        public async Task BrowseAsync(Window owner)
        {
            OpenFileDialog dialog = new()
            {
                Title = Loc("Compare_SelectFile"),
                Filter = LocalizationSource.GetString("Filter_DataFiles") + "|*.win;*.unx;*.ios;*.droid;audiogroup*.dat|" +
                         LocalizationSource.GetString("Filter_AllFiles") + "|*"
            };
            if (dialog.ShowDialog(owner) != true)
                return;

            _compareFilePath = dialog.FileName;
            await StartAsync();
        }

        public void Cancel()
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
    public sealed class CompareEntryRow : INotifyPropertyChanged
    {
        private bool _isSelected;

        public AssetDiffEntry Entry { get; }
        public string Name => Entry.Name;
        public string TypeTitle { get; }
        public bool CanMerge => DataFileMerger.CanMerge(Entry);
        public Action<CompareEntryRow> SelectionChanged { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                    SelectionChanged?.Invoke(this);
                }
            }
        }

        public string StatusText => Entry.Change switch
        {
            AssetChangeKind.Added => LocalizationSource.GetString("Compare_StatusAdded"),
            AssetChangeKind.Removed => LocalizationSource.GetString("Compare_StatusRemoved"),
            AssetChangeKind.Changed => LocalizationSource.GetString("Compare_StatusChanged"),
            _ => LocalizationSource.GetString("Compare_StatusUnchanged"),
        };

        public event PropertyChangedEventHandler PropertyChanged;

        public CompareEntryRow(AssetDiffEntry entry, string typeTitle, bool isSelected,
                               Action<CompareEntryRow> selectionChanged)
        {
            Entry = entry;
            TypeTitle = typeTitle;
            _isSelected = isSelected;
            SelectionChanged = selectionChanged;
        }
    }

    /// <summary>One property row in the details grid.</summary>
    public sealed record ComparePropertyRow(string Path, string BaseValue, string CompareValue);
}
