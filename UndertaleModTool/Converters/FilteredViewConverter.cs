using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using UndertaleModLib;

namespace UndertaleModTool
{
    [ValueConversion(typeof(object), typeof(ICollectionView))]
    public class FilteredViewConverter : DependencyObject, IValueConverter
    {
        public static DependencyProperty FilterProperty =
            DependencyProperty.Register("Filter", typeof(string),
                typeof(FilteredViewConverter),
                new FrameworkPropertyMetadata(null));

        public string Filter
        {
            get { return (string)GetValue(FilterProperty); }
            set { SetValue(FilterProperty, value); }
        }

        protected virtual Predicate<object> CreateFilter()
        {
            return (obj) =>
            {
                var filter = Filter;
                if (String.IsNullOrEmpty(filter))
                    return true;
                if (obj is ISearchable searchable)
                    return searchable.SearchMatches(filter);
                if (obj is UndertaleNamedResource namedRes)
                    return (namedRes.Name?.Content?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
                if (obj is object[] links)
                {
                    foreach (var x in links)
                    {
                        var str = x is UndertaleNamedResource res ? res.Name?.Content : x.ToString();
                        if (str?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                    return false;
                }
                return true;
            };
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is null)
                return null;
            ICollectionView filteredView = CollectionViewSource.GetDefaultView(value);
            filteredView.Filter = CreateFilter();
            return filteredView;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
