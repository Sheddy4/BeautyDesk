using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace BeautyDesk.Services;

/// <summary>
/// Загрузка процессора, памяти и видеокарты для виджета «Монитор железа».
/// Видеокарта и её температура читаются через библиотеку драйвера NVIDIA; на других видеокартах этих данных нет.
/// Температуру процессора Windows без прав администратора не отдаёт, поэтому её здесь нет.
/// </summary>
internal sealed class HardwareInfo : IDisposable
{
    /// <param name="Cpu">Загрузка процессора, 0–100.</param>
    /// <param name="Memory">Занято памяти, 0–100.</param>
    /// <param name="Gpu">Загрузка видеокарты, 0–100; null — данных нет.</param>
    /// <param name="GpuTemperature">Температура видеокарты в °C; null — данных нет.</param>
    public sealed record Snapshot(int Cpu, int Memory, double MemoryUsedGb, double MemoryTotalGb, int? Gpu, int? GpuTemperature, string? GpuName);

    public sealed record Disk(string Name, double FreeGb, double TotalGb);

    /// <summary>Скорость сети в байтах в секунду.</summary>
    public sealed record NetworkSpeed(long Down, long Up);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint Low, High; public readonly ulong Value => ((ulong)High << 32) | Low; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization { public uint Gpu, Memory; }

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")] private static extern int NvmlInit();
    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")] private static extern int NvmlShutdown();
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] private static extern int NvmlDeviceByIndex(uint index, out IntPtr device);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")] private static extern int NvmlUtilizationRates(IntPtr device, out NvmlUtilization utilization);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")] private static extern int NvmlTemperature(IntPtr device, int sensor, out uint degrees);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName")] private static extern int NvmlName(IntPtr device, byte[] name, uint length);

    private ulong _lastIdle, _lastTotal;
    private IntPtr _gpu;
    private string? _gpuName;
    private bool _nvml;

    public HardwareInfo()
    {
        Read(); // первое чтение задаёт точку отсчёта для загрузки процессора
        try
        {
            if (NvmlInit() == 0 && NvmlDeviceByIndex(0, out _gpu) == 0)
            {
                _nvml = true;
                var name = new byte[96];
                if (NvmlName(_gpu, name, (uint)name.Length) == 0)
                    _gpuName = Encoding.ASCII.GetString(name).TrimEnd('\0').Trim();
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // Видеокарта не NVIDIA или драйвер без этой библиотеки — виджет покажет процессор и память.
        }
    }

    public Snapshot Read()
    {
        int cpu = 0;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            // Время ядра уже включает простой, поэтому общее время — ядро плюс пользователь.
            ulong total = kernel.Value + user.Value, idleNow = idle.Value;
            ulong totalDelta = total - _lastTotal, idleDelta = idleNow - _lastIdle;
            if (_lastTotal != 0 && totalDelta > 0) cpu = (int)Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
            _lastTotal = total;
            _lastIdle = idleNow;
        }

        var memory = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref memory);
        const double Gb = 1024.0 * 1024 * 1024;

        int? gpu = null, temperature = null;
        if (_nvml)
        {
            if (NvmlUtilizationRates(_gpu, out var utilization) == 0) gpu = (int)utilization.Gpu;
            if (NvmlTemperature(_gpu, 0, out var degrees) == 0) temperature = (int)degrees;
        }

        return new Snapshot(cpu, (int)memory.MemoryLoad,
            Math.Round((memory.TotalPhys - memory.AvailPhys) / Gb, 1), Math.Round(memory.TotalPhys / Gb, 1),
            gpu, temperature, _gpuName);
    }

    /// <summary>Встроенные диски и свободное место на них.</summary>
    public static IReadOnlyList<Disk> ReadDisks()
    {
        const double Gb = 1024.0 * 1024 * 1024;
        var disks = new List<Disk>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                disks.Add(new Disk(drive.Name.TrimEnd('\\'), Math.Round(drive.AvailableFreeSpace / Gb), Math.Round(drive.TotalSize / Gb)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return disks;
    }

    private readonly Dictionary<string, (long Received, long Sent)> _traffic = [];
    private DateTime _trafficAt;

    /// <summary>
    /// Скорость сети с прошлого вызова. Берётся самый нагруженный адаптер, а не сумма по всем:
    /// при включённом VPN один и тот же трафик идёт и через настоящий адаптер, и через виртуальный.
    /// </summary>
    public NetworkSpeed ReadNetwork()
    {
        var now = DateTime.UtcNow;
        double seconds = (now - _trafficAt).TotalSeconds;
        bool first = _trafficAt == default || seconds <= 0;
        _trafficAt = now;

        long down = 0, up = 0;
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var stats = adapter.GetIPStatistics();
                if (_traffic.TryGetValue(adapter.Id, out var before) && !first)
                {
                    long received = stats.BytesReceived - before.Received, sent = stats.BytesSent - before.Sent;
                    if (received + sent > down + up) { down = Math.Max(0, received); up = Math.Max(0, sent); }
                }
                _traffic[adapter.Id] = (stats.BytesReceived, stats.BytesSent);
            }
        }
        catch (NetworkInformationException) { }
        return first ? new NetworkSpeed(0, 0) : new NetworkSpeed((long)(down / seconds), (long)(up / seconds));
    }

    public void Dispose()
    {
        if (!_nvml) return;
        _nvml = false;
        try { NvmlShutdown(); } catch (DllNotFoundException) { }
    }
}
