using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace AltsTools.Helpers
{
    /// <summary>byte[] PNG → Avalonia Bitmap (for the skin preview thumbnail).</summary>
    public sealed class BytesToBitmapConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is byte[] bytes && bytes.Length > 0)
            {
                try { return new Bitmap(new MemoryStream(bytes)); } catch { return null; }
            }
            return null;
        }

        public object? ConvertBack(object? value, Type t, object? p, CultureInfo c)
            => throw new NotSupportedException();
    }
}
