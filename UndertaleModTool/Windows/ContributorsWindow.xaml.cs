using System.Windows;
using System.Windows.Controls;

namespace UndertaleModTool;

public partial class ContributorsWindow : Window
{
    public ContributorsWindow()
    {
        InitializeComponent();
    }

    private void OpenLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string url })
            MainWindow.OpenBrowser(url);
    }
}
