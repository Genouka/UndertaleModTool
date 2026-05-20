using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using UndertaleModLib;

namespace UndertaleModTool
{
    [ValueConversion(typeof(object), typeof(bool))]
    public class IsModifiedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not UndertaleResource resource)
                return false;

            if (!(Settings.Instance?.ChangeTrackingEnabled ?? true))
                return false;

            var mainWindow = Application.Current.MainWindow as MainWindow;
            return mainWindow?.ChangeTracker?.IsModified(resource) == true;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
