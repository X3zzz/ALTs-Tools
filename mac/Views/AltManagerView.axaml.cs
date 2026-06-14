using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using AltsTools.Models;
using AltsTools.ViewModels;

namespace AltsTools.Views;

public partial class AltManagerView : UserControl
{
    private AltManagerViewModel? VM => DataContext as AltManagerViewModel;

    public AltManagerView() => AvaloniaXamlLoader.Load(this);

    private void OnCardClick(object? s, PointerPressedEventArgs e)
    {
        if (s is Control c && c.Tag is ProfileCardItem item)
            item.IsSelected = !item.IsSelected;
    }

    private async void OnActivate(object? s, RoutedEventArgs e)
    {
        if (VM is null || s is not Control c || c.Tag is not ProfileCardItem item) return;
        try
        {
            var progress = new Progress<string>(m => { if (!string.IsNullOrWhiteSpace(m)) Helpers.Toast.Info(m); });
            await VM.ActivateAsync(item.Block, progress);
            item.RaiseAllChanged();
            Helpers.Toast.Success(AltsTools.Localization.Loc.T("AltMgr.Login") + " ✓");
        }
        catch (Exception ex)
        {
            Helpers.Toast.Error(ex.Message);
        }
    }
}
