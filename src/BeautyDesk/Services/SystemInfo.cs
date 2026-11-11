using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;
using NAudio.CoreAudioApi;

namespace BeautyDesk.Services;

/// <summary>
/// Системные показатели для верхней панели: активное приложение, раскладка, сеть, батарея, громкость.
/// Всё читается опросом в <see cref="Refresh"/>; событие приходит, только когда что-то изменилось.
/// </summary>
internal sealed class SystemInfo : IDisposable
{
    /// <param name="ActiveApp">Название программы в фокусе; пустая строка — рабочий стол.</param>
    /// <param name="Battery">Заряд в процентах; null — батареи нет.</param>
    /// <param name="Network">wifi, ethernet или none.</param>
    /// <param name="Volume">Громкость 0–100; null — нет устройства вывода.</param>
    public sealed record Snapshot(string ActiveApp, string Layout, int? Battery, bool Charging, string Network, int? Volume, bool Muted);

    private const int SlowEvery = 10; // сеть и устройство звука перепроверяются раз в 10 опросов

    private readonly MMDeviceEnumerator _audio = new();
    private readonly Dictionary<string, string> _appNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Native.WinEventProc _foregroundProc;
    private readonly IntPtr _foregroundHook;
    private MMDevice? _device;
    private string _network = "none";
    private string _activeApp = "";
    private int _tick;
    private volatile bool _networkDirty = true;

    public Snapshot Current { get; private set; } = new("", "", null, false, "none", null, false);
    public event Action? Changed;

    public SystemInfo()
    {
        NetworkChange.NetworkAddressChanged += (_, _) => _networkDirty = true;
        NetworkChange.NetworkAvailabilityChanged += (_, _) => _networkDirty = true;

        // Смена активного окна приходит событием — название в панели меняется без задержки опроса.
        _foregroundProc = (_, _, _, _, _, _, _) => Refresh();
        _foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _foregroundProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
    }

    public void Refresh()
    {
        bool slow = _tick++ % SlowEvery == 0;
        if (slow || _networkDirty)
        {
            _networkDirty = false;
            _network = ReadNetwork();
        }
        if (slow) RebindAudioDevice();

        var foreground = Native.GetForegroundWindow();
        if (foreground != IntPtr.Zero)
        {
            Native.GetWindowThreadProcessId(foreground, out var pid);
            if (pid != Environment.ProcessId) ExternalFocus.Last = foreground;
        }
        var (volume, muted) = ReadVolume();
        var (battery, charging) = ReadBattery();
        var next = new Snapshot(ReadActiveApp(foreground), ReadLayout(foreground), battery, charging, _network, volume, muted);
        if (next == Current) return;
        Current = next;
        Changed?.Invoke();
    }

    /// <summary>Меняет громкость на указанное число процентных пунктов.</summary>
    public void StepVolume(int delta)
    {
        try
        {
            if (_device is null) return;
            var volume = _device.AudioEndpointVolume;
            volume.MasterVolumeLevelScalar = Math.Clamp(volume.MasterVolumeLevelScalar + delta / 100f, 0f, 1f);
        }
        catch (Exception e) { Log.Write($"Громкость не изменена: {e.Message}"); }
        Refresh();
    }

    public void Dispose()
    {
        Native.UnhookWinEvent(_foregroundHook);
        _device?.Dispose();
        _audio.Dispose();
    }

    // ---------- активное приложение и раскладка ----------

    private string ReadActiveApp(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero) return _activeApp;
        Native.GetWindowThreadProcessId(foreground, out var pid);
        // Пока открыто наше меню, в панели остаётся имя программы, из которой его вызвали.
        if (pid == Environment.ProcessId) return _activeApp;

        var cls = Native.ClassName(foreground);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return _activeApp = "";

        var path = ProcessPath(pid);
        if (path is null || Path.GetFileName(path).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            return _activeApp = Native.WindowTitle(foreground); // приложения из Магазина прячутся за общим процессом

        if (!_appNames.TryGetValue(path, out var name))
        {
            try { name = FileVersionInfo.GetVersionInfo(path).FileDescription; }
            catch (IOException) { name = null; }
            if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(path);
            _appNames[path] = name;
        }
        return _activeApp = name;
    }

    private static string? ProcessPath(uint pid)
    {
        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return Native.QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { Native.CloseHandle(handle); }
    }

    private static string ReadLayout(IntPtr foreground)
    {
        uint thread = foreground == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(foreground, out _);
        int langId = (int)(Native.GetKeyboardLayout(thread).ToInt64() & 0xFFFF);
        try { return langId == 0 ? "" : new CultureInfo(langId).TwoLetterISOLanguageName.ToUpperInvariant(); }
        catch (CultureNotFoundException) { return ""; }
    }

    // ---------- сеть, батарея ----------

    private static string ReadNetwork()
    {
        try
        {
            var online = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any)))
                .Select(n => n.NetworkInterfaceType)
                .ToList();
            if (online.Count == 0) return "none";
            // VPN-адаптеры выглядят как Ethernet, поэтому при живом Wi-Fi показываем именно его.
            return online.Contains(NetworkInterfaceType.Wireless80211) ? "wifi" : "ethernet";
        }
        catch (NetworkInformationException) { return "none"; }
    }

    private static (int? Percent, bool Charging) ReadBattery()
    {
        if (!Native.GetSystemPowerStatus(out var s) || (s.BatteryFlag & 128) != 0 || s.BatteryLifePercent > 100)
            return (null, false);
        return (s.BatteryLifePercent, s.ACLineStatus == 1);
    }

    // ---------- звук ----------

    private void RebindAudioDevice()
    {
        try
        {
            if (!_audio.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            {
                _device?.Dispose();
                _device = null;
                return;
            }
            var current = _audio.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (_device?.ID == current.ID) { current.Dispose(); return; }
            _device?.Dispose();
            _device = current;
        }
        catch (Exception e)
        {
            Log.Write($"Устройство звука не получено: {e.Message}");
            _device = null;
        }
    }

    private (int? Volume, bool Muted) ReadVolume()
    {
        try
        {
            if (_device is null) return (null, false);
            var v = _device.AudioEndpointVolume;
            return ((int)Math.Round(v.MasterVolumeLevelScalar * 100), v.Mute);
        }
        catch (Exception)
        {
            _device = null; // устройство отключили — подхватим новое на следующем медленном опросе
            return (null, false);
        }
    }
}
