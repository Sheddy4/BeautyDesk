using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace BeautyDesk;

/// <summary>Bar — верхняя панель, Dock — док, Island — «остров» с музыкой поверх панели, Menu — выпадающее меню, Launcher — лаунчер по клавише Win.</summary>
internal enum ShellRole { Bar, Dock, Island, Menu, Launcher }

/// <summary>
/// Одно окно оболочки. Само окно пустое и прозрачное, всё видимое рисует веб-страница внутри WebView2.
/// Где окну стоять, решает <see cref="DesktopHost"/>; панель дополнительно резервирует место
/// вверху экрана, чтобы развёрнутые окна под неё не заезжали.
/// </summary>
internal sealed class ShellWindow : Window
{
    private static readonly uint AppBarCallback = Native.RegisterWindowMessage("BeautyDesk.AppBar");
    private static readonly uint TaskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");

    private readonly WebView2 _web = new() { DefaultBackgroundColor = System.Drawing.Color.Transparent };
    private IntPtr _hwnd;
    private bool _appBarRegistered;
    private Native.RECT _bounds;

    public ShellRole Role { get; }
    /// <summary>Экран, на котором окно сейчас стоит. Меню одно на все экраны и переезжает между ними.</summary>
    public MonitorInfo Monitor { get; set; }
    /// <summary>Положение окна на экране в физических пикселях.</summary>
    public Native.RECT Bounds => _bounds;
    /// <summary>Для «острова»: развёрнут ли он сейчас.</summary>
    public bool Expanded { get; set; }

    public event Action<ShellWindow, JsonElement>? MessageReceived;

    // В лаунчере печатают, поэтому ему, в отличие от остальных окон оболочки, нужен фокус клавиатуры.
    private bool TakesFocus => Role == ShellRole.Launcher;

    public ShellWindow(ShellRole role, MonitorInfo monitor)
    {
        Role = role;
        Monitor = monitor;

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

    public async Task InitAsync(CoreWebView2Environment environment, Native.RECT bounds, bool startHidden = false)
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        var source = HwndSource.FromHwnd(_hwnd)!;
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        source.AddHook(WndProc);

        // Окно не попадает в Alt+Tab и (кроме лаунчера) само не просит фокус у программы, с которой работает человек.
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64() | Native.WS_EX_TOOLWINDOW;
        if (!TakesFocus) ex |= Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));

        MakeTransparent();
        _bounds = bounds;
        if (!startHidden)
        {
            Show();
            SetBounds(bounds);
        }
        else
        {
            // WebView2 создаётся только в показанном окне: показываем точкой за краем экрана и потом прячем.
            Show();
            Native.SetWindowPos(_hwnd, IntPtr.Zero, -32000, -32000, 1, 1, Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
        }

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

        if (startHidden) Hide();
    }

    public void Post(string json) => _web.CoreWebView2?.PostWebMessageAsJson(json);

    /// <summary>Ставит окно в указанное место экрана (физические пиксели).</summary>
    public void SetBounds(Native.RECT bounds)
    {
        _bounds = bounds;
        if (!IsVisible) return; // спрятанное окно встанет на место при показе
        if (Role == ShellRole.Bar) ReserveTopEdge(ref _bounds);
        Native.SetWindowPos(_hwnd, IntPtr.Zero, _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height,
            Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
    }

    /// <summary>Показывает или прячет окно, не трогая фокус.</summary>
    public void SetShown(bool shown)
    {
        if (shown == IsVisible) return;
        if (shown)
        {
            Show();
            SetBounds(_bounds);
        }
        else Hide();
    }

    /// <summary>Показывает окно и отдаёт ему клавиатуру (лаунчер).</summary>
    public void ShowFocused()
    {
        Show();
        SetBounds(_bounds);
        Native.SetForegroundWindow(_hwnd);
        Activate();
        _web.Focus();
    }

    /// <summary>
    /// Оставляет от окна только указанную часть (в пикселях от его левого верхнего угла): остальное
    /// не рисуется и пропускает клики насквозь. null — окно целиком.
    /// </summary>
    public void SetClip(Native.RECT? part)
    {
        // Windows забирает область себе, удалять её после вызова не нужно.
        var region = part is { } r ? Native.CreateRectRgn(r.Left, r.Top, r.Right, r.Bottom) : IntPtr.Zero;
        Native.SetWindowRgn(_hwnd, region, true);
    }

    /// <summary>
    /// Делает окно активным. Нужно при нажатии на иконку трея: программы вроде Telegram сворачивают
    /// своё окно, только если нажатие в трее отобрало у них активность, как это делает панель задач Windows.
    /// </summary>
    public void TakeForeground() => Native.SetForegroundWindow(_hwnd);

    /// <summary>Поднимает окно над остальными окнами оболочки.</summary>
    public void Raise() =>
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

    protected override void OnClosed(EventArgs e)
    {
        UnregisterAppBar();
        _web.Dispose();
        base.OnClosed(e);
    }

    // ---------- место вверху экрана ----------

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
        // Окно попало на экран с другим масштабом: Windows при этом сама пересчитывает его размер.
        // Наши размеры уже посчитаны под нужный экран, поэтому возвращаем их на место.
        if (msg == Native.WM_DPICHANGED) Dispatcher.BeginInvoke(() => SetBounds(_bounds));
        if (Role != ShellRole.Bar) return IntPtr.Zero;
        if ((uint)msg == AppBarCallback && wParam.ToInt32() == Native.ABN_POSCHANGED)
        {
            SetBounds(_bounds);
        }
        else if ((uint)msg == TaskbarCreated)
        {
            // Проводник перезапустился и забыл про наше место вверху экрана — занимаем заново.
            _appBarRegistered = false;
            SetBounds(_bounds);
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
