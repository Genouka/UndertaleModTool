#pragma warning disable CA1416 // Validate platform compatibility

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using UndertaleModLib;
using UndertaleModLib.Compiler;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using UndertaleModTool.Localization;

namespace UndertaleModTool.Windows
{
    /// <summary>
    /// Interaction logic for SearchInCodeWindow.xaml
    /// </summary>
    public partial class SearchInCodeWindow : Window
    {
        private static readonly MainWindow mainWindow = Application.Current.MainWindow as MainWindow;

        private static bool isSearchInProgress = false;

        private bool isCaseSensitive, isRegexSearch, isMultilineRegex, isInAssembly, isWholeWord;
        private string text;

        private int progressCount = 0;
        private int resultCount = 0;

        private ConcurrentDictionary<string, List<(int LineNumber, string LineText, int MatchIndex, int MatchLength)>> resultsDict;
        private ConcurrentBag<string> failedList;
        private IEnumerable<KeyValuePair<string, List<(int LineNumber, string LineText, int MatchIndex, int MatchLength)>>> resultsDictSorted;
        private IEnumerable<string> failedListSorted;

        private Regex keywordRegex, nameRegex;
        private GlobalDecompileContext decompileContext;
        private LoaderDialog loaderDialog;
        private UndertaleCodeEditor.CodeEditorTab editorTab;

        public readonly record struct Result(string Code, int LineNumber, string LineText, int MatchIndex, int MatchLength);

        public ObservableCollection<Result> Results { get; set; } = new();

        public SearchInCodeWindow(string query = null, bool inAssembly = false)
        {
            InitializeComponent();

            Results.CollectionChanged += Results_CollectionChanged;

            if (query is not null)
            {
                if (query.Length > 256 || query.Count(x => x == '\n') > 16)
                    return;

                SearchTextBox.Text = query;
                SearchTextBox.SelectAll();
            }

            InAssemblyCheckBox.IsChecked = inAssembly;
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            await Search();
        }

