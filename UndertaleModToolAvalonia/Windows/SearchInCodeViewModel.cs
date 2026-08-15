using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using UndertaleModLib;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

public partial class SearchInCodeViewModel : ObservableObject
{
    // Set this when testing.
    public IView? View;

    public MainViewModel MainVM { get; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsCaseSensitive { get; set; } = false;

    [ObservableProperty]
    public partial bool IsRegexSearch { get; set; } = false;

    [ObservableProperty]
    public partial bool IsInAssembly { get; set; } = false;

    [ObservableProperty]
    public partial ObservableCollection<SearchResult> Results { get; set; } = [];

    [ObservableProperty]
    public partial string StatusBarText { get; set; } = "";

    string searchText = null!;
    Regex searchTextRegex = null!;

    GlobalDecompileContext? globalDecompileContext;

    ConcurrentDictionary<UndertaleCode, List<(int, int, string)>> resultsByCodeDict = new();
    int resultCount = 0;
    int failedCount = 0;

    ILoaderWindow? loaderWindow;
    int currentCodeEntriesCount = 0;
    bool postToLoader = true;

    bool resultsAreInAssembly = false;

    public SearchInCodeViewModel(IServiceProvider serviceProvider)
    {
        MainVM = serviceProvider.GetRequiredService<MainViewModel>();
    }

    public async void Search()
    {
        if (MainVM.Data is null)
        {
            StatusBarText = LocalizationSource.GetString("Search_ErrorNoData");
            return;
        }

        if (MainVM.Data.IsYYC())
        {
            StatusBarText = LocalizationSource.GetString("Search_ErrorYYC");
            return;
        }

        searchText = SearchText.Replace("\r\n", "\n");

        if (String.IsNullOrEmpty(searchText))
        {
            StatusBarText = LocalizationSource.GetString("Search_ErrorNoText");
            return;
        }

        if (IsRegexSearch)
        {
            try
            {
                searchTextRegex = new(searchText, IsCaseSensitive ? RegexOptions.Compiled : RegexOptions.Compiled | RegexOptions.IgnoreCase);
            }
            catch (ArgumentException e)
            {
                StatusBarText = string.Format(LocalizationSource.GetString("Search_ErrorInvalidRegex"), e.Message);
                return;
            }
        }

        // Set up loader window
        loaderWindow = View!.LoaderOpen();
        loaderWindow.SetMaximum(MainVM.Data.Code.Count);
        loaderWindow.SetValue(0);
        loaderWindow.SetMessage(LocalizationSource.GetString("Search_Searching"));
        loaderWindow.EnsureShown();

        IsEnabled = false;
        MainVM.IsEnabled = false;

        // Search codes in parallel
        globalDecompileContext = new(MainVM.Data);

        await Task.Run(() => Parallel.ForEach(MainVM.Data.Code, SearchInUndertaleCode));

        // Sort results
        loaderWindow.SetText(LocalizationSource.GetString("Search_Sorting"));

        List<SearchResult> sortedResultsList = new(resultCount);

        await Task.Run(() =>
        {
            var sortedResultsByCodeDict = resultsByCodeDict.OrderBy(entry => MainVM.Data.Code.IndexOf(entry.Key));

            foreach (var result in sortedResultsByCodeDict)
            {
                UndertaleCode code = result.Key;
                foreach (var (lineNumber, columnNumber, lineText) in result.Value)
                {
                    sortedResultsList.Add(new(code, lineNumber, columnNumber, lineText));
                }
            }
        });

        resultsAreInAssembly = IsInAssembly;

        Results = [.. sortedResultsList];

        // Set status bar text
        string str = string.Format(LocalizationSource.GetString("Search_FoundResults"),
            resultCount, resultCount != 1 ? "s" : "", resultsByCodeDict.Count, resultsByCodeDict.Count != 1 ? "ies" : "y");
        if (failedCount > 0)
        {
            str += string.Format(LocalizationSource.GetString("Search_FailedResults"), failedCount, failedCount != 1 ? "ies" : "y");
        }
        StatusBarText = str;

        // Reset variables
        resultsByCodeDict = new();
        resultCount = 0;
        failedCount = 0;
        currentCodeEntriesCount = 0;
        postToLoader = true;

        // Close loader window
        loaderWindow.Close();

        IsEnabled = true;
        MainVM.IsEnabled = true;
    }

    void SearchInUndertaleCode(UndertaleCode code)
    {
        if (postToLoader)
        {
            postToLoader = false;
            Dispatcher.UIThread.Post(() =>
            {
                loaderWindow!.SetValue(currentCodeEntriesCount);
                postToLoader = true;
            }, DispatcherPriority.Background);
        }

        if (code is not null && code.ParentEntry is null)
        {
            string codeText = String.Empty;

            if (!IsInAssembly)
            {
                if (MainVM.Project is null || !MainVM.Project.TryGetCodeSource(code, out codeText))
                {
                    try
                    {
                        codeText = new Underanalyzer.Decompiler.DecompileContext(globalDecompileContext!, code, MainVM.Data!.ToolInfo.DecompilerSettings).DecompileToString();
                    }
                    catch (Underanalyzer.Decompiler.DecompilerException)
                    {
                        Interlocked.Increment(ref failedCount);
                        return;
                    }
                }
            }
            else
            {
                try
                {
                    codeText = code.Disassemble(MainVM.Data!.Variables, MainVM.Data!.CodeLocals?.For(code));
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref failedCount);
                    return;
                }
            }

            List<int> results = [];

            if (IsRegexSearch)
            {
                MatchCollection matches = searchTextRegex.Matches(codeText);
                foreach (Match match in matches)
                {
                    results.Add(match.Index);
                }
            }
            else
            {
                StringComparison comparisonType = IsCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

                int index = 0;
                while ((index = codeText.IndexOf(searchText, index, comparisonType)) != -1)
                {
                    results.Add(index);
                    index += searchText.Length;
                }
            }

            bool nameWritten = false;

            int lineNumber = 0;
            int lineStartIndex = 0;

            foreach (int index in results)
            {
                // Continue from previous line count since results are in order
                for (int i = lineStartIndex; i < index; ++i)
                {
                    if (codeText[i] == '\n')
                    {
                        lineNumber++;
                        lineStartIndex = i + 1;
                    }
                }

                int columnNumber = index - lineStartIndex;

                // Start at match.Index so it's only one line in case the search was multiline
                int lineEndIndex = codeText.IndexOf('\n', index);
                lineEndIndex = lineEndIndex == -1 ? codeText.Length : lineEndIndex;

                string lineText = codeText[lineStartIndex..lineEndIndex];

                if (nameWritten == false)
                {
                    resultsByCodeDict[code] = [];
                    nameWritten = true;
                }
                resultsByCodeDict[code].Add((lineNumber + 1, columnNumber + 1, lineText));

                Interlocked.Increment(ref resultCount);
            }
        }

        Interlocked.Increment(ref currentCodeEntriesCount);
    }

    public async void OpenSearchResult(SearchResult searchResult, bool inNewTab = false)
    {
        var tab = await MainVM.TabOpen(searchResult.Code, inNewTab);
        if (tab is not null && tab.Content is UndertaleCodeViewModel vm)
        {
            vm.GoToLocation(!resultsAreInAssembly ? UndertaleCodeViewModel.Tab.GML : UndertaleCodeViewModel.Tab.ASM, searchResult.LineNumber, searchResult.ColumnNumber);
        }
    }

    public class SearchResult
    {
        public string Location { get; set; }
        public string Text { get; set; }

        public UndertaleCode Code;
        public int LineNumber;
        public int ColumnNumber;

        public SearchResult(UndertaleCode code, int lineNumber, int columnNumber, string text)
        {
            Code = code;
            LineNumber = lineNumber;
            ColumnNumber = columnNumber;

            Location = (code.Name?.Content ?? LocalizationSource.GetString("Search_NullName")) + ":" + lineNumber + "," + columnNumber;
            Text = text.Trim();
        }
    }
}
