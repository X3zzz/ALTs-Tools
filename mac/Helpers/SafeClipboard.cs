using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace AltsTools.Helpers
{
    /// <summary>
    /// Avalonia port of the WPF SafeClipboard. Uses the top-level window's
    /// IClipboard; never throws. Get is best-effort (returns "" if unavailable).
    /// </summary>
    public static class SafeClipboard
    {
        private static Avalonia.Input.Platform.IClipboard? Clip
            => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
               ?.MainWindow?.Clipboard;

        public static bool TrySetText(string text)
        {
            try
            {
                var clip = Clip;
                if (clip == null) return false;
                if (Dispatcher.UIThread.CheckAccess())
                    _ = clip.SetTextAsync(text ?? string.Empty);
                else
                    Dispatcher.UIThread.Post(() => _ = clip.SetTextAsync(text ?? string.Empty));
                return true;
            }
            catch { return false; }
        }

        public static void SetText(string text) => TrySetText(text);

        public static string GetText()
        {
            try
            {
                var clip = Clip;
                if (clip == null) return string.Empty;
                return Dispatcher.UIThread.InvokeAsync(async () => await clip.GetTextAsync() ?? "")
                                 .GetAwaiter().GetResult();
            }
            catch { return string.Empty; }
        }

        public static bool ContainsText() => !string.IsNullOrEmpty(GetText());

        public static void Clear() => TrySetText(string.Empty);
    }
}
