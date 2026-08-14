using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;

namespace UndertaleModTool
{
    /// <summary>
    /// A standard data grid which compatible with the dark mode.
    /// </summary>
    public partial class DataGridDark : DataGrid
    {
        /// <summary>
        /// Custom column header style. The stock theme renders the header background from
        /// theme system brushes that ignore the theme's "Custom*" overrides (and stay
        /// transparent/white in background-image transparency mode). This style and its
        /// control template bind the header background/foreground directly to the theme
        /// brushes instead, so the header follows the current mode.
        /// </summary>
        private static readonly Style columnHeaderStyle = (Style)XamlReader.Parse(@"
<Style xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
       xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
       xmlns:scm=""clr-namespace:System.ComponentModel;assembly=System.ComponentModel.TypeConverter""
       TargetType=""{x:Type DataGridColumnHeader}"">
    <Setter Property=""Background"" Value=""{DynamicResource CustomControlBrush}""/>
    <Setter Property=""Foreground"" Value=""{DynamicResource CustomTextBrush}""/>
    <Setter Property=""BorderBrush"" Value=""{DynamicResource {x:Static SystemColors.ControlDarkBrushKey}}""/>
    <Setter Property=""BorderThickness"" Value=""0,0,0,1""/>
    <Setter Property=""Padding"" Value=""6,0,6,0""/>
    <Setter Property=""FontWeight"" Value=""SemiBold""/>
    <Setter Property=""Template"">
        <Setter.Value>
            <ControlTemplate TargetType=""{x:Type DataGridColumnHeader}"">
                <ControlTemplate.Resources>
                    <ScaleTransform x:Key=""SortArrowUp"" ScaleX=""1"" ScaleY=""-1""/>
                    <ScaleTransform x:Key=""SortArrowDown"" ScaleX=""1"" ScaleY=""1""/>
                </ControlTemplate.Resources>
                <Grid SnapsToDevicePixels=""True"">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width=""Auto""/>
                        <ColumnDefinition Width=""*""/>
                        <ColumnDefinition Width=""Auto""/>
                    </Grid.ColumnDefinitions>
                    <Border x:Name=""BackgroundBorder"" Grid.ColumnSpan=""3""
                            Background=""{TemplateBinding Background}""
                            BorderBrush=""{TemplateBinding BorderBrush}""
                            BorderThickness=""{TemplateBinding BorderThickness}""/>
                    <ContentPresenter Grid.Column=""1""
                                      Content=""{TemplateBinding Content}""
                                      ContentTemplate=""{TemplateBinding ContentTemplate}""
                                      ContentStringFormat=""{TemplateBinding ContentStringFormat}""
                                      HorizontalAlignment=""{TemplateBinding HorizontalContentAlignment}""
                                      VerticalAlignment=""{TemplateBinding VerticalContentAlignment}""
                                      Margin=""{TemplateBinding Padding}""/>
                    <Path x:Name=""SortArrow"" Grid.Column=""2""
                          Width=""8"" Height=""6""
                          Data=""M 0,0 L 4,4 L 8,0 Z""
                          Fill=""{DynamicResource {x:Static SystemColors.GrayTextBrushKey}}""
                          HorizontalAlignment=""Center""
                          VerticalAlignment=""Center""
                          RenderTransformOrigin=""0.5,0.5""
                          RenderTransform=""{StaticResource SortArrowDown}""
                          Visibility=""Collapsed""/>
                    <Thumb x:Name=""PART_LeftHeaderGripper"" Grid.Column=""0"" Width=""8"" HorizontalAlignment=""Left"">
                        <Thumb.Template>
                            <ControlTemplate TargetType=""Thumb"">
                                <Border Background=""Transparent""/>
                            </ControlTemplate>
                        </Thumb.Template>
                    </Thumb>
                    <Thumb x:Name=""PART_RightHeaderGripper"" Grid.Column=""2"" Width=""8"" HorizontalAlignment=""Right"">
                        <Thumb.Template>
                            <ControlTemplate TargetType=""Thumb"">
                                <Border Background=""Transparent""/>
                            </ControlTemplate>
                        </Thumb.Template>
                    </Thumb>
                </Grid>
                <ControlTemplate.Triggers>
                    <Trigger Property=""SortDirection"" Value=""{x:Static scm:ListSortDirection.Ascending}"">
                        <Setter TargetName=""SortArrow"" Property=""Visibility"" Value=""Visible""/>
                        <Setter TargetName=""SortArrow"" Property=""RenderTransform"" Value=""{StaticResource SortArrowUp}""/>
                        <Setter TargetName=""SortArrow"" Property=""Fill"" Value=""{DynamicResource {x:Static SystemColors.HighlightBrushKey}}""/>
                    </Trigger>
                    <Trigger Property=""SortDirection"" Value=""{x:Static scm:ListSortDirection.Descending}"">
                        <Setter TargetName=""SortArrow"" Property=""Visibility"" Value=""Visible""/>
                        <Setter TargetName=""SortArrow"" Property=""RenderTransform"" Value=""{StaticResource SortArrowDown}""/>
                    </Trigger>
                    <Trigger Property=""IsMouseOver"" Value=""True"">
                        <Setter TargetName=""BackgroundBorder"" Property=""Background"" Value=""{DynamicResource {x:Static SystemColors.ControlLightBrushKey}}""/>
                    </Trigger>
                    <MultiTrigger>
                        <MultiTrigger.Conditions>
                            <Condition Property=""IsMouseOver"" Value=""True""/>
                            <Condition Property=""SortDirection"" Value=""{x:Static scm:ListSortDirection.Ascending}""/>
                        </MultiTrigger.Conditions>
                        <Setter TargetName=""SortArrow"" Property=""Fill"" Value=""{DynamicResource {x:Static SystemColors.HighlightBrushKey}}""/>
                    </MultiTrigger>
                    <MultiTrigger>
                        <MultiTrigger.Conditions>
                            <Condition Property=""IsMouseOver"" Value=""True""/>
                            <Condition Property=""SortDirection"" Value=""{x:Static scm:ListSortDirection.Descending}""/>
                        </MultiTrigger.Conditions>
                        <Setter TargetName=""SortArrow"" Property=""Fill"" Value=""{DynamicResource {x:Static SystemColors.HighlightBrushKey}}""/>
                    </MultiTrigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>");

        /// <summary>Initializes a new instance of the data grid.</summary>
        public DataGridDark()
        {
            ColumnHeaderStyle = columnHeaderStyle;

            AddingNewItem += DataGrid_AddingNewItem;
        }

        protected override void OnSelectionChanged(SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] != DependencyProperty.UnsetValue)
                base.OnSelectionChanged(e);
            else
                System.Diagnostics.Debug.WriteLine("DataGridDark.OnSelectionChanged() - e.AddedItems[0] is \"UnsetValue\", skipping event handling.");
        }

        private void DataGrid_AddingNewItem(object sender, AddingNewItemEventArgs e)
        {
            _ = Task.Run(() =>
            {
                Dispatcher.Invoke(() =>
                {
                    UpdateLayout();
                    CommitEdit(DataGridEditingUnit.Row, true);
                });
            });
        }
    }
}