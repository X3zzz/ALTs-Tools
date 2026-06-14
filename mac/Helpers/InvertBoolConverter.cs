using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace AltsTools.Helpers
{
    public class InvertBoolConverter : IValueConverter
    {
        public object? Convert(object? value, Type t, object? p, CultureInfo c)
            => value is bool b ? !b : value;
        public object? ConvertBack(object? value, Type t, object? p, CultureInfo c)
            => value is bool b ? !b : value;
    }
}
