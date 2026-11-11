using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace BeautyDesk;

/// <summary>
/// Всё, что BeautyDesk меняет в самой Windows: панель задач, значки рабочего стола, обои.
/// Перед первым изменением исходное состояние записывается в файл восстановления,
/// поэтому вернуть всё назад может и основной процесс, и сторож после падения.
/// </summary>
internal static class ShellState
{
    private sealed record Saved(int OwnerPid, bool TaskbarAutoHide, string? Wallpaper);

    /// <summary>Прячет панель задач и (если нужно) значки рабочего стола.</summary>
    public static void Hide(bool hideDesktopIcons)
    {
        if (Load() is null)
            Save(new Saved(Environment.ProcessId, GetTaskbarAutoHide(), null));

        // Автоскрытие освобождает место, которое панель задач резервировала на экране.
        SetTaskbarAutoHide(true);
        KeepHidden(hideDesktopIcons);
    }

    /// <summary>Вызывается по таймеру: Windows иногда сама возвращает панель задач на экран.</summary>
    public static void KeepHidden(bool hideDesktopIcons)
    {
        foreach (var bar in TaskbarWindows())
            if (Native.IsWindowVisible(bar)) Native.ShowWindow(bar, Native.SW_HIDE);

        var icons = DesktopIconsWindow();
        if (icons != IntPtr.Zero && Native.IsWindowVisible(icons) == hideDesktopIcons)
            Native.ShowWindow(icons, hideDesktopIcons ? Native.SW_HIDE : Native.SW_SHOWNA);
    }

    /// <summary>Ставит обои до конца сеанса; исходные запоминаются для восстановления.</summary>
    public static void SetWallpaper(string path)
    {
        var saved = Load();
        if (saved is null) return; // без файла восстановления систему не трогаем
        if (saved.Wallpaper is null)
            Save(saved with { Wallpaper = GetWallpaper() });
        Native.SystemParametersInfo(Native.SPI_SETDESKWALLPAPER, 0, path, Native.SPIF_SENDCHANGE);
    }

    /// <summary>Возвращает исходные обои, если BeautyDesk их менял.</summary>
    public static void ResetWallpaper()
    {
        var saved = Load();
        if (saved?.Wallpaper is null) return;
        Native.SystemParametersInfo(Native.SPI_SETDESKWALLPAPER, 0, saved.Wallpaper, Native.SPIF_SENDCHANGE);
        Save(saved with { Wallpaper = null });
    }

    /// <summary>
    /// Возвращает Windows в исходное состояние. Безопасно вызывать повторно.
    /// <paramref name="onlyIfOwnerPid"/> задаёт сторож: он не должен отменять изменения
    /// нового экземпляра, запущенного после того, как старый уже завершился.
    /// </summary>
    public static void Restore(int? onlyIfOwnerPid = null)
    {
        var saved = Load();
        if (saved is null) return;
        if (onlyIfOwnerPid is { } pid && saved.OwnerPid != pid) return;

        if (saved.Wallpaper is not null)
            Native.SystemParametersInfo(Native.SPI_SETDESKWALLPAPER, 0, saved.Wallpaper, Native.SPIF_SENDCHANGE);
        SetTaskbarAutoHide(saved.TaskbarAutoHide);
        foreach (var bar in TaskbarWindows())
            Native.ShowWindow(bar, Native.SW_SHOWNA);
        var icons = DesktopIconsWindow();
        if (icons != IntPtr.Zero) Native.ShowWindow(icons, Native.SW_SHOWNA);

        try { File.Delete(AppPaths.RestoreState); } catch { }
        Log.Write("Состояние Windows восстановлено");
    }

    /// <summary>При запуске: если прошлый экземпляр умер и ничего не вернул, возвращаем за него.</summary>
    public static void RestoreIfStale()
    {
        var saved = Load();
        if (saved is null) return;
        try
        {
            using var owner = Process.GetProcessById(saved.OwnerPid);
            if (!owner.HasExited && owner.ProcessName == Process.GetCurrentProcess().ProcessName) return;
        }
        catch (ArgumentException) { /* процесса уже нет */ }
        Restore();
    }

    // ---------- панель задач ----------

    private static IEnumerable<IntPtr> TaskbarWindows()
    {
        var main = Native.FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero) yield return main;
        var next = IntPtr.Zero;
        while ((next = Native.FindWindowEx(IntPtr.Zero, next, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            yield return next;
    }

    private static Native.APPBARDATA TaskbarData() => new()
    {
        cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.APPBARDATA>(),
        hWnd = Native.FindWindow("Shell_TrayWnd", null)
    };

    private static bool GetTaskbarAutoHide()
    {
        var data = TaskbarData();
        return ((int)Native.SHAppBarMessage(Native.ABM_GETSTATE, ref data) & Native.ABS_AUTOHIDE) != 0;
    }

    private static void SetTaskbarAutoHide(bool on)
    {
        var data = TaskbarData();
        if (data.hWnd == IntPtr.Zero) return;
        data.lParam = on ? Native.ABS_AUTOHIDE : 0;
        Native.SHAppBarMessage(Native.ABM_SETSTATE, ref data);
    }

    // ---------- значки рабочего стола ----------

    private static IntPtr DesktopIconsWindow()
    {
        // Список значков живёт в SHELLDLL_DefView: обычно внутри Progman,
        // но при живых обоях Windows переносит его в одно из окон WorkerW.
        var view = Native.FindWindowEx(Native.FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
        if (view == IntPtr.Zero)
        {
            Native.EnumWindows((hwnd, _) =>
            {
                if (Native.ClassName(hwnd) != "WorkerW") return true;
                view = Native.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                return view == IntPtr.Zero;
            }, IntPtr.Zero);
        }
        return view == IntPtr.Zero ? IntPtr.Zero : Native.FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
    }

    // ---------- обои ----------

    private static string GetWallpaper()
    {
        var sb = new StringBuilder(520);
        Native.SystemParametersInfo(Native.SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0);
        return sb.ToString();
    }

    // ---------- файл восстановления ----------

    private static Saved? Load()
    {
        try
        {
            return File.Exists(AppPaths.RestoreState)
                ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(AppPaths.RestoreState))
                : null;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            Log.Write($"Файл восстановления не прочитан: {e.Message}");
            return null;
        }
    }

    private static void Save(Saved saved) =>
        File.WriteAllText(AppPaths.RestoreState, JsonSerializer.Serialize(saved));
}
