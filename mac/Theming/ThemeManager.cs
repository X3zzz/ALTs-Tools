using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AltsTools.Services;

namespace AltsTools.Theming
{
    /// <summary>
    /// Runtime appearance manager (Material You-style), Avalonia port of the WPF
    /// ThemeManager. Controls light/dark base theme and the accent color, persists
    /// to storage (RegistryService) and applies instantly. On Avalonia we drive
    /// the FluentTheme variant and publish the accent through app resources.
    /// </summary>
    public sealed class ThemeManager
    {
        private const string DarkKey    = "ThemeDark";
        private const string ColorKey   = "ThemeColor";
        private const string DynamicKey = "ThemeDynamic";

        public static ThemeManager Instance { get; } = new();

        public event EventHandler? ThemeChanged;

        private bool _isDark;
        private string _accentHex = DefaultAccent;
        private bool _dynamic;

        public const string DefaultAccent = "#6750A4"; // Material You purple

        public static IReadOnlyList<AccentColor> Accents { get; } = new[]
        {
            new AccentColor("Purple", "#6750A4"),
            new AccentColor("Indigo", "#3F51B5"),
            new AccentColor("Blue",   "#1976D2"),
            new AccentColor("Teal",   "#00897B"),
            new AccentColor("Green",  "#43A047"),
            new AccentColor("Lime",   "#AFB42B"),
            new AccentColor("Amber",  "#FF8F00"),
            new AccentColor("Orange", "#F4511E"),
            new AccentColor("Red",    "#E53935"),
            new AccentColor("Pink",   "#D81B60"),
        };

        private ThemeManager() { }

        public bool IsDark => _isDark;
        public string AccentHex => _accentHex;
        public bool IsDynamic => _dynamic;

        public void Initialize()
        {
            _isDark  = RegistryService.Read(DarkKey) == "1";
            _dynamic = RegistryService.Read(DynamicKey) == "1";
            string savedColor = RegistryService.Read(ColorKey);
            _accentHex = string.IsNullOrWhiteSpace(savedColor) ? DefaultAccent : savedColor;
            // The original WPF app defaults to the Light DeepPurple theme.
            Apply();
        }

        public void SetDarkMode(bool dark)
        {
            if (_isDark == dark) return;
            _isDark = dark;
            RegistryService.Write(DarkKey, dark ? "1" : "0");
            Apply();
        }

        public void SetAccent(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return;
            if (!_dynamic && _accentHex == hex) return;
            _dynamic = false;
            _accentHex = hex;
            RegistryService.Write(DynamicKey, "0");
            RegistryService.Write(ColorKey, hex);
            Apply();
        }

        public void EnableDynamic()
        {
            _dynamic = true;
            RegistryService.Write(DynamicKey, "1");
            Apply();
        }

        public void RefreshDynamic() { if (_dynamic) Apply(); }

        private void Apply()
        {
            void DoApply()
            {
                var app = Application.Current;
                if (app == null) return;

                app.RequestedThemeVariant = _isDark ? ThemeVariant.Dark : ThemeVariant.Light;

                Color accent;
                if (_dynamic)
                    accent = WallpaperColorService.TryGetSeedColor() ?? Color.Parse(DefaultAccent);
                else
                {
                    try { accent = Color.Parse(_accentHex); } catch { accent = Color.Parse(DefaultAccent); }
                }

                app.Resources["AccentColor"] = accent;
                app.Resources["AccentBrush"] = new SolidColorBrush(accent);

                // Drive Material.Avalonia's live theme: base (light/dark) + primary.
                // Find the MaterialTheme element in the application styles and set
                // its BaseTheme + PrimaryColor/SecondaryColor directly.
                try
                {
                    foreach (var style in app.Styles)
                    {
                        if (style is Material.Styles.Themes.MaterialTheme mt)
                        {
                            // Theme.Create(IBaseTheme, primary, accent) lets us use
                            // an arbitrary accent (not just the named swatches).
                            var baseTheme = _isDark
                                ? Material.Styles.Themes.Theme.Dark
                                : Material.Styles.Themes.Theme.Light;
                            mt.CurrentTheme = Material.Styles.Themes.Theme.Create(baseTheme, accent, accent);
                            break;
                        }
                    }
                }
                catch { /* fall back to Fluent-variant + resource accent only */ }

                ThemeChanged?.Invoke(this, EventArgs.Empty);
            }

            if (Dispatcher.UIThread.CheckAccess()) DoApply();
            else Dispatcher.UIThread.Post(DoApply);
        }
    }

    public sealed class AccentColor
    {
        public string Name { get; }
        public string Hex { get; }
        public AccentColor(string name, string hex) { Name = name; Hex = hex; }
        // For binding a swatch fill directly in XAML.
        public IBrush Brush => new SolidColorBrush(Color.Parse(Hex));
    }
}