        private async Task Search()
        {
            // TODO: Allow this be cancelled, probably make loader inside this window itself.

            if (mainWindow.Data == null)
            {
                this.ShowError(LocalizationSource.GetString("Msg_NoDataWinLoaded"));
                return;
            }

            if (mainWindow.Data.IsYYC())
            {
                this.ShowError(LocalizationSource.GetString("Msg_CantSearchYYC"));
                return;
            }

            text = SearchTextBox.Text.Replace("\r\n", "\n");

            if (String.IsNullOrEmpty(text))
                return;

            if (isSearchInProgress)
            {
                this.ShowError(LocalizationSource.GetString("Msg_CantSearchWhileInProgress"));
                return;
            }

            isCaseSensitive = CaseSensitiveCheckBox.IsChecked ?? false;
            isRegexSearch = RegexSearchCheckBox.IsChecked ?? false;
            isMultilineRegex = MultilineRegexCheckBox.IsChecked ?? false;
            isInAssembly = InAssemblyCheckBox.IsChecked ?? false;
            isWholeWord = WholeWordCheckBox.IsChecked ?? false;

            bool filterByName = FilterByNameExpander.IsExpanded;
            bool nameIsCaseSensitive, nameIsRegex;
            string name;

            IList<UndertaleCode> codeEntriesToSearch = mainWindow.Data.Code;

            if (isRegexSearch)
            {
                try
                {
                    RegexOptions options = RegexOptions.Compiled;
                    if (!isCaseSensitive)
                    {
                        options |= RegexOptions.IgnoreCase;
                    }
                    if (isMultilineRegex)
                    {
                        options |= RegexOptions.Multiline;
                    }
                    keywordRegex = new(text, options);
                }
                catch (ArgumentException e)
                {
                    this.ShowError(string.Format(LocalizationSource.GetString("Msg_InvalidRegex"), e.Message));
                    return;
                }
            }

            if (filterByName)
            {
                name = NameFilterTextBox.Text;
                if (!String.IsNullOrEmpty(name))
                {
                    nameIsCaseSensitive = NameCaseSensitiveCheckBox.IsChecked ?? false;
                    nameIsRegex = NameRegexSearchCheckBox.IsChecked ?? false;

                    if (nameIsRegex)
                    {
                        try
                        {
                            nameRegex = new(name, nameIsCaseSensitive ? RegexOptions.Compiled : RegexOptions.Compiled | RegexOptions.IgnoreCase);
                            codeEntriesToSearch = mainWindow.Data.Code.Where(c => !String.IsNullOrEmpty(c.Name.Content)
                                                                                  && nameRegex.IsMatch(c.Name.Content))
                                                                      .ToList();
                        }
                        catch (ArgumentException e)
                        {
                            this.ShowError(string.Format(LocalizationSource.GetString("Msg_InvalidNameRegex"), e.Message));
                            filterByName = false;
                        }
                    }
                    else
                    {
                        var comparison = nameIsCaseSensitive ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase;
                        codeEntriesToSearch = mainWindow.Data.Code.Where(c => !String.IsNullOrEmpty(c.Name.Content)
                                                                              && c.Name.Content.Contains(name, comparison))
                                                                  .ToList();
                    }
                }
            }

            if (codeEntriesToSearch.Count == 0)
            {
                this.ShowMessage(LocalizationSource.GetString("Msg_NoCodeEntriesMatchFilter"));
                return;
            }

            mainWindow.IsEnabled = false;
            this.IsEnabled = false;

            isSearchInProgress = true;

            loaderDialog = new(LocalizationSource.GetString("Dialog_Searching"), null);
            loaderDialog.Owner = this;
            loaderDialog.PreventClose = true;
            loaderDialog.Show();

            Results.Clear();

            resultsDict = new();
            failedList = new();
            resultsDictSorted = null;
            failedListSorted = null;
            progressCount = 0;
            resultCount = 0;

            if (!isInAssembly)
            {
                decompileContext = new GlobalDecompileContext(mainWindow.Data);
            }

            loaderDialog.SavedStatusText = LocalizationSource.GetString("Msg_CodeEntries");
            loaderDialog.Update(null, LocalizationSource.GetString("Msg_CodeEntries"), 0, codeEntriesToSearch.Count);

            await Task.Run(() => Parallel.ForEach(codeEntriesToSearch, SearchInUndertaleCode));
            await Task.Run(SortResults);

            loaderDialog.Maximum = null;
            loaderDialog.Update(LocalizationSource.GetString("Msg_GeneratingResultList"));

            editorTab = isInAssembly ? UndertaleCodeEditor.CodeEditorTab.Disassembly : UndertaleCodeEditor.CodeEditorTab.Decompiled;

            ShowResults();

            loaderDialog.PreventClose = false;
            loaderDialog.Close();
            loaderDialog = null;

            mainWindow.IsEnabled = true;
            this.IsEnabled = true;

            isSearchInProgress = false;
        }

        string GetCodeString(UndertaleCode code)
        {
            // First, try to retrieve source from project (if available)
            if (mainWindow.Project is null || !mainWindow.Project.TryGetCodeSource(code, out string decompiled))
            {
                // Source isn't available - perform decompile
                decompiled = new Underanalyzer.Decompiler.DecompileContext(decompileContext, code, mainWindow.Data.ToolInfo.DecompilerSettings).DecompileToString();
            }
            return decompiled;
        }

        private void SearchInUndertaleCode(UndertaleCode code)
        {
            try
            {
                if (code is not null && code.ParentEntry is null)
                {
                    var codeText = isInAssembly
                        ? code.Disassemble(mainWindow.Data.Variables, mainWindow.Data.CodeLocals?.For(code), mainWindow.Data.CodeLocals is null)
                        : GetCodeString(code);
                    SearchInCodeText(code.Name.Content, codeText);
                }
                
            }
            // TODO: Look at specific exceptions
            catch (Exception)
            {
                failedList.Add(code.Name.Content);
            }

            Interlocked.Increment(ref progressCount);
            Dispatcher.Invoke(() => loaderDialog.ReportProgress(progressCount));
        }

