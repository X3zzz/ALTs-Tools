using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using AltsTools.Models;

namespace AltsTools.Localization
{
    /// <summary>Converts a skin-related enum to its localized display string.</summary>
    public sealed class EnumLocalizeConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            string key = value switch
            {
                MinecraftSkinVariant v => $"Skin.Variant.{v}",
                PreviewAnimationMode a => $"Skin.Anim.{a}",
                PreviewBackgroundMode b => $"Skin.Bg.{b}",
                _ => value?.ToString() ?? string.Empty
            };
            return LocalizationManager.Instance[key];
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => BindingOperations.DoNothing;
    }
}
