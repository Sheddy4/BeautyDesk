using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace BeautyDesk;

/// <summary>Bar — верхняя панель, Dock — док, Island — «остров» с музыкой поверх панели, Menu — выпадающее меню, Launcher — лаунчер по клавише Win,
/// Desktop — слой виджетов на рабочем столе, Settings — окно настроек.</summary>
internal enum ShellRole { Bar, Dock, Island, Menu, Launcher, Desktop, Settings }

/// <summary>
/// Одно окно оболочки. Само окно пустое и прозрачное, всё видимое рисует веб-страница внутри WebView2.
/// Где окну стоять, решает <see cref="DesktopHost"/>; панель дополнительно резервирует место
/// вверху экрана, чтобы развёрнутые окна под неё не заезжали.
/// </summary>
internal sealed class ShellWindow : Window
{
    public const string UserWidgetsHost = "userwidgets.beautydesk.example";
    private static readonly uint AppBarCallback = Native.RegisterWindowMessage("BeautyDesk.AppBar");
    private static readonly uint TaskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");

    private readonly WebView2 _web = new() { DefaultBackgroundColor = System.Drawing.Color.Transparent };
    private IntPtr _hwnd;
    private bool _appBarRegistered;
    private Native.RECT _bounds;
    private bool _aboveWindows;
    private bool _blur;
    private uint _tint = Backdrop.DefaultTint;

    public ShellRole Role { get; }
    /// <summary>Экран, на котором окно сейчас стоит. Меню одно на все экраны и переезжает между ними.</summary>
    public MonitorInfo Monitor { get; set; }
    /// <summary>Положение окна на экране в физических пикселях.</summary>
    public Native.RECT Bounds => _bounds;
    public IntPtr Handle => _hwnd;
    /// <summary>Для «острова»: развёрнут ли он сейчас.</summary>
    public bool Expanded { get; set; }

    public event Action<ShellWindow, JsonElement>? MessageReceived;

    // В лаунчере печатают, а окно настроек закрывают клавишей Esc — им, в отличие от остальных окон
    // оболочки, нужен фокус клавиатуры.
    private bool TakesFocus => Role is ShellRole.Launcher or ShellRole.Settings;

    public ShellWindow(ShellRole role, MonitorInfo monitor)
    {
        Role = role;
        Monitor = monitor;

        Title = $"BeautyDesk {role}";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = role != ShellRole.Desktop; // виджеты лежат на рабочем столе, под обычными окнами
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = _web;
    }

    /// <param name="blur">Окно сразу создаётся с размытием (тема с «матовым стеклом»).</param>
    public async Task InitAsync(CoreWebView2Environment environment, Native.RECT bounds, bool startHidden = false, bool blur = false, uint tint = Backdrop.DefaultTint)
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        var source = HwndSource.FromHwnd(_hwnd)!;
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        source.AddHook(WndProc);

