using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using AltsTools.Theming;
using AltsTools.ViewModels;

namespace AltsTools.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? VM => DataContext as SettingsViewModel;

    public SettingsView() => AvaloniaXamlLoader.Load(this);

    private void OnAccentClick(object? s, RoutedEventArgs e)
    {
        if (VM is not null && s is Control c && c.Tag is AccentColor accent)
            VM.SelectedAccent = accent;
    }
}
