using System;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace AltsTools.Localization
{
    /// <summary>
    /// Avalonia markup extension: <c>{loc:Tr KeyName}</c>. Produces a OneWay
    /// binding to LocalizationManager's string indexer so bound text updates
    /// live when the language changes.
    /// </summary>
    public sealed class TrExtension : MarkupExtension
    {
        public string Key { get; set; } = string.Empty;

        public TrExtension() { }
        public TrExtension(string key) => Key = key;

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            return new Binding($"[{Key}]")
            {
                Source = LocalizationManager.Instance,
                Mode = BindingMode.OneWay,
            };
        }
    }
}
