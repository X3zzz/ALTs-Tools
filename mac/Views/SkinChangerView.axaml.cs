using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using AltsTools.ViewModels;

namespace AltsTools.Views;

public partial class SkinChangerView : UserControl
{
    private SkinChangerViewModel? _vm;

    public SkinChangerView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Hook();
    }

    private void Hook()
    {
        if (_vm != null) _vm.StatusAnnounced -= OnStatus;
        _vm = DataContext as SkinChangerViewModel;
        if (_vm != null) _vm.StatusAnnounced += OnStatus;
    }

    // Mirror status-line changes as toasts so actions always give feedback.
    private void OnStatus(string msg)
    {
        bool error = msg.Contains("失败") || msg.Contains("错误") || msg.Contains("登录")
                     || msg.Contains("sign in", System.StringComparison.OrdinalIgnoreCase)
                     || msg.Contains("fail", System.StringComparison.OrdinalIgnoreCase)
                     || msg.Contains("error", System.StringComparison.OrdinalIgnoreCase);
        if (error) Helpers.Toast.Error(msg);
        else Helpers.Toast.Info(msg);
    }
}
