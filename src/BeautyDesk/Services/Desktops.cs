using Microsoft.Win32;

namespace BeautyDesk.Services;

/// <summary>
/// Виртуальные рабочие столы Windows. Список и текущий стол читаются из реестра — там их хранит сам
/// Проводник; переключение — штатными сочетаниями Ctrl+Win+стрелки. Недокументированные интерфейсы
/// Проводника не используются: они меняются от сборки к сборке Windows.
/// </summary>
internal static class Desktops
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    /// <param name="Names">Имена столов; пустая строка — имя не задано.</param>
    /// <param name="Current">Номер текущего стола (с нуля).</param>
    public sealed record Snapshot(IReadOnlyList<string> Names, int Current);

    public static Snapshot Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            if (key?.GetValue("VirtualDesktopIDs") is not byte[] ids || ids.Length < 16) return new Snapshot([""], 0);
            var guids = Enumerable.Range(0, ids.Length / 16).Select(i => new Guid(ids.AsSpan(i * 16, 16))).ToList();

            // Текущий стол хранится для сеанса входа; в некоторых сборках — прямо в этом ключе.
            byte[]? current = key.GetValue("CurrentVirtualDesktop") as byte[];
            if (current is null)
            {
                int session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
                using var sessionKey = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\{session}\VirtualDesktops");
                current = sessionKey?.GetValue("CurrentVirtualDesktop") as byte[];
            }
            int index = current is { Length: 16 } ? Math.Max(0, guids.IndexOf(new Guid(current))) : 0;

            var names = guids.Select(g =>
            {
                using var desktop = Registry.CurrentUser.OpenSubKey($@"{Key}\Desktops\{g:B}");
                return desktop?.GetValue("Name") as string ?? "";
            }).ToList();
            return new Snapshot(names, index);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or ArgumentException or System.IO.IOException)
        {
            return new Snapshot([""], 0);
        }
    }
}
