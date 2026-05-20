using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace UndertaleModTool
{
    public interface IWindowHost
    {
        Window Window { get; }
        ContentControl DataEditor { get; }
        TabControlDark TabController { get; }
        ObservableCollection<Tab> Tabs { get; }
        Tab CurrentTab { get; set; }
        int CurrentTabIndex { get; set; }
        IList<Tab> ClosedTabsHistory { get; }

        void RaiseOnSelectedChanged();
        void OpenInTab(object obj, bool isNewTab = false, string tabTitle = null);
        void CloseTab(int tabIndex, bool addDefaultTab = true);
        bool CloseTab(object obj, bool addDefaultTab = true);
        void ChangeSelection(object newsel, bool inNewTab = false);
        void UpdateObjectLabel(object obj);
    }
}
