using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using Material.Icons.Avalonia;

namespace AltsTools.Helpers
{
    public enum ToastKind { Info, Success, Error }

    /// <summary>
    /// Lightweight Material You snackbar/toast. A single host is installed over
    /// the main window's content; Show() pops a pill at the bottom that fades +
    /// slides in, holds, then fades out. Thread-safe (marshals to UI thread).
    /// </summary>
    public static class Toast
    {
        private static StackPanel? _host;

        /// <summary>
        /// Install the toast host into the window's OverlayLayer (a built-in
        /// adorner surface every TopLevel has), so we never reparent the window
        /// content tree. Call once after the window is opened.
        /// </summary>
        public static void Install(Window window)
        {
            void Attach()
            {
                var overlay = OverlayLayer.GetOverlayLayer(window);
                if (overlay == null) return;
                var stack = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 28),
                    Spacing = 8,
                    IsHitTestVisible = false,
                };
                overlay.Children.Add(stack);
                _host = stack;
            }

            // OverlayLayer exists once the window is opened/templated.
            if (window.IsLoaded) Attach();
            else window.Opened += (_, _) => Attach();
        }

        public static void Info(string msg)    => Show(msg, ToastKind.Info);
        public static void Success(string msg) => Show(msg, ToastKind.Success);
        public static void Error(string msg)   => Show(msg, ToastKind.Error);

        public static void Show(string message, ToastKind kind = ToastKind.Info)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            if (Dispatcher.UIThread.CheckAccess()) Pop(message, kind);
            else Dispatcher.UIThread.Post(() => Pop(message, kind));
        }

        private static void Pop(string message, ToastKind kind)
        {
            if (_host == null) return;

            var (icon, accent) = kind switch
            {
                ToastKind.Success => (MaterialIconKind.CheckCircle, Color.Parse("#4CAF50")),
                ToastKind.Error   => (MaterialIconKind.AlertCircle, Color.Parse("#E53935")),
                _                 => (MaterialIconKind.Information, Color.Parse("#6750A4")),
            };

            var pill = new Border
            {
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(18, 12),
                MaxWidth = 520,
                Background = ResolveBrush("M3SurfaceContainerHigh", Color.Parse("#2B2930")),
                BoxShadow = new BoxShadows(new BoxShadow
                {
                    OffsetX = 0, OffsetY = 4, Blur = 18, Color = Color.Parse("#40000000")
                }),
                Opacity = 0,
                RenderTransform = new TranslateTransform(0, 16),
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new MaterialIcon { Kind = icon, Width = 20, Height = 20, Foreground = new SolidColorBrush(accent) });
            row.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ResolveBrush("M3OnSurface", Colors.White),
            });
            pill.Child = row;
            _host.Children.Add(pill);

            // enter
            pill.Transitions = new Transitions
            {
                new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new CubicEaseOut() },
            };
            Dispatcher.UIThread.Post(() =>
            {
                pill.Opacity = 1;
                if (pill.RenderTransform is TranslateTransform t) t.Y = 0;
            }, DispatcherPriority.Render);

            // auto-dismiss
            _ = DismissLater(pill, kind == ToastKind.Error ? 4200 : 2600);
        }

        private static async Task DismissLater(Border pill, int holdMs)
        {
            await Task.Delay(holdMs);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                pill.Opacity = 0;
            });
            await Task.Delay(260);
            await Dispatcher.UIThread.InvokeAsync(() => _host?.Children.Remove(pill));
        }

        private static IBrush ResolveBrush(string key, Color fallback)
        {
            var app = Application.Current;
            if (app != null && app.TryGetResource(key, app.ActualThemeVariant, out var v) && v is IBrush b)
                return b;
            return new SolidColorBrush(fallback);
        }
    }
}
