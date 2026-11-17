using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using ManagedShell.WindowsTasks;

namespace BeautyDesk.Services;

/// <summary>Закреплённая в доке программа или папка — так она хранится в настройках.</summary>
internal sealed class DockPin
{
    /// <summary>app или folder.</summary>
    public string Kind { get; set; } = "app";
    public string Path { get; set; } = "";
    /// <summary>Идентификатор приложения из Магазина; у обычных программ пусто.</summary>
    public string? Aumid { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
/// Содержимое дока: закреплённые программы, запущенные программы, папки и корзина —
/// и действия над ними. Что и как рисовать, решает страница; здесь только данные и поведение.
/// </summary>
internal sealed class Dock
{
    public sealed record WindowRef(string Id, string Title);

    /// <param name="Kind">app, folder, trash или separator.</param>
    /// <param name="Icon">Ключ в <see cref="Icons"/>.</param>
    public sealed record Item(string Id, string Kind, string Title, string? Icon, bool Pinned, bool Running, bool Active, IReadOnlyList<WindowRef> Windows);

    private const string TrashId = "trash", TrashShellName = "shell:RecycleBinFolder";

    private readonly Tasks _tasks;
    private readonly Settings _settings;
    private readonly Func<string, string?> _findPackagedApp;
    private readonly Dictionary<string, string> _icons = [];
    private readonly HashSet<string> _noFileIcon = []; // программы, у которых иконку из файла прочитать не удалось, — повторно не пробуем
    private readonly HashSet<ApplicationWindow> _tracked = [];
    private readonly Dictionary<IntPtr, WindowIdentity.Info> _identities = [];
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _trashFull;

    /// <summary>Состав дока или состояние окон изменились.</summary>
    public event Action? Changed;

    /// <summary>Картинки по ключам из <see cref="Item.Icon"/>. Растёт по мере появления новых программ.</summary>
    public IReadOnlyDictionary<string, string> Icons => _icons;
    /// <summary>Увеличивается при каждом изменении <see cref="Icons"/> — чтобы не пересылать их зря.</summary>
    public int IconsVersion { get; private set; }

    /// <param name="findPackagedApp">По имени пакета приложения из Магазина возвращает его полное имя
    /// в папке «Приложения» (или null, если список программ ещё не прочитан).</param>
    public Dock(Tasks tasks, Settings settings, Func<string, string?> findPackagedApp)
    {
        _tasks = tasks;
        _settings = settings;
        _findPackagedApp = findPackagedApp;
        if (_settings.Dock is null)
        {
            _settings.Dock = DefaultPins();
            _settings.Save();
        }

        // Закрепления обычных программ, которые ведут в никуда (программу удалили), убираются.
        if (_settings.Dock.RemoveAll(p => p.Kind == "app" && PackageOf(p) is null && !File.Exists(p.Path)) > 0)
            _settings.Save();

        _debounce.Tick += (_, _) => { _debounce.Stop(); Changed?.Invoke(); };
        ((INotifyCollectionChanged)_tasks.GroupedWindows).CollectionChanged += (_, _) => Schedule();
        _trashFull = IsTrashFull();
    }

    // ---------- что показывать ----------

    public IReadOnlyList<Item> Items()
    {
        var windows = Windows().ToList();
        foreach (var w in windows)
            if (_tracked.Add(w)) w.PropertyChanged += OnWindowChanged;
        _tracked.RemoveWhere(w => !windows.Contains(w));
        foreach (var gone in _identities.Keys.Where(h => windows.All(w => w.Handle != h)).ToList()) _identities.Remove(gone);

        var byApp = windows.GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.ToList());
        var items = new List<Item>();

        Item AppItem(string key, string title, bool pinned, string parsingName, List<ApplicationWindow>? own)
        {
            own ??= [];
            if (!_icons.ContainsKey(key))
            {
                // Иконку из файла программы прочитать удаётся не всегда (например, у приложений из Магазина путь закрыт) —
                // тогда берём ту, что даёт само окно; она может появиться позже, вместе с окном.
                string? icon = _noFileIcon.Contains(key) ? null : IconLoader.DataUrl(parsingName);
                if (icon is null)
                {
                    // Для приложения из Магазина неудача может быть временной: список программ ещё читается.
                    if (!key.StartsWith("pkg:") || parsingName.StartsWith("shell:")) _noFileIcon.Add(key);
                    icon = IconLoader.Encode(own.FirstOrDefault()?.Icon);
                }
                if (icon is not null) SetIcon(key, icon);
            }
            return new Item("app:" + key, "app", title, _icons.ContainsKey(key) ? key : null, pinned, own.Count > 0,
                own.Any(w => w.State == ApplicationWindow.WindowState.Active),
                own.Select(w => new WindowRef(w.Handle.ToInt64().ToString(), w.Title ?? "")).ToList());
        }

        var pins = _settings.Dock!;
        foreach (var pin in pins.Where(p => p.Kind == "app"))
        {
            var key = KeyOf(pin);
            byApp.Remove(key, out var own);
            items.Add(AppItem(key, pin.Name, true, ParsingName(AumidOf(pin), pin.Path), own));
        }
        foreach (var (key, own) in byApp)
        {
            var first = own[0];
            items.Add(AppItem(key, DisplayName(first), false, ParsingName(AumidOf(first), ProgramPath(first)), own));
        }

        items.Add(new Item("separator", "separator", "", null, false, false, false, []));
        foreach (var pin in pins.Where(p => p.Kind == "folder" && Directory.Exists(p.Path)))
        {
            var key = "folder:" + pin.Path.ToLowerInvariant();
            if (!_icons.ContainsKey(key) && IconLoader.DataUrl(pin.Path) is { } icon) SetIcon(key, icon);
            items.Add(new Item(key, "folder", pin.Name, _icons.ContainsKey(key) ? key : null, true, false, false, []));
        }

        var trashKey = _trashFull ? "trash:full" : "trash:empty";
        if (!_icons.ContainsKey(trashKey) && IconLoader.DataUrl(TrashShellName) is { } trashIcon) SetIcon(trashKey, trashIcon);
        if (_settings.ShowTrash)
            items.Add(new Item(TrashId, "trash", "", _icons.ContainsKey(trashKey) ? trashKey : null, false, false, false, []));
        return items;
    }

    /// <summary>Вызывается изредка: корзина не сообщает сама, что в неё что-то положили.</summary>
    public void RefreshTrash()
    {
        bool full = IsTrashFull();
        if (full == _trashFull) return;
        _trashFull = full;
        _icons.Remove(full ? "trash:full" : "trash:empty"); // иконка корзины зависит от того, пуста ли она
        Schedule();
    }

    public IReadOnlyList<ApplicationWindow> WindowsOf(string itemId) =>
        itemId.StartsWith("app:") ? Windows().Where(w => "app:" + KeyOf(w) == itemId).ToList() : [];

    public ApplicationWindow? Window(string windowId) =>
        Windows().FirstOrDefault(w => w.Handle.ToInt64().ToString() == windowId);

    public bool IsPinned(string itemId) => FindPin(itemId) is not null;

    // ---------- действия ----------

    /// <summary>Обычный клик: запустить, показать, переключить между окнами или свернуть активное.</summary>
    public void Activate(string itemId)
    {
        if (itemId == TrashId) { Open(TrashShellName); return; }

        var windows = WindowsOf(itemId);
        if (windows.Count == 0) { Launch(itemId); return; }

        int active = windows.ToList().FindIndex(w => w.State == ApplicationWindow.WindowState.Active);
        if (active < 0) windows[0].BringToFront();
        else if (windows.Count == 1) windows[0].Minimize();
        else windows[(active + 1) % windows.Count].BringToFront();
    }

    public void Launch(string itemId)
    {
        if (itemId == TrashId) { Open(TrashShellName); return; }
        if (FindPin(itemId) is { } pin)
        {
            Open(pin.Kind == "app" ? ParsingName(AumidOf(pin), pin.Path) : pin.Path);
            return;
        }
        // Незакреплённая программа: запускаем ещё один экземпляр того же, что уже работает.
        if (WindowsOf(itemId).FirstOrDefault() is { } w)
            Open(ParsingName(AumidOf(w), ProgramPath(w)));
    }

    public void Pin(string itemId)
    {
        if (IsPinned(itemId) || PinFromWindow(itemId) is not { } pin) return;
        var pins = _settings.Dock!;
        // Новая программа встаёт после последней закреплённой программы, перед папками.
        pins.Insert(pins.FindLastIndex(p => p.Kind == "app") + 1, pin);
        _settings.Save();
        Schedule();
    }

    public void Unpin(string itemId)
    {
        if (FindPin(itemId) is not { } pin) return;
        _settings.Dock!.Remove(pin);
        _settings.Save();
        Schedule();
    }

    public void CloseWindows(string itemId)
    {
        foreach (var w in WindowsOf(itemId)) w.Close();
    }

    /// <summary>
    /// Человек перетащил иконку. <paramref name="itemIds"/> — программы в том порядке, в каком они теперь стоят
    /// в доке, <paramref name="draggedId"/> — та, которую тянули.
    /// Незакреплённые программы всегда стоят после закреплённых, поэтому, чтобы порядок остался таким,
    /// каким его видит человек, закрепляется всё вплоть до перетащенной иконки (и до последней закреплённой).
    /// </summary>
    public void Reorder(IReadOnlyList<string> itemIds, string draggedId)
    {
        var ids = itemIds.Distinct().ToList();
        int last = Math.Max(ids.FindLastIndex(IsPinned), ids.IndexOf(draggedId));
        var apps = ids.Take(last + 1)
            .Select(id => FindPin(id) ?? PinFromWindow(id))
            .OfType<DockPin>()
            .ToList();

        var pins = _settings.Dock!;
        // Закреплённые программы, которых страница не прислала (на всякий случай), не теряются.
        apps.AddRange(pins.Where(p => p.Kind == "app" && !apps.Contains(p)));
        _settings.Dock = [.. apps, .. pins.Where(p => p.Kind != "app")];
        _settings.Save();
        Schedule();
    }

    public static void EmptyTrash()
    {
        // Windows сама спросит подтверждение.
        SHEmptyRecycleBin(IntPtr.Zero, null, 0);
    }

    // ---------- внутреннее ----------

    private IEnumerable<ApplicationWindow> Windows() => _tasks.GroupedWindows.Cast<ApplicationWindow>();

    // Путь приложения из Магазина: ...\WindowsApps\Имя_версия_архитектура_ресурс_издатель\...
    private static readonly Regex PackagedPath = new(@"[/\\]WindowsApps[/\\]([^_/\\]+)_[^_/\\]*_[^_/\\]*_[^_/\\]*_([^/\\]+)[/\\]", RegexOptions.IgnoreCase);

    /// <summary>
    /// Имя пакета приложения из Магазина («Имя_издатель») или null для обычной программы.
    /// В пути к файлу такого приложения записан номер версии, поэтому после обновления путь меняется —
    /// узнавать приложение нужно по имени пакета.
    /// </summary>
    private static string? PackageOf(string? aumid, string? path)
    {
        if (!string.IsNullOrEmpty(aumid) && aumid.Contains('!')) return aumid[..aumid.IndexOf('!')];
        var match = PackagedPath.Match(path ?? "");
        return match.Success ? $"{match.Groups[1].Value}_{match.Groups[2].Value}" : null;
    }

    private static string? PackageOf(DockPin pin) => PackageOf(pin.Aumid, pin.Path);
    private static string? PackageOf(ApplicationWindow w) => PackageOf(w.AppUserModelID, w.WinFileName);

    /// <summary>Окна одной программы собираются в одну иконку по этому ключу.</summary>
    private string KeyOf(ApplicationWindow w) =>
        PackageOf(w) is { } package ? "pkg:" + package.ToLowerInvariant() : "path:" + ProgramPath(w).ToLowerInvariant();

    /// <summary>Полное имя приложения из Магазина в папке «Приложения»: по нему оно запускается и получает иконку.</summary>
    private string? AumidOf(ApplicationWindow w) =>
        PackageOf(w) is not { } package ? null
        : !string.IsNullOrEmpty(w.AppUserModelID) && w.AppUserModelID.Contains('!') ? w.AppUserModelID
        : _findPackagedApp(package);

    private string? AumidOf(DockPin pin) =>
        !string.IsNullOrEmpty(pin.Aumid) ? pin.Aumid : PackageOf(pin) is { } package ? _findPackagedApp(package) : null;

    private WindowIdentity.Info Identity(ApplicationWindow w)
    {
        if (!_identities.TryGetValue(w.Handle, out var info))
            _identities[w.Handle] = info = WindowIdentity.Read(w.Handle);
        return info;
    }

    /// <summary>Программа, которой принадлежит окно: из «паспорта» окна, а если его нет — сам процесс окна.</summary>
    private string ProgramPath(ApplicationWindow w) => Identity(w).Path ?? w.WinFileName ?? "";

    private static string KeyOf(DockPin pin) =>
        PackageOf(pin) is { } package ? "pkg:" + package.ToLowerInvariant() : "path:" + pin.Path.ToLowerInvariant();

    private static string ParsingName(string? aumid, string? path) =>
        !string.IsNullOrEmpty(aumid) ? @"shell:AppsFolder\" + aumid : path ?? "";

    private string DisplayName(ApplicationWindow w)
    {
        if (w.IsUWP && !string.IsNullOrWhiteSpace(w.Title)) return w.Title;
        var identity = Identity(w);
        if (!string.IsNullOrWhiteSpace(identity.Name)) return identity.Name;
        var path = ProgramPath(w);
        try
        {
            if (File.Exists(path) && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description)
                return description;
        }
        catch (IOException) { }
        return System.IO.Path.GetFileNameWithoutExtension(path) is { Length: > 0 } file ? file : w.Title ?? "";
    }

    private DockPin? PinFromWindow(string itemId) =>
        WindowsOf(itemId).FirstOrDefault() is { } w
            ? new DockPin { Kind = "app", Path = ProgramPath(w), Aumid = AumidOf(w), Name = DisplayName(w) }
            : null;

    private DockPin? FindPin(string itemId) =>
        _settings.Dock!.FirstOrDefault(p =>
            p.Kind == "app" ? "app:" + KeyOf(p) == itemId : "folder:" + p.Path.ToLowerInvariant() == itemId);

    private void SetIcon(string key, string dataUrl)
    {
        _icons[key] = dataUrl;
        IconsVersion++;
    }

    private void OnWindowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ApplicationWindow.Title) or nameof(ApplicationWindow.State) or nameof(ApplicationWindow.ShowInTaskbar) or nameof(ApplicationWindow.Icon))
            Schedule();
    }

    private void Schedule()
    {
        if (!_debounce.IsEnabled) _debounce.Start();
    }

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = false })?.Dispose(); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { Log.Write($"Не открылось {target}: {e.Message}"); }
    }

    // ---------- первый запуск ----------

    /// <summary>В первый раз в док переносятся программы, закреплённые на панели задач Windows, и папка «Загрузки».</summary>
    private static List<DockPin> DefaultPins()
    {
        var pins = new List<DockPin>();
        try
        {
            var taskbar = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            if (Directory.Exists(taskbar) && Type.GetTypeFromProgID("WScript.Shell") is { } type)
            {
                dynamic shell = Activator.CreateInstance(type)!;
                foreach (var link in Directory.GetFiles(taskbar, "*.lnk"))
                {
                    string target = shell.CreateShortcut(link).TargetPath;
                    if (string.IsNullOrEmpty(target) || !File.Exists(target)) continue; // ярлыки приложений из Магазина сюда не попадают
                    if (pins.Any(p => p.Path.Equals(target, StringComparison.OrdinalIgnoreCase))) continue;
                    pins.Add(new DockPin { Kind = "app", Path = target, Name = System.IO.Path.GetFileNameWithoutExtension(link) });
                }
                Marshal.ReleaseComObject(shell);
            }
        }
        catch (Exception e)
        {
            Log.Write($"Закреплённые программы панели задач не прочитаны: {e.Message}");
        }

        if (pins.Count == 0)
        {
            var explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            pins.Add(new DockPin { Kind = "app", Path = explorer, Name = "Explorer" });
        }

        var downloads = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads))
            pins.Add(new DockPin { Kind = "folder", Path = downloads, Name = System.IO.Path.GetFileName(downloads) });
        return pins;
    }

    // ---------- корзина ----------

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct SHQUERYRBINFO { public int cbSize; public long i64Size, i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? root, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    private static bool IsTrashFull()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 && info.i64NumItems > 0;
    }
}
