using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
    private readonly Dictionary<string, string> _icons = [];
    private readonly HashSet<ApplicationWindow> _tracked = [];
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _trashFull;

    /// <summary>Состав дока или состояние окон изменились.</summary>
    public event Action? Changed;

    /// <summary>Картинки по ключам из <see cref="Item.Icon"/>. Растёт по мере появления новых программ.</summary>
    public IReadOnlyDictionary<string, string> Icons => _icons;
    /// <summary>Увеличивается при каждом изменении <see cref="Icons"/> — чтобы не пересылать их зря.</summary>
    public int IconsVersion { get; private set; }

    public Dock(Tasks tasks, Settings settings)
    {
        _tasks = tasks;
        _settings = settings;
        if (_settings.Dock is null)
        {
            _settings.Dock = DefaultPins();
            _settings.Save();
        }

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

        var byApp = windows.GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.ToList());
        var items = new List<Item>();

        Item AppItem(string key, string title, bool pinned, string parsingName, List<ApplicationWindow>? own)
        {
            own ??= [];
            if (!_icons.ContainsKey(key))
            {
                var icon = IconLoader.DataUrl(parsingName) ?? IconLoader.Encode(own.FirstOrDefault()?.Icon);
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
            items.Add(AppItem(key, pin.Name, true, ParsingName(pin.Aumid, pin.Path), own));
        }
        foreach (var (key, own) in byApp)
        {
            var first = own[0];
            items.Add(AppItem(key, DisplayName(first), false, ParsingName(first.IsUWP ? first.AppUserModelID : null, first.WinFileName), own));
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
            Open(pin.Kind == "app" ? ParsingName(pin.Aumid, pin.Path) : pin.Path);
            return;
        }
        // Незакреплённая программа: запускаем ещё один экземпляр того же, что уже работает.
        if (WindowsOf(itemId).FirstOrDefault() is { } w)
            Open(ParsingName(w.IsUWP ? w.AppUserModelID : null, w.WinFileName));
    }

    public void Pin(string itemId)
    {
        if (IsPinned(itemId) || WindowsOf(itemId).FirstOrDefault() is not { } w) return;
        var pins = _settings.Dock!;
        // Новая программа встаёт после последней закреплённой программы, перед папками.
        int index = pins.FindLastIndex(p => p.Kind == "app") + 1;
        pins.Insert(index, new DockPin { Kind = "app", Path = w.WinFileName ?? "", Aumid = w.IsUWP ? w.AppUserModelID : null, Name = DisplayName(w) });
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

    /// <summary>Новый порядок закреплённых программ после перетаскивания.</summary>
    public void Reorder(IReadOnlyList<string> itemIds)
    {
        var pins = _settings.Dock!;
        var apps = pins.Where(p => p.Kind == "app").ToList();
        var ordered = itemIds.Select(id => apps.FirstOrDefault(p => "app:" + KeyOf(p) == id)).OfType<DockPin>().Distinct().ToList();
        ordered.AddRange(apps.Except(ordered)); // на случай, если страница прислала не всё
        _settings.Dock = [.. ordered, .. pins.Where(p => p.Kind != "app")];
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

    /// <summary>Окна одной программы собираются в одну иконку по этому ключу.</summary>
    private static string KeyOf(ApplicationWindow w) =>
        w.IsUWP && !string.IsNullOrEmpty(w.AppUserModelID) ? "aumid:" + w.AppUserModelID.ToLowerInvariant()
        : "path:" + (w.WinFileName ?? "").ToLowerInvariant();

    private static string KeyOf(DockPin pin) =>
        !string.IsNullOrEmpty(pin.Aumid) ? "aumid:" + pin.Aumid.ToLowerInvariant() : "path:" + pin.Path.ToLowerInvariant();

    private static string ParsingName(string? aumid, string? path) =>
        !string.IsNullOrEmpty(aumid) ? @"shell:AppsFolder\" + aumid : path ?? "";

    private static string DisplayName(ApplicationWindow w) =>
        !string.IsNullOrWhiteSpace(w.WinFileDescription) && !w.IsUWP ? w.WinFileDescription
        : !string.IsNullOrWhiteSpace(w.Title) && w.IsUWP ? w.Title
        : System.IO.Path.GetFileNameWithoutExtension(w.WinFileName ?? "") is { Length: > 0 } file ? file : w.Title ?? "";

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
        if (e.PropertyName is nameof(ApplicationWindow.Title) or nameof(ApplicationWindow.State) or nameof(ApplicationWindow.ShowInTaskbar))
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
