// Дополнение BeautyDesk для ноутбуков ASUS: режимы вентиляторов, обороты и порог зарядки.
// Основано на сервисе AsusQuick, который написал первый тестировщик BeautyDesk для своего Vivobook.
//
// Доступ к драйверу ASUS (WMI root\wmi, AsusAtkWmi_WMNB) есть только у администратора, поэтому
// дополнение — отдельная программа, которую планировщик заданий запускает при входе с повышенными правами.
// Сам BeautyDesk прав администратора не получает.
//
// Обмен — через канал данных BeautyDesk (%LOCALAPPDATA%\BeautyDesk\bridge):
//   fans.json      — состояние, его показывает виджет «Питание»;
//   fans.commands  — команды виджета, по одной строке JSON: {"action":"mode","id":2} или {"action":"chargeLimit","value":80}.
//
// Сборка: build.cmd (компилятор .NET Framework есть в каждой Windows 10/11).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

static class AsusFans
{
    // Коды устройств ASUS WMI.
    const uint DEV_PERF_ROG = 0x00120075;      // режим ROG / TUF: 0 баланс, 1 турбо, 2 тихий
    const uint DEV_PERF_VIVOBOOK = 0x00110019; // режим Vivobook / Zenbook: 0 стандарт, 1 тихий, 2 турбо, 3 full speed
    const uint DEV_CHARGE_LIMIT = 0x00120057;
    const uint DEV_FAN_CPU = 0x00110013, DEV_FAN_GPU = 0x00110014;
    const uint UNSUPPORTED = 0xFFFFFFFE;

    class Mode { public int Id; public string Ru, En; public Mode(int id, string ru, string en) { Id = id; Ru = ru; En = en; } }

    // Режимы — от самого тихого к самому мощному: так виджет понимает, с какой скоростью крутить значок.
    static readonly Mode[] RogModes = { new Mode(2, "Тихий", "Silent"), new Mode(0, "Баланс", "Balanced"), new Mode(1, "Турбо", "Turbo") };
    static readonly Mode[] VivobookModes = { new Mode(1, "Тихий", "Silent"), new Mode(0, "Стандарт", "Standard"), new Mode(2, "Турбо", "Turbo"), new Mode(3, "Full speed", "Full speed") };
    static readonly int[] ChargeLimits = { 60, 80, 100 };

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    static readonly Regex Action = new Regex("\"action\"\\s*:\\s*\"(mode|chargeLimit)\"", RegexOptions.Compiled);
    static readonly Regex Number = new Regex("\"(id|value)\"\\s*:\\s*(\\d{1,3})", RegexOptions.Compiled);

    static ManagementObject wmi;
    static uint perfDevice;
    static Mode[] modes;
    static string stateFile, commandsFile, limitFile;

