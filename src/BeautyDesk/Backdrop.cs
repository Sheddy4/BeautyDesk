using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace BeautyDesk;

/// <summary>
/// Размытие того, что находится под окном («матовое стекло»). Тема включает его строкой
/// "backdrop": "blur" в theme.json.
/// Используется политика акцента DWM: штатные подложки Windows 11 (Mica, Acrylic) гаснут
/// у неактивных окон, а окна оболочки активными не бывают.
/// </summary>
internal static class Backdrop
{
    private const int WCA_ACCENT_POLICY = 19, ACCENT_DISABLED = 0, ACCENT_ACRYLIC_BLUR = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENT_POLICY { public int State, Flags; public uint GradientColor; public int AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWCOMPOSITIONATTRIBDATA { public int Attribute; public IntPtr Data; public int Size; }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

    /// <summary>Оттенок стекла по умолчанию — тёмно-серый.</summary>
    public const uint DefaultTint = 0x202020;

    /// <param name="tint">Оттенок стекла, 0xBBGGRR. Слишком тёмный (близкий к чёрному) превращает размытие
    /// в сплошную чёрную заливку — так устроено размытие Windows.</param>
    public static void Apply(IntPtr hwnd, bool blur, uint tint = DefaultTint)
    {
        // Непрозрачность оттенка небольшая и постоянная: насыщенность стеклу добавляет сама страница.
        var policy = new ACCENT_POLICY { State = blur ? ACCENT_ACRYLIC_BLUR : ACCENT_DISABLED, Flags = 2, GradientColor = 0x30000000 | (tint & 0xFFFFFF) };
        int size = Marshal.SizeOf<ACCENT_POLICY>();
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, memory, false);
            var data = new WINDOWCOMPOSITIONATTRIBDATA { Attribute = WCA_ACCENT_POLICY, Data = memory, Size = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
}

/// <summary>
/// Размытая подложка дока. Окно дока намного больше самого дока (в нём запас под увеличенные иконки),
/// и размыть только нужную его часть нельзя — размытие заполняет окно целиком. Поэтому под док
/// подкладывается отдельное пустое окно по размеру его подложки. Скругление у него системное,
/// поэтому тема с размытием должна задавать доку такое же: --bd-radius: 8px.
/// </summary>
internal sealed class BackdropWindow : Window
{
    private const long WS_EX_TRANSPARENT = 0x20;
    private const int DWMWA_BORDER_COLOR = 34;
    private IntPtr _hwnd;

    private uint _tint = Backdrop.DefaultTint;

    /// <summary>Оттенок стекла меняется вместе с темой.</summary>
    public void SetTint(uint tint)
    {
        if (tint == _tint) return;
        _tint = tint;
        if (_hwnd != IntPtr.Zero) Backdrop.Apply(_hwnd, true, tint);
    }

    public BackdropWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)!.CompositionTarget.BackgroundColor = Colors.Transparent;
        // Клики проходят сквозь подложку: их принимает окно дока над ней.
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | WS_EX_TRANSPARENT));
        Backdrop.Apply(_hwnd, true, _tint);
        // Углы скругляет сама Windows 11 (радиус 8 px). Обрезать окно по своей форме нельзя:
        // обрезанное окно Windows перестаёт размывать и заливает чёрным.
        int round = 2, noBorder = unchecked((int)0xFFFFFFFE);
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        Native.DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
    }

    /// <summary>Ставит подложку под док. null — убрать.</summary>
    /// <param name="bounds">Место на экране в пикселях.</param>
    /// <param name="under">Окно дока: подложка должна лежать сразу под ним.</param>
    public void Update(Native.RECT? bounds, IntPtr under)
    {
        if (bounds is not { } r || r.Width <= 0 || r.Height <= 0)
        {
            if (IsVisible) Hide();
            return;
        }
        if (!IsVisible) Show();
        Native.SetWindowPos(_hwnd, under, r.Left, r.Top, r.Width, r.Height, Native.SWP_NOACTIVATE);
    }
}
