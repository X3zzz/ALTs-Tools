using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using AltsTools.ViewModels;

namespace AltsTools.Views;

public partial class TokenConverterView : UserControl
{
    private TokenConverterViewModel? VM => DataContext as TokenConverterViewModel;

    public TokenConverterView() => AvaloniaXamlLoader.Load(this);

    private async void OnConvert(object? s, RoutedEventArgs e)
    {
        if (VM is null) return;
        var progress = new Progress<string>(msg => VM.StatusMessage = msg);
        if (VM.IsCookieMode) await VM.ConvertCookieAsync(progress);
        else await VM.ConvertAsync(progress);
    }

    private void OnPaste(object? s, RoutedEventArgs e) => VM?.PasteRefreshToken();
    private void OnClearRefresh(object? s, RoutedEventArgs e) => VM?.ClearRefreshToken();

    private void OnCopyAccess(object? s, RoutedEventArgs e)
    {
        if (VM is null) return;
        VM.CopyAccessToken();
        Helpers.Toast.Success(AltsTools.Localization.Loc.T("AltMgr.Copied"));
    }

    private void OnCheckExpiry(object? s, RoutedEventArgs e) => VM?.CheckExpiry();
}
