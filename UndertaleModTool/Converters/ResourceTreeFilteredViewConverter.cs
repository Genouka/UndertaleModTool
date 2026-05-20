using System;
using System.ComponentModel;
using System.Windows.Data;

namespace UndertaleModTool
{
    [ValueConversion(typeof(object), typeof(ICollectionView))]
    public class ResourceTreeFilteredViewConverter : FilteredViewConverter
    {
        protected override Predicate<object> CreateFilter()
        {
            Predicate<object> baseFilter = base.CreateFilter();
            return (obj) =>
            {
                if (obj is null && Settings.Instance is not null && !Settings.Instance.ShowNullEntriesInResourceTree)
                    return false;
                return baseFilter(obj);
            };
        }
    }
}
