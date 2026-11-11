using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace BeautyDesk;

internal enum ShellRole { Bar, Dock }

/// <summary>
/// Одно окно оболочки на одном экране: верхняя панель или док.
/// Само окно пустое и прозрачное, всё видимое рисует веб-страница внутри WebView2.
/// Панель дополнительно резервирует место вверху экрана, чтобы развёрнутые окна под неё не заезжали.
/// </summary>
internal sealed class ShellWindow : Window
{
    private const double DockBottomMargin = 8;
    private static readonly uint AppBarCallback = Native.RegisterWindowMessage("BeautyDesk.AppBar");
    private static readonly uint TaskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");

    private readonly WebView2 _web = new() { DefaultBackgroundColor = System.Drawing.Color.Transparent };
    private IntPtr _hwnd;
    private bool _appBarRegistered;
    private Theme _theme;

    public ShellRole Role { get; }
    public MonitorInfo Monitor { get; }
    public event Action<ShellWindow, JsonElement>? MessageReceived;

    public ShellWindow(ShellRole role, MonitorInfo monitor, Theme theme)
    {
        Role = role;
        Monitor = monitor;
        _theme = theme;

        Title = $"BeautyDesk {role}";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = _web;
    }

    public async Task InitAsync(CoreWebView2Environment environment)
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        var source = HwndSource.FromHwnd(_hwnd)!;
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        source.AddHook(WndProc);

        // Окно не попадает в Alt+Tab и не забирает фокус у программы, с которой работает человек.
        var ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));

        MakeTransparent();
        Place();
        Show();
        Place(); // WPF при первом показе может пересчитать размер — ставим окончательно

        await _web.EnsureCoreWebView2Async(environment);
        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
#endif
        core.SetVirtualHostNameToFolderMapping("app.beautydesk.example", AppPaths.Ui, CoreWebView2HostResourceAccessKind.Deny);
        // Папки тем открыты для страницы оболочки: без этого WebView2 не отдаёт ей стили и шрифты с другого адреса.
        core.SetVirtualHostNameToFolderMapping(Theme.BuiltInHost, AppPaths.BuiltInThemes, CoreWebView2HostResourceAccessKind.Allow);
        core.SetVirtualHostNameToFolderMapping(Theme.UserHost, AppPaths.UserThemes, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += (_, e) =>
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            MessageReceived?.Invoke(this, doc.RootElement.Clone());
        };
        core.Navigate($"https://app.beautydesk.example/index.html?role={Role.ToString().ToLowerInvariant()}");
    }

    public void Post(string json) => _web.CoreWebView2?.PostWebMessageAsJson(json);

    public void ApplyTheme(Theme theme)
    {
        _theme = theme;
        Place();
    }

    /// <summary>Прячет окно, пока на этом экране открыто полноэкранное приложение.</summary>
    public void SetSuppressed(bool suppressed)
    {
        if (suppressed == !IsVisible) return;
        if (suppressed) Hide();
        else { Show(); Place(); }
    }

    protected override void OnClosed(EventArgs e)
    {
        UnregisterAppBar();
        _web.Dispose();
        base.OnClosed(e);
    }

    // ---------- положение на экране ----------

    private int Px(double dip) => (int)Math.Round(dip * Monitor.Scale);

    private void Place()
    {
        var m = Monitor.Bounds;
        if (Role == ShellRole.Bar)
        {
            var rect = new Native.RECT { Left = m.Left, Top = m.Top, Right = m.Right, Bottom = m.Top + Px(_theme.BarHeight) };
            ReserveTopEdge(ref rect);
            Move(rect);
        }
        else
        {
            int w = Math.Min(Px(_theme.DockWidth), m.Width), h = Px(_theme.DockHeight);
            int left = m.Left + (m.Width - w) / 2, top = m.Bottom - h - Px(DockBottomMargin);
            Move(new Native.RECT { Left = left, Top = top, Right = left + w, Bottom = top + h });
        }
    }

    private void Move(Native.RECT r) =>
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height, Native.SWP_NOACTIVATE);

    private void ReserveTopEdge(ref Native.RECT rect)
    {
        var data = new Native.APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<Native.APPBARDATA>(),
            hWnd = _hwnd,
            uCallbackMessage = AppBarCallback,
            uEdge = Native.ABE_TOP,
            rc = rect
        };
        if (!_appBarRegistered)
        {
            Native.SHAppBarMessage(Native.ABM_NEW, ref data);
            _appBarRegistered = true;
        }
        int height = rect.Height;
        Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref data);
        data.rc.Bottom = data.rc.Top + height;
        Native.SHAppBarMessage(Native.ABM_SETPOS, ref data);
        rect = data.rc;
    }

    private void UnregisterAppBar()
    {
        if (!_appBarRegistered) return;
        var data = new Native.APPBARDATA { cbSize = (uint)Marshal.SizeOf<Native.APPBARDATA>(), hWnd = _hwnd };
        Native.SHAppBarMessage(Native.ABM_REMOVE, ref data);
        _appBarRegistered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (Role != ShellRole.Bar) return IntPtr.Zero;
        if ((uint)msg == AppBarCallback && wParam.ToInt32() == Native.ABN_POSCHANGED)
        {
            Place();
        }
        else if ((uint)msg == TaskbarCreated)
        {
            // Проводник перезапустился и забыл про наше место вверху экрана — занимаем заново.
            _appBarRegistered = false;
            Place();
        }
        return IntPtr.Zero;
    }

    // ---------- прозрачность ----------

    private void MakeTransparent()
    {
        // Пустая область размытия делает окно насквозь прозрачным там, где страница ничего не нарисовала.
        var region = Native.CreateRectRgn(0, 0, -1, -1);
        var blur = new Native.DWM_BLURBEHIND
        {
            dwFlags = Native.DWM_BB_ENABLE | Native.DWM_BB_BLURREGION,
            fEnable = true,
            hRgnBlur = region
        };
        Native.DwmEnableBlurBehindWindow(_hwnd, ref blur);
        Native.DeleteObject(region);
    }
}