        private void SearchInCodeText(string codeName, string codeText)
        {
            List<(int Index, int Length)> results = new();

            if (isRegexSearch)
            {
                MatchCollection matches = keywordRegex.Matches(codeText);
                foreach (Match match in matches)
                {
                    if (isWholeWord && !IsWholeWordMatch(codeText, match.Index, match.Length))
                        continue;
                    results.Add((match.Index, match.Length));
                }
            }
            else
            {
                StringComparison comparisonType = isCaseSensitive ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase;

                int index = 0;
                while ((index = codeText.IndexOf(text, index, comparisonType)) != -1)
                {
                    if (isWholeWord && !IsWholeWordMatch(codeText, index, text.Length))
                    {
                        index += text.Length;
                        continue;
                    }
                    results.Add((index, text.Length));
                    index += text.Length;
                }
            }

            bool nameWritten = false;

            int lineNumber = 0;
            int lineStartIndex = 0;

            foreach (var (matchIndex, matchLength) in results)
            {
                for (int i = lineStartIndex; i < matchIndex; ++i)
                {
                    if (codeText[i] == '\n')
                    {
                        lineNumber++;
                        lineStartIndex = i + 1;
                    }
                }

                int lineEndIndex = codeText.IndexOf('\n', matchIndex);
                lineEndIndex = lineEndIndex == -1 ? codeText.Length : lineEndIndex;

                string lineText;

                if (lineEndIndex - lineStartIndex > 128)
                {
                    lineEndIndex = lineStartIndex + 128;
                    lineText = codeText[lineStartIndex..lineEndIndex] + "...";
                }
                else
                {
                    lineText = codeText[lineStartIndex..lineEndIndex];
                }

                if (nameWritten == false)
                {
                    resultsDict[codeName] = new List<(int, string, int, int)>();
                    nameWritten = true;
                }
                resultsDict[codeName].Add((lineNumber + 1, lineText, matchIndex, matchLength));

                Interlocked.Increment(ref resultCount);
            }
        }

        private void SortResults()
        {
            string[] codeNames = mainWindow.Data.Code.Select(x => x.Name.Content).ToArray();

            resultsDictSorted = resultsDict.OrderBy(c => Array.IndexOf(codeNames, c.Key));
            failedListSorted = failedList.OrderBy(c => Array.IndexOf(codeNames, c));
        }

        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        private static bool IsWholeWordMatch(string text, int index, int length)
        {
            if (index > 0 && IsWordChar(text[index - 1]))
                return false;
            if (index + length < text.Length && IsWordChar(text[index + length]))
                return false;
            return true;
        }

        private string BuildWholeWordRegexPattern(string searchText)
        {
            return @"\b" + Regex.Escape(searchText) + @"\b";
        }

