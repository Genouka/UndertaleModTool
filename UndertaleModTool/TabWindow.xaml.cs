using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using UndertaleModTool.Localization;

namespace UndertaleModTool
{
    public partial class TabWindow : Window, INotifyPropertyChanged, IWindowHost
    {
        private Tab _currentTab;
        private readonly MainWindow _mainWindow;

        public ObservableCollection<Tab> Tabs { get; set; } = new();
        public Tab CurrentTab
        {
            get => _currentTab;
            set
            {
                _currentTab = value;
                OnPropertyChanged();
                OnPropertyChanged("Selected");
            }
        }
        public int CurrentTabIndex { get; set; } = 0;

        public List<Tab> ClosedTabsHistory { get; } = new();

        public object Selected
        {
            get => CurrentTab?.CurrentObject;
            set
            {
                OnPropertyChanged();
                OpenInTab(value);
            }
        }

        Window IWindowHost.Window => this;
        ContentControl IWindowHost.DataEditor => DataEditor;
        TabControlDark IWindowHost.TabController => TabController;
        IList<Tab> IWindowHost.ClosedTabsHistory => ClosedTabsHistory;

        public TabWindow(MainWindow mainWindow, Tab tab)
        {
            InitializeComponent();
            DataContext = this;
            _mainWindow = mainWindow;

            tab.Host = this;
            Tabs.Add(tab);
            CurrentTabIndex = 0;
            CurrentTab = tab;

            Title = Tab.GetTitleForObject(tab.CurrentObject) ?? "UndertaleModTool";

            SourceInitialized += (s, e) =>
            {
                if (Settings.Instance.EnableDarkMode)
                    MainWindow.SetDarkTitleBarForWindow(this, true, false);
            };

            IsTabMultiLine = Settings.Instance.TabMultiLine;
            UpdateTabLayout();

            Tabs.CollectionChanged += (s, args) => Dispatcher.BeginInvoke(() => UpdateTabScrollButtonsVisibility());
            TabsGrid.SizeChanged += (s, args) => UpdateTabScrollButtonsVisibility();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
        public void RaiseOnSelectedChanged()
        {
            OnPropertyChanged("Selected");
        }

        public void OpenInTab(object obj, bool isNewTab = false, string tabTitle = null)
        {
            if (obj is null)
                return;

            if (obj is DescriptionView && CurrentTab is not null && !CurrentTab.AutoClose)
                return;

            if (Tabs.Count > 0 && CurrentTabIndex >= 0 && CurrentTab.AutoClose)
                CloseTab(CurrentTab.TabIndex, false);

            if (isNewTab || Tabs.Count == 0)
            {
                int newIndex = Tabs.Count;
                Tab newTab = new(obj, newIndex, this, tabTitle);

                Tabs.Add(newTab);
                CurrentTabIndex = newIndex;

                newTab.History.Add(obj);

                if (!TabController.IsLoaded)
                    CurrentTab = newTab;
            }
            else if (obj != CurrentTab?.CurrentObject)
            {
                if (CurrentTab.HistoryPosition < CurrentTab.History.Count - 1)
                {
                    int count = CurrentTab.History.Count - CurrentTab.HistoryPosition - 1;
                    for (int i = 0; i < count; i++)
                        CurrentTab.History.RemoveAt(CurrentTab.History.Count - 1);
                }

                CurrentTab.CurrentObject = obj;
                UpdateObjectLabel(obj);

                CurrentTab.History.Add(obj);
                CurrentTab.HistoryPosition++;
            }

            if (DataEditor.IsLoaded)
                VisualTreeUtil.GetNearestParent<ScrollViewer>(DataEditor)?.ScrollToTop();
        }

        public void CloseTab(bool addDefaultTab = true)
        {
            CloseTab(CurrentTabIndex, addDefaultTab);
        }

        public void CloseTab(int tabIndex, bool addDefaultTab = true)
        {
            if (tabIndex >= 0 && tabIndex < Tabs.Count)
            {
                Tab closingTab = Tabs[tabIndex];

                TabController.SelectionChanged -= TabController_SelectionChanged;

                int currIndex = CurrentTabIndex;

                var item = TabController.ItemContainerGenerator.ContainerFromIndex(tabIndex) as TabItem;
                if (item is not null)
                    item.Template = null;

                Tabs.RemoveAt(tabIndex);

                if (!closingTab.AutoClose)
                    ClosedTabsHistory.Add(closingTab);

                if (Tabs.Count == 0)
                {
                    if (!closingTab.AutoClose)
                        CurrentTab.SaveTabContentState();

                    CurrentTabIndex = -1;
                    CurrentTab = null;

                    TabController.SelectionChanged += TabController_SelectionChanged;

                    Close();
                    return;
                }
                else
                {
                    bool tabIsChanged = false;

                    for (int i = tabIndex; i < Tabs.Count; i++)
                        Tabs[i].TabIndex = i;

                    if (currIndex == tabIndex)
                    {
                        if (Tabs.Count > 1 && tabIndex < Tabs.Count - 1)
                        {
                            currIndex = Tabs.Count - 1;
                        }
                        else
                        {
                            if (currIndex != 0)
                                currIndex -= 1;

                            tabIsChanged = true;
                            CurrentTab.SaveTabContentState();
                        }
                    }
                    else if (currIndex > tabIndex)
                    {
                        currIndex -= 1;
                    }

                    TabController.SelectionChanged += TabController_SelectionChanged;

                    CurrentTabIndex = currIndex;
                    Tab newTab = Tabs[CurrentTabIndex];

                    if (tabIsChanged)
                    {
                        if (closingTab.CurrentObject != newTab.CurrentObject)
                            newTab.PrepareCodeEditor();
                    }

                    CurrentTab = newTab;
                    UpdateObjectLabel(CurrentTab.CurrentObject);

                    if (tabIsChanged)
                        CurrentTab.RestoreTabContentState();
                }
            }
        }

        public bool CloseTab(object obj, bool addDefaultTab = true)
        {
            if (obj is not null)
            {
                int tabIndex = Tabs.FirstOrDefault(x => x.CurrentObject == obj)?.TabIndex ?? -1;
                if (tabIndex != -1)
                {
                    CloseTab(tabIndex, addDefaultTab);
                    return true;
                }
            }
            return false;
        }

        public void ChangeSelection(object newsel, bool inNewTab = false)
        {
            OpenInTab(newsel, inNewTab);
        }

        public void UpdateObjectLabel(object obj)
        {
            Title = Tab.GetTitleForObject(obj) ?? "UndertaleModTool";
        }

        private void TabController_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TabController.SelectedIndex >= 0)
            {
                CurrentTab?.SaveTabContentState();

                Tab newTab = Tabs[CurrentTabIndex];

                if (CurrentTab?.CurrentObject != newTab.CurrentObject)
                    newTab.PrepareCodeEditor();

                CurrentTab = newTab;

                UpdateObjectLabel(CurrentTab.CurrentObject);

                CurrentTab.RestoreTabContentState();
            }
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            foreach (var tab in Tabs)
            {
                tab.SaveTabContentState();
                tab.Host = _mainWindow;
                _mainWindow.ClosedTabsHistory.Add(tab);
            }
            Tabs.Clear();
            CurrentTab = null;
        }

