using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace UndertaleModTool
{
    /// <summary>
    /// A standard combo box which compatible with the dark mode.
    /// </summary>
    public partial class ComboBoxDark : ComboBox
    {
        // Setting "Foreground" implicitly breaks internal "IsEnabled" style trigger,
        // so this has to be handled manually.
        private static readonly SolidColorBrush disabledTextBrush = new(Color.FromArgb(255, 131, 131, 131));

        /// <summary>
        /// Custom control template. The stock theme template renders the background through ComboBoxChrome,
        /// which ignores the ComboBox's own "Background" property; this template binds the chrome background
        /// and border directly to the control instead, so setting "Background"/"BorderBrush" actually works.
        /// </summary>
        private static readonly ControlTemplate defaultTemplate = (ControlTemplate)XamlReader.Parse(@"
<ControlTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                 xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
                 TargetType=""{x:Type ComboBox}"">
    <Grid>
        <VisualStateManager.VisualStateGroups>
            <VisualStateGroup x:Name=""EditableStates"">
                <VisualState x:Name=""Editable"">
                    <Storyboard>
                        <ObjectAnimationUsingKeyFrames Storyboard.TargetName=""PART_EditableTextBox""
                                                       Storyboard.TargetProperty=""Visibility"">
                            <DiscreteObjectKeyFrame KeyTime=""0"" Value=""Visible""/>
                        </ObjectAnimationUsingKeyFrames>
                    </Storyboard>
                </VisualState>
                <VisualState x:Name=""Uneditable""/>
            </VisualStateGroup>
        </VisualStateManager.VisualStateGroups>
        <ToggleButton x:Name=""toggleButton""
                      Focusable=""False""
                      ClickMode=""Press""
                      Background=""{Binding RelativeSource={RelativeSource TemplatedParent}, Path=Background}""
                      BorderBrush=""{Binding RelativeSource={RelativeSource TemplatedParent}, Path=BorderBrush}""
                      IsChecked=""{Binding RelativeSource={RelativeSource TemplatedParent}, Path=IsDropDownOpen, Mode=TwoWay}"">
            <ToggleButton.Template>
                <ControlTemplate TargetType=""ToggleButton"">
                    <Border SnapsToDevicePixels=""True""
                            BorderThickness=""1""
                            CornerRadius=""3"">
                        <Border.Style>
                            <Style TargetType=""Border"">
                                <Setter Property=""Background"" Value=""{Binding RelativeSource={RelativeSource TemplatedParent}, Path=Background}""/>
                                <Setter Property=""BorderBrush"" Value=""{Binding RelativeSource={RelativeSource TemplatedParent}, Path=BorderBrush}""/>
                                <Style.Triggers>
                                    <Trigger Property=""IsMouseOver"" Value=""True"">
                                        <Setter Property=""BorderBrush"" Value=""{DynamicResource {x:Static SystemColors.ControlLightLightBrushKey}}""/>
                                    </Trigger>
                                </Style.Triggers>
                            </Style>
                        </Border.Style>
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition/>
                                <ColumnDefinition Width=""18""/>
                            </Grid.ColumnDefinitions>
                            <Path Grid.Column=""1""
                                  Data=""M 0 0 L 8 0 L 4 6 Z""
                                  Fill=""{TemplateBinding Foreground}""
                                  Stretch=""None""
                                  HorizontalAlignment=""Center""
                                  VerticalAlignment=""Center""/>
                        </Grid>
                    </Border>
                </ControlTemplate>
            </ToggleButton.Template>
        </ToggleButton>
        <ContentPresenter x:Name=""ContentSite""
                          IsHitTestVisible=""False""
                          Content=""{TemplateBinding SelectionBoxItem}""
                          ContentTemplate=""{TemplateBinding SelectionBoxItemTemplate}""
                          ContentTemplateSelector=""{TemplateBinding ItemTemplateSelector}""
                          Margin=""{TemplateBinding Padding}""
                          VerticalAlignment=""Center""/>
        <TextBox x:Name=""PART_EditableTextBox""
                 Style=""{x:Null}""
                 Background=""{TemplateBinding Background}""
                 Foreground=""{TemplateBinding Foreground}""
                 IsReadOnly=""{Binding RelativeSource={RelativeSource TemplatedParent}, Path=IsReadOnly}""
                 FontFamily=""{TemplateBinding FontFamily}""
                 FontSize=""{TemplateBinding FontSize}""
                 HorizontalContentAlignment=""{TemplateBinding HorizontalContentAlignment}""
                 VerticalContentAlignment=""{TemplateBinding VerticalContentAlignment}""
                 VerticalAlignment=""Center""
                 Margin=""{TemplateBinding Padding}""
                 Visibility=""Hidden""/>
        <Popup x:Name=""PART_Popup""
               AllowsTransparency=""True""
               IsOpen=""{TemplateBinding IsDropDownOpen}""
               PopupAnimation=""Slide""
               Placement=""Bottom"">
            <Border x:Name=""DropDownBorder""
                    SnapsToDevicePixels=""True""
                    MinWidth=""{TemplateBinding ActualWidth}""
                    MaxHeight=""{TemplateBinding MaxDropDownHeight}""
                    Background=""{DynamicResource CustomTextBoxBrush}""
                    BorderBrush=""{DynamicResource {x:Static SystemColors.ControlDarkBrushKey}}""
                    BorderThickness=""1""
                    CornerRadius=""3""
                    TextElement.Foreground=""{DynamicResource CustomTextBrush}"">
                <ScrollViewer x:Name=""DropDownScrollViewer"" CanContentScroll=""True"">
                    <Grid>
                        <ItemsPresenter KeyboardNavigation.DirectionalNavigation=""Contained""/>
                    </Grid>
                </ScrollViewer>
            </Border>
        </Popup>
    </Grid>
</ControlTemplate>");

        /// <summary>Initializes a new instance of the combo box.</summary>
        public ComboBoxDark()
        {
            // Even though this will be called again in "OnPropertyChanged()", it's required.
            SetResourceReference(ForegroundProperty, "CustomTextBrush");
            SetResourceReference(BackgroundProperty, "CustomControlBrush");
            SetResourceReference(BorderBrushProperty, SystemColors.ControlDarkBrushKey);

            Template = defaultTemplate;
        }

        /// <inheritdoc/>
        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            if (e.Property == IsEnabledProperty)
            {
                if ((bool)e.NewValue)
                {
                    SetResourceReference(ForegroundProperty, "CustomTextBrush");
                    SetResourceReference(BackgroundProperty, "CustomControlBrush");
                }
                else
                {
                    Foreground = disabledTextBrush;
                }
            }

            base.OnPropertyChanged(e);
        }
    }
}