        private async Task Replace(bool replaceAll)
        {
            if (mainWindow.Data == null)
            {
                this.ShowError(LocalizationSource.GetString("Msg_NoDataWinLoaded"));
                return;
            }

            if (mainWindow.Data.IsYYC())
            {
                this.ShowError(LocalizationSource.GetString("Msg_CantSearchYYC"));
                return;
            }

            if (isSearchInProgress)
            {
                this.ShowError(LocalizationSource.GetString("Msg_CantSearchWhileInProgress"));
                return;
            }

            if (isInAssembly)
            {
                this.ShowError(LocalizationSource.GetString("Msg_CantReplaceInAssembly"));
                return;
            }

            string searchText = SearchTextBox.Text.Replace("\r\n", "\n");
            string replacement = ReplaceTextBox.Text.Replace("\r\n", "\n");

            if (String.IsNullOrEmpty(searchText))
                return;

            bool caseSensitive = CaseSensitiveCheckBox.IsChecked ?? false;
            bool regexSearch = RegexSearchCheckBox.IsChecked ?? false;
            bool wholeWord = WholeWordCheckBox.IsChecked ?? false;

            if (!replaceAll && ResultsListView.SelectedItem is not Result)
            {
                this.ShowError(LocalizationSource.GetString("Msg_NoResultSelected"));
                return;
            }

            Result selectedResult = replaceAll ? default : (Result)ResultsListView.SelectedItem;

            string confirmMsg;
            if (replaceAll)
            {
                int codeEntryCount = resultsDictSorted?.Select(r => r.Key).Count()
                                    ?? Results.Select(r => r.Code).Distinct().Count();
                confirmMsg = string.Format(LocalizationSource.GetString("Msg_ConfirmReplaceAll"), codeEntryCount);
            }
            else
            {
                confirmMsg = string.Format(LocalizationSource.GetString("Msg_ConfirmReplace"), selectedResult.Code);
            }

            if (this.ShowQuestion(confirmMsg) != MessageBoxResult.Yes)
                return;

            mainWindow.IsEnabled = false;
            this.IsEnabled = false;

            isSearchInProgress = true;

            loaderDialog = new(LocalizationSource.GetString("Dialog_Replacing"), null);
            loaderDialog.Owner = this;
            loaderDialog.PreventClose = true;
            loaderDialog.Show();

            int replacedCount = 0;
            List<string> failedNames = new();

            await Task.Run(() =>
            {
                try
                {
                    CodeImportGroup importGroup = new(mainWindow.Data, null, mainWindow.Data.ToolInfo.DecompilerSettings)
                    {
                        MainThreadAction = mainWindow.MainThreadAction
                    };

                    if (replaceAll)
                    {
                        HashSet<string> codeNamesToReplace = resultsDictSorted?.Select(r => r.Key).ToHashSet()
                                                            ?? Results.Select(r => r.Code).ToHashSet();

                        foreach (string codeName in codeNamesToReplace)
                        {
                            UndertaleCode code = mainWindow.Data.Code.ByName(codeName);
                            if (code is null || code.ParentEntry is not null)
                                continue;

                            if (regexSearch || wholeWord)
                            {
                                string pattern = wholeWord ? BuildWholeWordRegexPattern(searchText) : searchText;
                                importGroup.QueueRegexFindReplace(code, pattern, replacement, caseSensitive);
                            }
                            else
                            {
                                importGroup.QueueFindReplace(code, searchText, replacement, caseSensitive);
                            }
                        }
                        replacedCount = codeNamesToReplace.Count;
                    }
                    else
                    {
                        UndertaleCode code = mainWindow.Data.Code.ByName(selectedResult.Code);
                        if (code is not null && code.ParentEntry is null)
                        {
                            string codeText = GetCodeString(code);

                            if (selectedResult.MatchIndex >= 0
                                && selectedResult.MatchIndex + selectedResult.MatchLength <= codeText.Length)
                            {
                                string modifiedCode = string.Concat(
                                    codeText.AsSpan(0, selectedResult.MatchIndex),
                                    replacement.AsSpan(),
                                    codeText.AsSpan(selectedResult.MatchIndex + selectedResult.MatchLength));
                                importGroup.QueueReplace(code, modifiedCode);
                                replacedCount = 1;
                            }
                        }
                    }

                    importGroup.Import();
                }
                catch (Exception ex)
                {
                    failedNames.Add(ex.Message);
                }
            });

            loaderDialog.PreventClose = false;
            loaderDialog.Close();
            loaderDialog = null;

            if (failedNames.Count > 0)
            {
                this.ShowError(string.Format(LocalizationSource.GetString("Msg_ReplaceError"), string.Join("\n", failedNames)));
            }
            else
            {
                string msg = string.Format(LocalizationSource.GetString("Msg_ReplaceComplete"), replacedCount);
                StatusBarTextBlock.Text = msg;
            }

            mainWindow.IsEnabled = true;
            this.IsEnabled = true;

            isSearchInProgress = false;

            await Search();
        }

        public void ShowResults()
        {
            static string GetWordEnding(int quantity, bool isResults)
            {
                if (isResults)
                    return quantity != 1 ? "s" : "";
                
                return quantity != 1 ? "ies" : "y";
            }

            var resultsSorted = resultsDictSorted.ToArray();
            var failedSorted = failedListSorted.ToArray();
            foreach (var result in resultsSorted)
            {
                var code = result.Key;
                foreach (var (lineNumber, lineText, matchIndex, matchLength) in result.Value)
                {
                    Results.Add(new(code, lineNumber, lineText, matchIndex, matchLength));
                }
            }

            string str = string.Format(LocalizationSource.GetString("Msg_SearchResultsSummary"), resultCount, GetWordEnding(resultCount, true), resultsSorted.Length, GetWordEnding(resultsSorted.Length, false));
            if (failedSorted.Length > 0)
            {
                str += " " + string.Format(LocalizationSource.GetString("Msg_SearchResultsErrorCount"), failedSorted.Length, GetWordEnding(failedSorted.Length, false));
            }
            StatusBarTextBlock.Text = str;

            ReplaceAllButton.IsEnabled = Results.Count > 0;
        }

