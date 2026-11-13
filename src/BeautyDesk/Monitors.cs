using System.Runtime.InteropServices;

namespace BeautyDesk;

/// <summary>Один экран: границы в физических пикселях и масштаб (1.0 = 100 %).</summary>
/// <param name="Device">Системное имя экрана — по нему запоминается раскладка виджетов.</param>
internal sealed record MonitorInfo(IntPtr Handle, int Index, Native.RECT Bounds, double Scale, bool IsPrimary, string Device);

internal static class Monitors
{
    public static IReadOnlyList<MonitorInfo> All()
    {
        var list = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(handle, ref info))
            {
                Native.GetDpiForMonitor(handle, 0, out var dpi, out _);
                list.Add(new MonitorInfo(handle, 0, info.rcMonitor, dpi == 0 ? 1.0 : dpi / 96.0, (info.dwFlags & 1) != 0, info.szDevice ?? ""));
            }
            return true;
        }, IntPtr.Zero);

        // Основной экран первым, остальные слева направо — порядок стабилен между запусками.
        return list
            .OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top)
            .Select((m, i) => m with { Index = i })
            .ToList();
    }
}
