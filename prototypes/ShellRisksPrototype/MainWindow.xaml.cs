// ПРОТОТИП (этап 1, Sheddy4/BeautyDesk#2). Одноразовый код: без тестов, без обработки ошибок.
// Вопрос: получается ли на Windows 11 при работающем explorer.exe
//   1) показать иконки трея чужих программ в своём окне с рабочими кликами,
//   2) показать живые превью чужих окон и переключаться на них.
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell;
using ManagedShell.WindowsTasks;
using ManagedShell.WindowsTray;

namespace ShellRisksPrototype;

public partial class MainWindow : Window
{
    private readonly ShellManager _shell;
    private readonly Window _preview;
    private IntPtr _previewHwnd;
    private IntPtr _thumb;
    private readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "prototype-log.txt");

    public MainWindow()
    {
        InitializeComponent();
        File.WriteAllText(_logPath, "");

        _shell = new ShellManager(ShellManager.DefaultShellConfig);
        TrayList.ItemsSource = _shell.NotificationArea.TrayIcons;
        TaskList.ItemsSource = _shell.Tasks.GroupedWindows;

        _preview = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            ShowActivated = false, Topmost = true, Width = 360, Height = 230, Background = Brushes.Black
        };
        _previewHwnd = new WindowInteropHelper(_preview).EnsureHandle();

        Closed += (_, _) => { _preview.Close(); _shell.Dispose(); };
        Loaded += (_, _) =>
        {
            Log($"Старт. Трей не поднялся (IsFailed): {_shell.NotificationArea.IsFailed}");
            if (Environment.GetCommandLineArgs().Contains("--selftest"))
            {
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                t.Tick += (_, _) => { t.Stop(); SelfTest(); };
                t.Start();
            }
        };
    }

    private void Log(string s)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {s}";
        LogBox.AppendText(line + "\n");
        LogBox.ScrollToEnd();
        File.AppendAllText(_logPath, line + "\n");
    }

    // ---------- Трей ----------

    private static uint CursorParam()
    {
        GetCursorPos(out var p);
        return (uint)((p.Y << 16) | (p.X & 0xFFFF));
    }

    private static NotifyIcon IconOf(object sender) => (NotifyIcon)((FrameworkElement)sender).DataContext;

    private void Tray_MouseEnter(object sender, MouseEventArgs e)
    {
        var el = (FrameworkElement)sender;
        var icon = IconOf(sender);
        // Сообщаем программе, где на экране её иконка, чтобы меню открылось рядом.
        var tl = el.PointToScreen(new Point(0, 0));
        var br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
        icon.Placement = new ManagedShell.Interop.NativeMethods.Rect
        {
            Left = (int)tl.X, Top = (int)tl.Y, Right = (int)br.X, Bottom = (int)br.Y
        };
        icon.IconMouseEnter(CursorParam());
    }

    private void Tray_MouseLeave(object sender, MouseEventArgs e) => IconOf(sender).IconMouseLeave(CursorParam());
    private void Tray_MouseMove(object sender, MouseEventArgs e) => IconOf(sender).IconMouseMove(CursorParam());

    private void Tray_MouseDown(object sender, MouseButtonEventArgs e)
    {
        IconOf(sender).IconMouseDown(e.ChangedButton, CursorParam(), (int)GetDoubleClickTime());
        e.Handled = true;
    }

    private void Tray_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var icon = IconOf(sender);
        icon.IconMouseUp(e.ChangedButton, CursorParam(), (int)GetDoubleClickTime());
        Log($"Трей: {e.ChangedButton} клик -> \"{icon.Title}\" ({Path.GetFileName(icon.Path)})");
        e.Handled = true;
    }

    // ---------- Окна и превью ----------

    private static ApplicationWindow WinOf(object sender) => (ApplicationWindow)((FrameworkElement)sender).DataContext;

    private void Task_MouseEnter(object sender, MouseEventArgs e)
    {
        var el = (FrameworkElement)sender;
        var w = WinOf(sender);
        var dpi = VisualTreeHelper.GetDpi(this);
        var pos = el.PointToScreen(new Point(0, el.ActualHeight));
        _preview.Left = pos.X / dpi.DpiScaleX;
        _preview.Top = pos.Y / dpi.DpiScaleY + 4;
        _preview.Show();
        var hr = ShowThumb(w.Handle, out var src);
        Log($"Превью \"{w.Title}\": hr=0x{hr:X8}, размер источника {src.Width}x{src.Height}, свёрнуто={w.IsMinimized}");
    }

    private void Task_MouseLeave(object sender, MouseEventArgs e)
    {
        DropThumb();
        _preview.Hide();
    }

    private void Task_Click(object sender, MouseButtonEventArgs e)
    {
        var w = WinOf(sender);
        DropThumb();
        _preview.Hide();
        w.BringToFront();
        Log($"Переключение на \"{w.Title}\"");
    }

    private int ShowThumb(IntPtr source, out SIZE src)
    {
        DropThumb();
        src = default;
        int hr = DwmRegisterThumbnail(_previewHwnd, source, out _thumb);
        if (hr != 0) return hr;
        DwmQueryThumbnailSourceSize(_thumb, out src);

        GetClientRect(_previewHwnd, out var client);
        // Вписываем с сохранением пропорций.
        double k = Math.Min((double)client.Right / Math.Max(1, src.Width), (double)client.Bottom / Math.Max(1, src.Height));
        int w = (int)(src.Width * k), h = (int)(src.Height * k);
        int x = (client.Right - w) / 2, y = (client.Bottom - h) / 2;
        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = 0x1 | 0x4 | 0x8, // RECTDESTINATION | OPACITY | VISIBLE
            rcDestination = new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h },
            opacity = 255,
            fVisible = true
        };
        return DwmUpdateThumbnailProperties(_thumb, ref props);
    }

    private void DropThumb()
    {
        if (_thumb != IntPtr.Zero) { DwmUnregisterThumbnail(_thumb); _thumb = IntPtr.Zero; }
    }

    // ---------- Самопроверка без человека: dotnet run -- --selftest ----------

    private void SelfTest()
    {
        var icons = _shell.NotificationArea.TrayIcons.ToList();
        Log($"SELFTEST иконок трея: {icons.Count}");
        foreach (var i in icons)
            Log($"  трей: \"{i.Title}\" exe={Path.GetFileName(i.Path)} иконка={(i.Icon != null ? "есть" : "НЕТ")} скрыта={i.IsHidden}");

        var wins = _shell.Tasks.GroupedWindows.Cast<ApplicationWindow>().ToList();
        Log($"SELFTEST окон: {wins.Count}");
        _preview.Show();
        foreach (var w in wins)
        {
            var hr = ShowThumb(w.Handle, out var src);
            Log($"  окно: \"{w.Title}\" exe={Path.GetFileName(w.WinFileName)} uwp={w.IsUWP} свёрнуто={w.IsMinimized} превью hr=0x{hr:X8} {src.Width}x{src.Height}");
        }
        DropThumb();
        Log("SELFTEST конец");
        Close();
    }

    // ---------- P/Invoke ----------

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
}
