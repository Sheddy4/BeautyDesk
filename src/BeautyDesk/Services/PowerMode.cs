using System.Runtime.InteropServices;

namespace BeautyDesk.Services;

/// <summary>
/// Режим питания Windows 11 («Эффективность», «Сбалансированный», «Производительность») и батарея.
/// Это тот же переключатель, что в «Параметры → Система → Питание»; прав администратора не нужно.
/// На многих ноутбуках драйвер производителя меняет вместе с ним и режим вентиляторов.
/// </summary>
internal static class PowerMode
{
    /// <param name="Mode">efficiency, balanced, performance; null — переключатель недоступен (например, выбрана не сбалансированная схема питания).</param>
    /// <param name="Battery">Заряд в процентах; null — батареи нет.</param>
    /// <param name="MinutesLeft">Сколько осталось работать от батареи; null — неизвестно или идёт зарядка.</param>
    public sealed record Snapshot(string? Mode, int? Battery, bool Plugged, bool Charging, int? MinutesLeft, bool Saver);

    private static readonly (string Name, Guid Id)[] Modes =
    [
        ("efficiency", new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a")),
        ("balanced", Guid.Empty),
        ("performance", new Guid("ded574b5-45a0-4f42-8737-46345c09c238"))
    ];

    [DllImport("powrprof.dll")] private static extern uint PowerGetEffectiveOverlayScheme(out Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveOverlayScheme(Guid scheme);

    public static Snapshot Read()
    {
        string? mode = null;
        try
        {
            if (PowerGetEffectiveOverlayScheme(out var scheme) == 0)
                mode = Modes.FirstOrDefault(m => m.Id == scheme).Name ?? "balanced";
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { }

        int? battery = null, minutes = null;
        bool plugged = true, charging = false, saver = false;
        if (Native.GetSystemPowerStatus(out var s))
        {
            plugged = s.ACLineStatus != 0;
            saver = s.SystemStatusFlag == 1;
            if ((s.BatteryFlag & 128) == 0 && s.BatteryLifePercent <= 100)
            {
                battery = s.BatteryLifePercent;
                charging = (s.BatteryFlag & 8) != 0;
                if (!plugged && s.BatteryLifeTime != uint.MaxValue) minutes = (int)(s.BatteryLifeTime / 60);
            }
        }
        return new Snapshot(mode, battery, plugged, charging, minutes, saver);
    }

    public static bool Set(string mode)
    {
        var target = Modes.FirstOrDefault(m => m.Name == mode);
        if (target.Name is null) return false;
        try { return PowerSetActiveOverlayScheme(target.Id) == 0; }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { return false; }
    }
}
