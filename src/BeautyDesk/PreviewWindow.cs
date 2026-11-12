using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace BeautyDesk;

/// <summary>
/// Всплывающее окно с живыми миниатюрами окон программы — появляется над её иконкой в доке.
/// Миниатюры рисует сама Windows (DWM) поверх окна, поэтому это обычное окно WPF, а не веб-страница.
/// </summary>
internal sealed class PreviewWindow : Window
{
    private const double CellWidth = 208, ThumbHeight = 124, MaxWindows = 6, Gap = 8;

    private readonly StackPanel _cells = new() { Orientation = Orientation.Horizontal };
    private readonly List<IntPtr> _thumbnails = [];
    private IntPtr _hwnd;

    /// <summary>Человек кликнул по миниатюре.</summary>
    public event Action<IntPtr>? WindowChosen;
    /// <summary>Человек нажал крестик у миниатюры.</summary>
    public event Action<IntPtr>? WindowCloseRequested;
    /// <summary>Курсор вошёл в окно превью или вышел из него.</summary>
    public event Action<bool>? HoverChanged;

    public PreviewWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = new SolidColorBrush(Color.FromRgb(0x1c, 0x1c, 0x1e));
        Content = new Border { Padding = new Thickness(10), Child = _cells };

        MouseEnter += (_, _) => HoverChanged?.Invoke(true);
        MouseLeave += (_, _) => HoverChanged?.Invoke(false);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
        int round = 2; // скруглённые углы Windows 11
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    /// <param name="windows">Окна программы: описатель и заголовок.</param>
    /// <param name="anchor">Иконка в доке (экранные пиксели) — превью встаёт над ней по центру.</param>
    /// <param name="monitor">Экран, за пределы которого превью не должно выходить.</param>
    public void ShowFor(IReadOnlyList<(IntPtr Handle, string Title)> windows, Native.RECT anchor, MonitorInfo monitor)
    {
        ClearThumbnails();
        _cells.Children.Clear();
        var areas = new List<(IntPtr Handle, Border Area)>();
        foreach (var (handle, title) in windows.Take((int)MaxWindows))
        {
            var area = new Border { Width = CellWidth, Height = ThumbHeight };
            // Крестик стоит в строке заголовка: поверх самой миниатюры Windows рисует картинку окна и закрыла бы его.
            var close = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(4), Background = Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = "\u2715", FontSize = 11, Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            close.MouseEnter += (_, _) => close.Background = new SolidColorBrush(Color.FromRgb(0xc4, 0x2b, 0x1c));
            close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
            close.MouseLeftButtonUp += (_, e) => { e.Handled = true; WindowCloseRequested?.Invoke(handle); };

            var header = new DockPanel { Width = CellWidth, Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(close, System.Windows.Controls.Dock.Right);
            header.Children.Add(close);
            header.Children.Add(new TextBlock
            {
                Text = title, Foreground = Brushes.White, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
            });

            var cell = new Border
            {
                Padding = new Thickness(6),
                Margin = new Thickness(_cells.Children.Count == 0 ? 0 : Gap, 0, 0, 0),
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Child = new StackPanel { Children = { header, area } }
            };
            cell.MouseEnter += (_, _) => cell.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xff, 0xff, 0xff));
            cell.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;
            cell.MouseLeftButtonUp += (_, e) => { e.Handled = true; WindowChosen?.Invoke(handle); };
            _cells.Children.Add(cell);
            areas.Add((handle, area));
        }
        if (areas.Count == 0) { HidePreview(); return; }

        // Сначала переносим окно на нужный экран: от него зависит масштаб, а значит и размеры.
        new WindowInteropHelper(this).EnsureHandle();
        Native.SetWindowPos(_hwnd, IntPtr.Zero, anchor.Left, anchor.Top - 200, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
        Show();
        UpdateLayout();

        Native.GetWindowRect(_hwnd, out var size);
        int gap = (int)Math.Round(10 * monitor.Scale);
        int left = Math.Clamp(anchor.Left + anchor.Width / 2 - size.Width / 2, monitor.Bounds.Left + gap, Math.Max(monitor.Bounds.Left + gap, monitor.Bounds.Right - size.Width - gap));
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, left, anchor.Top - size.Height - gap, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        foreach (var (handle, area) in areas)
        {
            if (Native.DwmRegisterThumbnail(_hwnd, handle, out var thumbnail) != 0) continue;
            _thumbnails.Add(thumbnail);
            Native.DwmQueryThumbnailSourceSize(thumbnail, out var source);

            // Миниатюра вписывается в свою ячейку с сохранением пропорций.
            var origin = area.TransformToAncestor(this).Transform(new Point(0, 0));
            double boxW = CellWidth * scale, boxH = ThumbHeight * scale;
            double k = Math.Min(boxW / Math.Max(1, source.X), boxH / Math.Max(1, source.Y));
            int w = (int)(source.X * k), h = (int)(source.Y * k);
            int x = (int)(origin.X * scale + (boxW - w) / 2), y = (int)(origin.Y * scale + (boxH - h) / 2);
            var props = new Native.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = 0x1 | 0x4 | 0x8, // положение, непрозрачность, видимость
                rcDestination = new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h },
                opacity = 255,
                fVisible = true
            };
            Native.DwmUpdateThumbnailProperties(thumbnail, ref props);
        }
    }

    /// <summary>Находится ли курсор над окном превью прямо сейчас.</summary>
    public bool ContainsCursor()
    {
        if (!IsVisible || !Native.GetCursorPos(out var p) || !Native.GetWindowRect(_hwnd, out var r)) return false;
        return p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    }

    public void HidePreview()
    {
        ClearThumbnails();
        if (IsVisible) Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        ClearThumbnails();
        base.OnClosed(e);
    }

    private void ClearThumbnails()
    {
        foreach (var t in _thumbnails) Native.DwmUnregisterThumbnail(t);
        _thumbnails.Clear();
    }
}