        private void OpenSelectedListViewItem(bool inNewTab = false, Result resultToOpen = default)
        {
            if (isSearchInProgress)
            {
                this.ShowError(LocalizationSource.GetString("Msg_CantOpenResultsWhileInProgress"));
                return;
            }

            if (resultToOpen != default)
            {
                mainWindow.OpenCodeEntry(resultToOpen.Code, resultToOpen.LineNumber, editorTab, inNewTab);
            }
            else
            {
                foreach (Result result in ResultsListView.SelectedItems)
                {
                    mainWindow.OpenCodeEntry(result.Code, result.LineNumber, editorTab, inNewTab);
                    // Only first one opens in current tab, the rest go into new tabs.
                    inNewTab = true;
                }
            }

            // So it activates the window after it finished processing
            // (otherwise it doesn't work sometimes)
            _ = Task.Run(() =>
            {
                Dispatcher.Invoke(mainWindow.Activate);
            });
        }

        private void CopyListViewItems(IEnumerable items)
        {
            string str = String.Join('\n', items
                .Cast<Result>()
                .Select(result => $"{result.Code}\t{result.LineNumber}\t{result.LineText}"));
            try
            {
                Clipboard.SetText(str);
            }
            catch (Exception ex)
            {
                this.ShowError(string.Format(LocalizationSource.GetString("Msg_CantCopyToClipboard"), ex.Message));
            }
        }

        private async void SearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                await Search();
            }
        }

        private async void ReplaceTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                await Replace(replaceAll: true);
            }
        }

        private async void ReplaceButton_Click(object sender, RoutedEventArgs e)
        {
            await Replace(replaceAll: false);
        }

        private async void ReplaceAllButton_Click(object sender, RoutedEventArgs e)
        {
            await Replace(replaceAll: true);
        }

        private void ListViewItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed
                && e.ChangedButton == MouseButton.Middle)
            {
                if (e.Source is not FrameworkElement elem
                    || elem.DataContext is not Result res)
                    return;

                OpenSelectedListViewItem(true, res);  
            }
        }

        private void ListViewItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            OpenSelectedListViewItem();
        }

        private void ListViewItem_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return)
            {
                OpenSelectedListViewItem();
                e.Handled = true;
            }   
        }

        private void MenuItemOpen_Click(object sender, RoutedEventArgs e)
        {
            OpenSelectedListViewItem();
        }

        private void MenuItemOpenInNewTab_Click(object sender, RoutedEventArgs e)
        {
            OpenSelectedListViewItem(true);
        }

        private void MenuItemCopyAll_Click(object sender, RoutedEventArgs e)
        {
            CopyListViewItems(ResultsListView.Items);
        }

        private void CopyCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            CopyListViewItems(ResultsListView.SelectedItems.Cast<Result>().OrderBy(item => ResultsListView.Items.IndexOf(item)));
        }

        private void ResultsListView_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            ReplaceButton.IsEnabled = ResultsListView.SelectedItem is Result;
        }

        private void Results_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            ReplaceAllButton.IsEnabled = Results.Count > 0;
        }

        private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible || IsLoaded)
                return;

            if (Settings.Instance.EnableDarkMode)
                MainWindow.SetDarkTitleBarForWindow(this, true, false);
        }

        private void PinButton_Checked(object sender, RoutedEventArgs e)
        {
            Topmost = true;
            PinIcon.Fill = SystemColors.HighlightBrush;
        }

        private void PinButton_Unchecked(object sender, RoutedEventArgs e)
        {
            Topmost = false;
            PinIcon.Fill = System.Windows.Media.Brushes.Transparent;
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            e.Cancel = (loaderDialog is not null);
        }
    }
}

#pragma warning restore CA1416 // Validate platform compatibility
