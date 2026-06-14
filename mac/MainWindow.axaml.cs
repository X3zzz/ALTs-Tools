using System;
using System.Net;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AltsTools.Services;

namespace AltsTools;

public partial class MainWindow : Window
{
    // The injection HTTP listener — same port the Windows app used (38964).
    // Injected payloads POST /client/online here to register their port.
    private static readonly HttpListener _listener = new();

    public MainWindow()
    {
        InitializeComponent();
        Helpers.Toast.Install(this);         // overlay snackbar host
        Helper.ExtractInjectionDll();        // unpack payload.dylib
        // Route injection-service notifications to the snackbar.
        TokenInjectionService.Notify = (msg, isError) =>
        {
            if (isError) Helpers.Toast.Error(msg);
            else Helpers.Toast.Success(msg);
        };
        StartInjectionListener();
        Closing += (_, _) => StopInjectionListener();

        // Switch pages from the nav rail selection.
        var nav = this.FindControl<ListBox>("NavList");
        if (nav != null)
            nav.SelectionChanged += (_, _) => ShowPage(nav.SelectedIndex);
        ShowPage(nav?.SelectedIndex ?? 0);
    }

    private void ShowPage(int index)
    {
        Control?[] pages =
        {
            this.FindControl<Control>("PageConverter"),
            this.FindControl<Control>("PageAltManager"),
            this.FindControl<Control>("PageInjector"),
            this.FindControl<Control>("PageSkin"),
            this.FindControl<Control>("PageSettings"),
        };
        for (int i = 0; i < pages.Length; i++)
        {
            var p = pages[i];
            if (p == null) continue;
            bool show = i == index;
            if (show && !p.IsVisible)
            {
                // Material emphasized entrance: start hidden+offset, then settle.
                p.Classes.Add("entering");
                p.IsVisible = true;
                Dispatcher.UIThread.Post(() => p.Classes.Remove("entering"),
                                         DispatcherPriority.Render);
            }
            else
            {
                p.IsVisible = show;
            }
        }

        // When the Skin page (玩家档案) is opened, lazy-load the profile — the
        // original WPF app did this on tab activation. Without it the page stays
        // empty and its authenticated buttons never enable.
        if (index == 3 && DataContext is ViewModels.MainViewModel mvm)
            _ = mvm.SkinChanger.EnsureLoadedAsync();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void StartInjectionListener()
    {
        try
        {
            if (_listener.IsListening) return;
            // The injected payload connects to 127.0.0.1 (IPv4). On macOS,
            // "localhost" makes HttpListener bind ::1 (IPv6) only, so the IPv4
            // connection was refused ("host not reachable"). Bind IPv4 too.
            _listener.Prefixes.Add("http://127.0.0.1:38964/");
            _listener.Prefixes.Add("http://localhost:38964/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    try
                    {
                        var ctx = await _listener.GetContextAsync();
                        _ = TokenInjectionService.HandleRequestAsync(ctx);
                    }
                    catch { /* listener stopped */ break; }
                }
            });
        }
        catch { /* port busy / already running */ }
    }

    private void StopInjectionListener()
    {
        try { if (_listener.IsListening) _listener.Stop(); } catch { }
    }
}