    static void Main()
    {
        bool created;
        using (var single = new Mutex(true, @"Local\BeautyDesk.AsusFans", out created))
        {
            if (!created) return;

            string bridge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"BeautyDesk\bridge");
            Directory.CreateDirectory(bridge);
            stateFile = Path.Combine(bridge, "fans.json");
            commandsFile = Path.Combine(bridge, "fans.commands");
            // Драйвер не всегда умеет сообщить порог зарядки — помним последний установленный.
            string own = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeautyDesk-AsusFans");
            Directory.CreateDirectory(own);
            limitFile = Path.Combine(own, "charge-limit.txt");

            foreach (ManagementObject o in new ManagementClass(@"root\wmi", "AsusAtkWmi_WMNB", null).GetInstances()) { wmi = o; break; }
            if (wmi == null) return; // не ноутбук ASUS или нет драйвера ASUS System Control Interface

            // Какой из двух переключателей режима есть у этой модели.
            if (Supported(DEV_PERF_ROG)) { perfDevice = DEV_PERF_ROG; modes = RogModes; }
            else if (Supported(DEV_PERF_VIVOBOOK)) { perfDevice = DEV_PERF_VIVOBOOK; modes = VivobookModes; }
            else modes = new Mode[0];

            long position = File.Exists(commandsFile) ? new FileInfo(commandsFile).Length : 0; // старые команды не повторяем
            string last = null;
            DateTime lastWrite = DateTime.MinValue;
            for (int tick = 0; ; tick++)
            {
                bool changed = false;
                try
                {
                    changed = ReadCommands(ref position);
                    // Состояние — раз в 2 с и только пока BeautyDesk запущен; после команды — сразу.
                    if (changed || tick % 4 == 0)
                    {
                        if (changed || Process.GetProcessesByName("BeautyDesk").Length > 0)
                        {
                            string json = BuildState();
                            // Даже без изменений файл обновляется раз в 5 с: по его свежести виджет понимает, что дополнение работает.
                            if (json != last || (DateTime.UtcNow - lastWrite).TotalSeconds >= 5)
                            {
                                WriteAtomically(stateFile, json);
                                last = json;
                                lastWrite = DateTime.UtcNow;
                            }
                        }
                    }
                }
                catch { /* следующий круг попробует снова */ }
                Thread.Sleep(500);
            }
        }
    }

    static uint? Dsts(uint device)
    {
        try
        {
            var p = wmi.GetMethodParameters("DSTS");
            p["Device_ID"] = device;
            return Convert.ToUInt32(wmi.InvokeMethod("DSTS", p, null)["device_status"]);
        }
        catch { return null; }
    }

    static void Devs(uint device, uint value)
    {
        var p = wmi.GetMethodParameters("DEVS");
        p["Device_ID"] = device;
        p["Control_status"] = value;
        wmi.InvokeMethod("DEVS", p, null);
    }

    /// <summary>Устройство есть у этой модели: драйвер отвечает и ставит бит «присутствует».</summary>
    static bool Supported(uint device)
    {
        uint? v = Dsts(device);
        return v != null && v.Value != UNSUPPORTED && (v.Value & 0x00010000) != 0;
    }

    static string Rpm(uint device)
    {
        uint? v = Dsts(device);
        if (v == null || v.Value == UNSUPPORTED || (v.Value & 0x00010000) == 0) return "null";
        return ((v.Value & 0xFFFF) * 100).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Читает новые строки команд. Принимаются только известные режимы и пороги.</summary>
    static bool ReadCommands(ref long position)
    {
        var info = new FileInfo(commandsFile);
        if (!info.Exists) return false;
        if (info.Length < position) position = 0;
        if (info.Length == position) return false;

        string text;
        using (var fs = new FileStream(commandsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            fs.Seek(position, SeekOrigin.Begin);
            var buffer = new byte[info.Length - position];
            int read = fs.Read(buffer, 0, buffer.Length);
            position += read;
            text = Utf8.GetString(buffer, 0, read);
        }

        bool any = false;
        foreach (string line in text.Split('\n'))
        {
            var action = Action.Match(line);
            var number = Number.Match(line);
            if (!action.Success || !number.Success) continue;
            int value = int.Parse(number.Groups[2].Value, CultureInfo.InvariantCulture);
            if (action.Groups[1].Value == "mode" && perfDevice != 0 && Array.Exists(modes, m => m.Id == value))
            {
                Devs(perfDevice, (uint)value);
                any = true;
            }
            else if (action.Groups[1].Value == "chargeLimit" && Array.IndexOf(ChargeLimits, value) >= 0)
            {
                Devs(DEV_CHARGE_LIMIT, (uint)value);
                File.WriteAllText(limitFile, value.ToString(CultureInfo.InvariantCulture), Utf8);
                any = true;
            }
        }
        return any;
    }

    static string BuildState()
    {
        var sb = new StringBuilder("{\"vendor\":\"ASUS\"");

        uint? current = perfDevice != 0 ? Dsts(perfDevice) : null;
        sb.Append(",\"mode\":").Append(current == null ? "null" : (current.Value & 0xFF).ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"modes\":[");
        for (int i = 0; i < modes.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(modes[i].Id).Append(",\"name\":{\"ru\":\"").Append(modes[i].Ru).Append("\",\"en\":\"").Append(modes[i].En).Append("\"}}");
        }
        sb.Append("],\"fans\":[{\"name\":\"CPU\",\"rpm\":").Append(Rpm(DEV_FAN_CPU)).Append("},{\"name\":\"GPU\",\"rpm\":").Append(Rpm(DEV_FAN_GPU)).Append("}]");

        SYSTEM_POWER_STATUS ps;
        bool hasBattery = GetSystemPowerStatus(out ps) && ps.BatteryLifePercent <= 100;
        if (hasBattery)
        {
            sb.Append(",\"chargeLimits\":[60,80,100],\"chargeLimit\":").Append(ReadLimit());
        }
        return sb.Append('}').ToString();
    }

    static string ReadLimit()
    {
        try
        {
            int value;
            if (File.Exists(limitFile) && int.TryParse(File.ReadAllText(limitFile).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return value.ToString(CultureInfo.InvariantCulture);
        }
        catch { }
        return "null";
    }

    /// <summary>Запись через временный файл: BeautyDesk никогда не увидит файл наполовину.</summary>
    static void WriteAtomically(string path, string text)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, text, Utf8);
        if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
    }
}
