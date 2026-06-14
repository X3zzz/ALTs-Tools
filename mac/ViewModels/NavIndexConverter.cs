using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace AltsTools.ViewModels
{
    /// <summary>Returns true when the bound int index equals the parameter.</summary>
    public sealed class NavIndexConverter : IValueConverter
    {
        public static readonly NavIndexConverter Eq = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            int idx = value is int i ? i : -1;
            int want = parameter switch
            {
                int p => p,
                string s when int.TryParse(s, out var p) => p,
                _ => -2
            };
            return idx == want;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
