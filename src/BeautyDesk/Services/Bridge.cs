using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace BeautyDesk.Services;

/// <summary>
/// Канал данных между сторонними программами и виджетами — то, чем пользуются дополнения вроде ASUS.
///
/// Папка: %LOCALAPPDATA%\BeautyDesk\bridge.
///   Программа → виджет: программа кладёт туда &lt;имя&gt;.json (лучше записать во временный файл и
///   переименовать). BeautyDesk замечает изменение и пересылает содержимое виджетам: BD.bridge(имя, …).
///   Если файл давно не обновлялся, виджет видит это по полю age (секунды) и может показать «нет связи».
///   Виджет → программа: BD.bridgeSend(имя, команда) дописывает команду строкой JSON в &lt;имя&gt;.commands.
///
/// Имя — латиница в нижнем регистре, цифры и дефис, до 32 символов.
/// </summary>
internal sealed partial class Bridge : IDisposable
{
    private const long MaxStateBytes = 256 * 1024;

    [GeneratedRegex("^[a-z0-9-]{1,32}$")]
    private static partial Regex ValidName();

    public static readonly string Folder = Path.Combine(AppPaths.Local, "bridge");

    private readonly FileSystemWatcher _watcher;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly HashSet<string> _pending = [];
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };

    /// <summary>Пришли новые данные от программы: имя и содержимое (JSON).</summary>
    public event Action<string, JsonElement, double>? Received;

    public Bridge()
    {
        Directory.CreateDirectory(Folder);
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            foreach (var name in _pending.ToList()) Read(name);
            _pending.Clear();
        };
        _watcher = new FileSystemWatcher(Folder, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        // События приходят из чужого потока и часто по нескольку раз на одну запись — собираем в пачку.
        FileSystemEventHandler changed = (_, e) => _ui.BeginInvoke(() => Schedule(Path.GetFileNameWithoutExtension(e.Name)));
        _watcher.Changed += changed;
        _watcher.Created += changed;
        _watcher.Renamed += (_, e) => _ui.BeginInvoke(() => Schedule(Path.GetFileNameWithoutExtension(e.Name)));
    }

    /// <summary>Перечитывает все файлы — при запуске и когда страница с виджетами загрузилась заново.</summary>
    public void ReadAll()
    {
        foreach (var file in Directory.GetFiles(Folder, "*.json"))
            Read(Path.GetFileNameWithoutExtension(file));
    }

    /// <summary>Передаёт команду программе-поставщику: одна строка JSON в &lt;имя&gt;.commands.</summary>
    public void Send(string name, JsonElement command)
    {
        if (!ValidName().IsMatch(name)) return;
        try
        {
            File.AppendAllText(Path.Combine(Folder, name + ".commands"), command.GetRawText() + Environment.NewLine);
        }
        catch (IOException e) { Log.Write($"Команда для «{name}» не записана: {e.Message}"); }
    }

    public void Dispose() => _watcher.Dispose();

    private void Schedule(string? name)
    {
        if (name is null || !ValidName().IsMatch(name)) return;
        _pending.Add(name);
        if (!_debounce.IsEnabled) _debounce.Start();
    }

    private void Read(string name)
    {
        if (!ValidName().IsMatch(name)) return;
        var path = Path.Combine(Folder, name + ".json");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxStateBytes) return;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            Received?.Invoke(name, doc.RootElement.Clone(), (DateTime.UtcNow - info.LastWriteTimeUtc).TotalSeconds);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Файл как раз переписывают — следующее событие придёт, когда запись закончится.
        }
    }
}
