using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using AltsTools.Services;

namespace AltsTools.Views;

public partial class TokenInjectorView : UserControl
{
    private List<Helper.JavaProc> _procs = new();
    private ListBox? _procList;
    private TextBox? _tokenBox;
    private TextBlock? _status;

    public TokenInjectorView()
    {
        AvaloniaXamlLoader.Load(this);
        _procList = this.FindControl<ListBox>("ProcList");
        _tokenBox = this.FindControl<TextBox>("TokenBox");
        _status   = this.FindControl<TextBlock>("Status");
        LoadProcs();
    }

    private void LoadProcs()
    {
        _procs = Helper.GetJavaProcesses();
        var items = new List<string>();
        foreach (var p in _procs) items.Add(p.Display);
        if (_procList != null)
        {
            _procList.ItemsSource = items;
            _procList.SelectedIndex = items.Count > 0 ? 0 : -1;
        }
    }

    private void OnRefresh(object? s, RoutedEventArgs e) => LoadProcs();

    private void Say(string msg, Helpers.ToastKind? toast = null)
    {
        if (_status != null) _status.Text = msg;
        if (toast.HasValue) Helpers.Toast.Show(msg, toast.Value);
    }

    // Inject if needed, then poll up to ~5s for the payload to report its port.
    // Supports "inject once, swap many": once a pid is known (this session, a
    // persisted map, or a live resident payload) we never re-inject — so you
    // don't have to restart Minecraft between swaps.
    private async Task<bool> EnsureInjectedAsync(int pid)
    {
        if (TokenInjectionService.PidPortMap.ContainsKey(pid)) return true;

        // A payload from a previous run may still be resident in this pid.
        // Recover its port from the persisted map and verify it's alive.
        if (await TokenInjectionService.TryRecoverResidentAsync(pid)) return true;

        Say($"正在注入 pid {pid}…", Helpers.ToastKind.Info);
        bool ok = TokenInjectionService.InjectDll(pid, Helper.tmpFileName);
        if (!ok) { Say("注入失败。", Helpers.ToastKind.Error); return false; }

        // The payload needs time to attach the JVM, defineClass, start its HTTP
        // server and POST /client/online back. Poll instead of a fixed wait.
        for (int i = 0; i < 25; i++)   // ~5s
        {
            if (TokenInjectionService.PidPortMap.ContainsKey(pid)) return true;
            await Task.Delay(200);
        }
        string detail = TokenInjectionService.LastInjectorOutput;
        Say("注入器已运行，但 payload 未回连（确认选的是运行中的 Minecraft 游戏进程、且 SIP 已关闭）。\n"
            + "injector 输出：\n" + detail, Helpers.ToastKind.Error);
        return false;
    }

    private async void OnInject(object? s, RoutedEventArgs e)
    {
        int idx = _procList?.SelectedIndex ?? -1;
        if (idx < 0 || idx >= _procs.Count) { Say("请先选择一个进程。", Helpers.ToastKind.Error); return; }
        int pid = _procs[idx].Pid;
        if (await EnsureInjectedAsync(pid))
            Say($"进程 {pid} 已就绪，粘贴 token 后点「注入令牌」。", Helpers.ToastKind.Success);
    }

    private async void OnSwap(object? s, RoutedEventArgs e)
    {
        int idx = _procList?.SelectedIndex ?? -1;
        if (idx < 0 || idx >= _procs.Count) { Say("请先选择一个进程。", Helpers.ToastKind.Error); return; }
        int pid = _procs[idx].Pid;
        string token = _tokenBox?.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(token)) { Say("请先粘贴 access token。", Helpers.ToastKind.Error); return; }

        // Auto-inject if this process isn't injected yet — no separate click needed.
        if (!await EnsureInjectedAsync(pid)) return;

        if (!TokenInjectionService.PidPortMap.TryGetValue(pid, out int port))
        {
            Say("尚未回连，请稍候重试。", Helpers.ToastKind.Error); return;
        }
        Say("正在注入令牌…", Helpers.ToastKind.Info);
        await TokenInjectionService.SendSwapTokenAsync(port, token);   // shows its own toast via Notify
    }
}
