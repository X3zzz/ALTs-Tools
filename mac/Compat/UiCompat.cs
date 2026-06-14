using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace AltsTools.Compat
{
    // ── Enums mirroring System.Windows.* so ported VM code compiles unchanged ──
    public enum MessageBoxButton { OK, OKCancel, YesNo, YesNoCancel }
    public enum MessageBoxImage { None, Information, Warning, Error, Question }
    public enum MessageBoxResult { None, OK, Cancel, Yes, No }

    /// <summary>
    /// Minimal cross-platform replacement for System.Windows.MessageBox. Shows an
    /// Avalonia dialog window. Synchronous Show() blocks via the dispatcher so the
    /// ported (synchronous) call sites keep working.
    /// </summary>
    public static class MessageBox
    {
        public static MessageBoxResult Show(string text)
            => Show(text, "", MessageBoxButton.OK, MessageBoxImage.None);
        public static MessageBoxResult Show(string text, string caption)
            => Show(text, caption, MessageBoxButton.OK, MessageBoxImage.None);
        public static MessageBoxResult Show(string text, string caption, MessageBoxButton button)
            => Show(text, caption, button, MessageBoxImage.None);

        public static MessageBoxResult Show(string text, string caption,
                                            MessageBoxButton button, MessageBoxImage icon)
        {
            // If we're already on the UI thread we can't block synchronously without
            // deadlock; show non-blocking and return OK. Otherwise marshal+wait.
            if (Dispatcher.UIThread.CheckAccess())
            {
                _ = ShowDialogAsync(text, caption, button);
                return MessageBoxResult.OK;
            }
            // Off the UI thread: marshal the dialog onto it and block for the result.
            // InvokeAsync(Func<Task<T>>) unwraps to Task<T>, so one await is enough.
            return Dispatcher.UIThread.InvokeAsync(
                () => ShowDialogAsync(text, caption, button)).GetAwaiter().GetResult();
        }

        public static Task<MessageBoxResult> ShowDialogAsync(
            string text, string caption, MessageBoxButton button)
        {
            var tcs = new TaskCompletionSource<MessageBoxResult>();

            var panel = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
            if (!string.IsNullOrEmpty(caption))
                panel.Children.Add(new TextBlock { Text = caption, FontWeight = Avalonia.Media.FontWeight.Bold });
            panel.Children.Add(new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 420 });

            var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal,
                                           HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
            var win = new Window
            {
                Title = string.IsNullOrEmpty(caption) ? "ALTs Tools" : caption,
                SizeToContent = SizeToContent.WidthAndHeight,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel { Children = { panel, buttons } },
            };

            void Add(string label, MessageBoxResult r)
            {
                var b = new Button { Content = label, MinWidth = 72 };
                b.Click += (_, _) => { tcs.TrySetResult(r); win.Close(); };
                buttons.Children.Add(b);
            }
            switch (button)
            {
                case MessageBoxButton.OKCancel: Add("OK", MessageBoxResult.OK); Add("Cancel", MessageBoxResult.Cancel); break;
                case MessageBoxButton.YesNo: Add("Yes", MessageBoxResult.Yes); Add("No", MessageBoxResult.No); break;
                case MessageBoxButton.YesNoCancel: Add("Yes", MessageBoxResult.Yes); Add("No", MessageBoxResult.No); Add("Cancel", MessageBoxResult.Cancel); break;
                default: Add("OK", MessageBoxResult.OK); break;
            }

            var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (owner != null) win.ShowDialog(owner);
            else win.Show();
            return tcs.Task;
        }
    }
}
