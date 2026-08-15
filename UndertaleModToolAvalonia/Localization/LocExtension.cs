using System;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia
{
    public class LocExtension : MarkupExtension
    {
        public string Key { get; }

        public LocExtension(string key)
        {
            Key = key;
        }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            return new Binding($"[{Key}]")
            {
                Source = LocalizationSource.Instance,
                Mode = BindingMode.OneWay
            };
        }
    }
}