        // Окно не попадает в Alt+Tab и (кроме лаунчера) само не просит фокус у программы, с которой работает человек.
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64() | Native.WS_EX_TOOLWINDOW;
        if (!TakesFocus) ex |= Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));

        _blur = blur;
        _tint = tint;
        if (blur) Backdrop.Apply(_hwnd, true, tint);
        else SetSeeThrough(true);
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
        core.SetVirtualHostNameToFolderMapping(UserWidgetsHost, AppPaths.UserWidgets, CoreWebView2HostResourceAccessKind.Allow);
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

    /// <summary>Оставляет от окна только указанные прямоугольники — так слой виджетов пропускает клики между ними.</summary>
    public void SetClipParts(IReadOnlyList<Native.RECT> parts)
    {
        var region = Native.CreateRectRgn(0, 0, 0, 0);
        foreach (var r in parts)
        {
            var part = Native.CreateRectRgn(r.Left, r.Top, r.Right, r.Bottom);
            Native.CombineRgn(region, region, part, Native.RGN_OR);
            Native.DeleteObject(part);
        }
        Native.SetWindowRgn(_hwnd, region, true);
    }

    /// <summary>Включает или выключает размытие того, что под окном (для тем с «матовым стеклом»).</summary>
    public void SetBlur(bool blur, uint tint = Backdrop.DefaultTint)
    {
        if (blur == _blur && tint == _tint) return;
        // Размытие и «прозрачность насквозь» включаются разными средствами и вместе не работают.
        if (blur != _blur) SetSeeThrough(!blur);
        _blur = blur;
        _tint = tint;
        Backdrop.Apply(_hwnd, blur, tint);
    }

    /// <summary>
    /// Режим расстановки виджетов: слой поднимается поверх всех окон, чтобы его было видно целиком.
    /// В обычном режиме он возвращается на рабочий стол.
    /// </summary>
    public void SetAboveWindows(bool above)
    {
        _aboveWindows = above;
        Native.SetWindowPos(_hwnd, above ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
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

    /// <summary>Отдаёт окну клавиатуру — когда в нём нужно что-то напечатать.</summary>
    public void FocusKeyboard()
    {
        Native.SetForegroundWindow(_hwnd);
        _web.Focus();
    }

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

        // Слой виджетов всегда остаётся в самом низу, прямо над рабочим столом: клик по виджету
        // не должен поднимать его поверх окон программ.
        if (msg == Native.WM_WINDOWPOSCHANGING && Role == ShellRole.Desktop && !_aboveWindows)
        {
            var position = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
            position.hwndInsertAfter = JustAboveDesktop();
            position.flags &= ~Native.SWP_NOZORDER;
            Marshal.StructureToPtr(position, lParam, false);
        }
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

    /// <summary>
    /// Окно рабочего стола Windows — то, в котором лежат значки. Обычно это Progman,
    /// но при некоторых обоях значки переезжают в одно из окон WorkerW.
    /// </summary>
    private static IntPtr WindowsDesktop()
    {
        var progman = Native.FindWindow("Progman", null);
        if (Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) return progman;
        var found = progman;
        Native.EnumWindows((hwnd, _) =>
        {
            if (Native.ClassName(hwnd) != "WorkerW" || Native.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Окно, сразу под которым должен лежать слой виджетов: ближайшее над рабочим столом Windows.</summary>
    private IntPtr JustAboveDesktop()
    {
        var above = Native.GetWindow(WindowsDesktop(), Native.GW_HWNDPREV);
        while (above == _hwnd) above = Native.GetWindow(above, Native.GW_HWNDPREV);
        return above != IntPtr.Zero ? above : Native.HWND_BOTTOM;
    }

    /// <summary>
    /// Возвращает слой виджетов на место — прямо над рабочим столом. Нужно вызывать время от времени:
    /// по Win+D Windows поднимает рабочий стол поверх всех окон и закрывает им виджеты,
    /// а при возврате окон опускает обратно.
    /// </summary>
    public void KeepOnDesktop()
    {
        if (Role != ShellRole.Desktop || _aboveWindows || !IsVisible) return;
        if (Native.GetWindow(_hwnd, Native.GW_HWNDNEXT) == WindowsDesktop()) return;
        // Само место в стопке окон подставляется при обработке WM_WINDOWPOSCHANGING.
        Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    // ---------- прозрачность ----------

    /// <summary>Делает окно прозрачным насквозь там, где страница ничего не нарисовала.</summary>
    private void SetSeeThrough(bool on)
    {
        if (!on)
        {
            // Выключение — обычным способом, без области: иначе окно остаётся в непрозрачном состоянии
            // и размытие под ним превращается в чёрную заливку.
            var off = new Native.DWM_BLURBEHIND { dwFlags = Native.DWM_BB_ENABLE, fEnable = false };
            Native.DwmEnableBlurBehindWindow(_hwnd, ref off);
            return;
        }
        // Приём: «размытие» с пустой областью. Размытия нет, зато окно перестаёт быть непрозрачным.
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