        private void TabTitleText_Initialized(object sender, EventArgs e)
        {
            Tab.SetTabTitleBinding(null, null, sender as TextBlock);
        }

        private void TabItem_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                if (sender is TabItem tabItem && tabItem.DataContext is Tab tab)
                    CloseTab(tab.TabIndex);
            }
        }

#pragma warning disable CS0649
        private Point _initTabContPos;
#pragma warning restore CS0649
        private void TabItem_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.Source is not TabItemDark tabItem || e.OriginalSource is Button)
                return;

            if (Mouse.PrimaryDevice.LeftButton == MouseButtonState.Pressed)
            {
                Point currPos = e.GetPosition(TabScrollViewer);
                if (Math.Abs(Point.Subtract(currPos, _initTabContPos).X) < 2)
                    return;

                CurrentTabIndex = tabItem.TabIndex;
                try
                {
                    var tab = tabItem.DataContext as Tab;
                    if (tab is not null)
                    {
                        var data = new DataObject();
                        data.SetData(typeof(TabItemDark), tabItem);
                        data.SetData(typeof(Tab), tab);
                        DragDrop.DoDragDrop(tabItem, data, DragDropEffects.All);
                    }
                    else
                    {
                        DragDrop.DoDragDrop(tabItem, tabItem, DragDropEffects.All);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error on handling tab drag&drop:\n{ex}");
                }

                if (Tabs.Count > 0 && CurrentTabIndex >= 0 && CurrentTabIndex < Tabs.Count)
                {
                    var tabUnderMouse = Tabs[CurrentTabIndex];
                    if (tabUnderMouse is not null && tabUnderMouse == tabItem.DataContext as Tab
                        && ReferenceEquals(tabUnderMouse.Host, this))
                    {
                        Point screenPos = PointToScreen(e.GetPosition(this));
                        var tabRect = new Rect(PointToScreen(new Point(0, 0)),
                                               new Size(ActualWidth, TabScrollViewer.ActualHeight + TabController.ActualHeight));
                        if (!tabRect.Contains(screenPos))
                        {
                            var targetWindow = FindTargetWindowAt(screenPos);
                            if (targetWindow is not null)
                            {
                                RemoveTab(tabUnderMouse);
                                targetWindow.ReceiveTabFromWindow(tabUnderMouse);
                                targetWindow.Activate();
                            }
                            else if (Tabs.Count > 0 || !tabUnderMouse.AutoClose)
                            {
                                TearOutToMainWindow(tabUnderMouse, screenPos);
                            }
                        }
                    }
                }
            }
        }

        private MainWindow FindTargetWindowAt(Point screenPos)
        {
            if (_mainWindow.Visibility == Visibility.Visible)
            {
                var mainRect = new Rect(
                    _mainWindow.PointToScreen(new Point(0, 0)),
                    new Size(_mainWindow.ActualWidth, _mainWindow.ActualHeight));
                if (mainRect.Contains(screenPos))
                    return _mainWindow;
            }

            return null;
        }

        private void TearOutToMainWindow(Tab tab, Point screenPos)
        {
            tab.SaveTabContentState();
            RemoveTab(tab);
            _mainWindow.ReceiveTabFromWindow(tab);
            _mainWindow.Activate();
        }

        public void RemoveTab(Tab tab)
        {
            int removeIndex = tab.TabIndex;
            if (removeIndex < 0 || removeIndex >= Tabs.Count || Tabs[removeIndex] != tab)
            {
                removeIndex = Tabs.IndexOf(tab);
                if (removeIndex < 0)
                    return;
            }

            tab.SaveTabContentState();

            TabController.SelectionChanged -= TabController_SelectionChanged;

            Tabs.RemoveAt(removeIndex);
            for (int i = removeIndex; i < Tabs.Count; i++)
                Tabs[i].TabIndex = i;

            if (Tabs.Count == 0)
            {
                CurrentTabIndex = -1;
                CurrentTab = null;
                TabController.SelectionChanged += TabController_SelectionChanged;
                Close();
                return;
            }

            CurrentTabIndex = Math.Min(removeIndex, Tabs.Count - 1);
            TabController.SelectionChanged += TabController_SelectionChanged;

            CurrentTab = Tabs[CurrentTabIndex];
            if (CurrentTab is not null)
            {
                UpdateObjectLabel(CurrentTab.CurrentObject);
                CurrentTab.RestoreTabContentState();
            }
        }

        private void TabItem_Drop(object sender, DragEventArgs e)
        {
            if (e.Source is not TabItemDark tabItemTarget)
                return;

            int targetIndex = tabItemTarget.TabIndex;
            var sourceTab = e.Data.GetData(typeof(Tab)) as Tab;

            if (sourceTab is not null && !ReferenceEquals(sourceTab.Host, this))
            {
                if (sourceTab.Host is MainWindow mainWindow)
                    mainWindow.CloseTab(sourceTab, false);
                else if (sourceTab.Host is TabWindow otherWindow)
                    otherWindow.RemoveTab(sourceTab);

                sourceTab.Host = this;

                TabController.SelectionChanged -= TabController_SelectionChanged;

                Tabs.Insert(targetIndex, sourceTab);
                for (int i = 0; i < Tabs.Count; i++)
                    Tabs[i].TabIndex = i;
                CurrentTabIndex = targetIndex;

                TabController.SelectionChanged += TabController_SelectionChanged;

                CurrentTab = sourceTab;
                UpdateObjectLabel(sourceTab.CurrentObject);
                sourceTab.RestoreTabContentState();
                e.Handled = true;
                return;
            }

            var sourceTabItem = e.Data.GetData(typeof(TabItemDark)) as TabItemDark;
            if (sourceTabItem is not null && !sourceTabItem.Equals(tabItemTarget))
            {
                int sourceIndex = sourceTabItem.TabIndex;
                Tab localSourceTab = sourceTabItem.DataContext as Tab;
                if (localSourceTab is null)
                    return;

                TabController.SelectionChanged -= TabController_SelectionChanged;

                Tabs.RemoveAt(sourceIndex);
                Tabs.Insert(targetIndex, localSourceTab);

                for (int i = 0; i < Tabs.Count; i++)
                    Tabs[i].TabIndex = i;

                CurrentTabIndex = targetIndex;

                TabController.SelectionChanged += TabController_SelectionChanged;
                e.Handled = true;
            }
        }

        private void TabController_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(Tab)))
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
            }
        }

        private void TabController_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(Tab)))
            {
                var tab = e.Data.GetData(typeof(Tab)) as Tab;
                if (tab is null)
                    return;

                var sourceHost = tab.Host;
                if (sourceHost is MainWindow mw && !ReferenceEquals(mw, _mainWindow))
                    return;

                if (sourceHost is MainWindow mainWindow && !ReferenceEquals(mainWindow, this))
                {
                    mainWindow.CloseTab(tab, false);
                    tab.Host = this;

                    TabController.SelectionChanged -= TabController_SelectionChanged;

                    int newIndex = Tabs.Count;
                    tab.TabIndex = newIndex;
                    Tabs.Add(tab);
                    CurrentTabIndex = newIndex;

                    TabController.SelectionChanged += TabController_SelectionChanged;

                    CurrentTab = tab;
                    UpdateObjectLabel(tab.CurrentObject);
                    tab.RestoreTabContentState();
                    e.Handled = true;
                }
                else if (sourceHost is TabWindow otherWindow && !ReferenceEquals(otherWindow, this))
                {
                    otherWindow.RemoveTab(tab);
                    tab.Host = this;

                    TabController.SelectionChanged -= TabController_SelectionChanged;

                    int newIndex = Tabs.Count;
                    tab.TabIndex = newIndex;
                    Tabs.Add(tab);
                    CurrentTabIndex = newIndex;

                    TabController.SelectionChanged += TabController_SelectionChanged;

                    CurrentTab = tab;
                    UpdateObjectLabel(tab.CurrentObject);
                    tab.RestoreTabContentState();
                    e.Handled = true;
                }
            }
        }

        private void TabCloseButton_OnClick(object sender, RoutedEventArgs e)
        {
            Tab tab = (sender as FrameworkElement)?.DataContext as Tab;
            if (tab is not null)
                CloseTab(tab.TabIndex);
        }

        private void TabCloseButton_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.FindName("Img") is not Image)
            {
                var btn = fe as Button;
                if (btn?.Content is Image img)
                    img.Source = Tab.ClosedHoverIcon;
            }
        }

        private void TabCloseButton_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement fe)
            {
                var btn = fe as Button;
                if (btn?.Content is Image img)
                    img.Source = Tab.ClosedIcon;
            }
        }

        private void TabScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            bool multiLine = e.Delta < 0;
            if (IsTabMultiLine != multiLine)
            {
                IsTabMultiLine = multiLine;
                UpdateTabLayout();
            }
            e.Handled = true;
        }

        private bool _isTabMultiLine = false;
        public bool IsTabMultiLine
        {
            get => _isTabMultiLine;
            set
            {
                _isTabMultiLine = value;
                if (Settings.Instance is not null)
                    Settings.Instance.TabMultiLine = value;
            }
        }

        private void UpdateTabLayout()
        {
            TabController.IsTabMultiLine = IsTabMultiLine;

            var tabPanel = MainWindow.FindVisualChild<TabPanel>(TabController);
            if (tabPanel is not null)
            {
                var isMultiLineProp = tabPanel.GetType().GetProperty("IsMultiLine");
                if (isMultiLineProp is not null)
                {
                    isMultiLineProp.SetValue(tabPanel, IsTabMultiLine);
                    tabPanel.InvalidateMeasure();
                    tabPanel.InvalidateArrange();
                }
            }

            if (IsTabMultiLine)
            {
                TabScrollViewer.Height = Double.NaN;
                TabScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                TabScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                TabController.Height = Double.NaN;
                TabScrollButtonsPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                TabScrollViewer.Height = 24;
                TabScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
                TabScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
                TabController.Height = 20;
                UpdateTabScrollButtonsVisibility();
            }
        }

        private void UpdateTabScrollButtonsVisibility()
        {
            if (IsTabMultiLine)
            {
                TabScrollButtonsPanel.Visibility = Visibility.Collapsed;
                return;
            }

            TabScrollViewer.UpdateLayout();
            TabScrollButtonsPanel.Visibility = TabController.ActualWidth > TabsGrid.ActualWidth
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TabsScrollLeftButton_Click(object sender, RoutedEventArgs e)
        {
            TabScrollViewer.ScrollToHorizontalOffset(TabScrollViewer.HorizontalOffset - 40);
        }

        private void TabsScrollRightButton_Click(object sender, RoutedEventArgs e)
        {
            TabScrollViewer.ScrollToHorizontalOffset(TabScrollViewer.HorizontalOffset + 40);
        }

        private void TabListButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenuDark();

            for (int i = 0; i < Tabs.Count; i++)
            {
                var tab = Tabs[i];
                var menuItem = new MenuItem
                {
                    Header = tab.TabTitle,
                    FontWeight = i == CurrentTabIndex ? FontWeights.Bold : FontWeights.Normal,
                    Tag = i
                };
                int capturedIndex = i;
                menuItem.Click += (s, args) =>
                {
                    CurrentTabIndex = capturedIndex;
                };
                menu.Items.Add(menuItem);
            }

            menu.PlacementTarget = sender as UIElement;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            ScrollViewer viewer = sender as ScrollViewer;
            if (viewer.ComputedVerticalScrollBarVisibility != Visibility.Visible && e.Source == viewer)
                e.Handled = true;
        }
    }
}
