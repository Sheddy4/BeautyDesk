using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BeautyDesk.Services;
using ManagedShell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace BeautyDesk;

/// <summary>
/// Дирижёр приложения: держит окна оболочки на каждом экране, раздаёт страницам тему, язык и данные,
/// выполняет их просьбы, следит за полноэкранными программами и возвращает Windows в исходный вид при выходе.
/// </summary>
internal sealed class DesktopHost
{
    private const int ExitHotKeyId = 1;
    private const uint VK_Q = 0x51;

    // Размеры в единицах, не зависящих от масштаба экрана (как px в CSS).
    private const double DockBottomMargin = 8;
    private const double IslandWidth = 260, IslandExpandedWidth = 380, IslandExpandedHeight = 150;
    // Окно дока выше самого дока: в запас помещаются увеличенные под курсором иконки.
    private const double DockWindowHeightFactor = 2.4;
    private const double MenuWideWidth = 300;
    private const double LauncherWidth = 720, LauncherHeight = 560;
    private const int LauncherWarmIcons = 48;
    private const int TrayGridColumns = 5;
    private const double TrayGridCell = 34;
    private const double MenuWidth = 240, MenuItemHeight = 30, MenuSeparatorHeight = 9, MenuPadding = 14, MenuGap = 4;

    // Окна самой Windows, которые занимают весь экран, но полноэкранным приложением не являются.
    private static readonly HashSet<string> NotFullscreenClasses =
    [
        "Progman", "WorkerW", "Shell_TrayWnd", "XamlExplorerHostIslandWindow",
        "Windows.UI.Core.CoreWindow", "MultitaskingViewFrame", "ForegroundStaging"
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Settings _settings = Settings.Load();
    private readonly List<ShellWindow> _windows = [];
    private readonly Dictionary<ShellWindow, BackdropWindow> _dockBackdrops = [];
    private readonly Dictionary<string, string> _topics = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _displayChanged = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly SystemInfo _system = new();
    private readonly MediaInfo _media = new();
    private readonly AudioSpectrum _spectrum = new();
    private readonly HardwareInfo _hardware = new();
    private readonly Weather _weather = new();
    private readonly DispatcherTimer _hardwareTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private IReadOnlyList<Theme> _themes = Themes.LoadAll();
    private CoreWebView2Environment? _environment;
    private ShellManager? _shell;
    private TrayIcons? _tray;
    private ShellWindow? _menu;
    private readonly AppCatalog _apps = new();
    private WinKey? _winKey;
    private ShellWindow? _launcher;
    private IntPtr _launcherReturnTo;
    private bool _editingWidgets;
    private HashSet<string> _statsInUse = [];
    private int _statsTick;
    private Dock? _dock;
    private DockController? _dockController;
    private int _dockIconsVersion = -1;
    private int _tick;
    private string? _menuOwner;
    private IntPtr _fullscreenMonitor;
    private HashSet<IntPtr> _maximizedMonitors = [];
    private Native.RECT _menuAnchor;
    private Native.LowLevelMouseProc? _mouseProc;
    private IntPtr _mouseHook;
    private bool _exiting;

    public async Task StartAsync()
    {
        if (_themes.Count == 0)
            throw new InvalidOperationException($"Не найдено ни одной темы в {AppPaths.BuiltInThemes}");

        ShellState.Hide(!_settings.ShowDesktopIcons);
        ApplyWallpaper();

        // ManagedShell даёт иконки трея и список открытых окон.
        _shell = new ShellManager(ShellManager.DefaultShellConfig);
        _dock = new Dock(_shell.Tasks, _settings,
            package => _apps.Apps.FirstOrDefault(a => a.Id.StartsWith(package + "!", StringComparison.OrdinalIgnoreCase))?.Id);
        _dockController = new DockController(_dock,
            key => Strings(_settings.Language).GetValueOrDefault(key, key),
            (window, anchor, owner, items) => OpenMenu(window, anchor, owner, items, above: true));
        _dock.Changed += PublishDock;
        _dockController.PreviewShownChanged += shown => Publish("dockHold", new { value = shown });
        _tray = new TrayIcons(_shell.NotificationArea);
        _tray.Changed += () => Publish("tray", _tray.Snapshot());
        // Смена активного окна — в том числе показ рабочего стола по Win+D: виджеты сразу возвращаются на место.
        _system.Changed += () => { Publish("sys", _system.Current); KeepWidgetsOnDesktop(); };
        _media.Changed += () => { Publish("media", _media.Current); UpdateVisibility(); };
        _spectrum.Changed += levels => Publish("audio", levels);
        _hardwareTimer.Tick += (_, _) => PublishStats();

        _environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewData);
        await BuildWindowsAsync();
        await _media.StartAsync();

        Publish("tray", _tray.Snapshot());
        PublishDock();
        UpdateWidgetServices();
        Publish("media", _media.Current);
        _system.Refresh();
        Publish("sys", _system.Current);
        UpdateVisibility();

        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        // Смена разрешения или подключение экрана присылает несколько событий подряд — ждём, пока утихнет.
        _displayChanged.Tick += async (_, _) => { _displayChanged.Stop(); await BuildWindowsAsync(); UpdateVisibility(); };
        SystemEvents.DisplaySettingsChanged += (_, _) => { _displayChanged.Stop(); _displayChanged.Start(); };

        _winKey = new WinKey();
        _winKey.Pressed += ToggleLauncher;
        _apps.Loaded += PublishLauncher;
        _apps.Refresh();

        Native.RegisterHotKey(IntPtr.Zero, ExitHotKeyId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, VK_Q);
        ComponentDispatcher.ThreadPreprocessMessage += OnThreadMessage;

        Log.Write($"Запущено: экранов {Monitors.All().Count}, тема {CurrentTheme.Id}, язык {_settings.Language}");
    }

    public void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        _timer.Stop();
        Native.UnregisterHotKey(IntPtr.Zero, ExitHotKeyId);
        CloseMenu();
        foreach (var w in _windows) w.Close();
        _windows.Clear();
        foreach (var b in _dockBackdrops.Values) b.Close();
        _dockBackdrops.Clear();
        _menu?.Close();
        _launcher?.Close();
        _winKey?.Dispose();
        _dockController?.Close();
        _hardwareTimer.Stop();
        _spectrum.Dispose();
        _hardware.Dispose();
        _system.Dispose();
        _shell?.Dispose();
        ShellState.Restore();
        Application.Current.Shutdown();
    }

    private Theme CurrentTheme => _themes.FirstOrDefault(t => t.Id == _settings.Theme) ?? _themes[0];

    // ---------- окна и их места ----------

    private async Task BuildWindowsAsync()
    {
        CloseMenu();
        CloseLauncher();
        foreach (var w in _windows) w.Close();
        _windows.Clear();
        foreach (var b in _dockBackdrops.Values) b.Close();
        _dockBackdrops.Clear();
        _menu?.Close();
        _launcher?.Close();

        var monitors = Monitors.All();
        foreach (var monitor in monitors)
        foreach (var role in new[] { ShellRole.Desktop, ShellRole.Bar, ShellRole.Dock, ShellRole.Island })
        {
            var window = new ShellWindow(role, monitor);
            window.MessageReceived += OnMessage;
            _windows.Add(window);
            // «Остров» появляется, только когда что-то играет.
            await window.InitAsync(_environment!, BoundsFor(window), startHidden: role == ShellRole.Island,
                blur: role == ShellRole.Bar && CurrentTheme.Blur, tint: CurrentTheme.BlurTint);
            if (role == ShellRole.Island) ClipIsland(window);
            // Окна дока и виджетов больше того, что в них нарисовано; пока страница не сообщила,
            // где именно, окно пропускает все клики.
            if (role is ShellRole.Dock or ShellRole.Desktop) window.SetClip(new Native.RECT { Right = 1, Bottom = 1 });
            if (role == ShellRole.Dock) _dockBackdrops[window] = new BackdropWindow();
        }

        // Меню одно на все экраны: создаётся заранее, чтобы открываться без задержки.
        _menu = new ShellWindow(ShellRole.Menu, monitors[0]);
        _menu.MessageReceived += OnMessage;
        await _menu.InitAsync(_environment!, default, startHidden: true);

        _launcher = new ShellWindow(ShellRole.Launcher, monitors[0]);
        _launcher.MessageReceived += OnMessage;
        await _launcher.InitAsync(_environment!, default, startHidden: true);
    }

    private static int Px(MonitorInfo monitor, double units) => (int)Math.Round(units * monitor.Scale);

    private Native.RECT BoundsFor(ShellWindow window)
    {
        var monitor = window.Monitor;
        var m = monitor.Bounds;
        var theme = CurrentTheme;
        switch (window.Role)
        {
            case ShellRole.Bar:
                return new Native.RECT { Left = m.Left, Top = m.Top, Right = m.Right, Bottom = m.Top + Px(monitor, theme.BarHeight) };
            case ShellRole.Dock:
            {
                // Во всю ширину экрана у нижнего края; видимую часть вырезает сама страница (сообщение clip).
                int h = Px(monitor, theme.DockHeight * DockWindowHeightFactor + DockBottomMargin);
                return new Native.RECT { Left = m.Left, Top = m.Bottom - h, Right = m.Right, Bottom = m.Bottom };
            }
            case ShellRole.Desktop:
                // Весь экран под верхней панелью; видимую часть вырезает страница (сообщение clip).
                return new Native.RECT { Left = m.Left, Top = m.Top + Px(monitor, theme.BarHeight), Right = m.Right, Bottom = m.Bottom };
            case ShellRole.Island:
            {
                // Окно всегда развёрнутого размера; в свёрнутом виде от него оставлена только плашка (см. ClipIsland).
                int w = Px(monitor, IslandExpandedWidth), h = Px(monitor, IslandExpandedHeight);
                int left = m.Left + (m.Width - w) / 2;
                return new Native.RECT { Left = left, Top = m.Top, Right = left + w, Bottom = m.Top + h };
            }
            default:
                return window.Bounds;
        }
    }

    /// <summary>
    /// Свёрнутый «остров» — узкая плашка в высоту панели. Менять размер окна при наведении нельзя:
    /// страница перерисовывается рывками. Вместо этого окно обрезается, а лишняя часть пропускает клики.
    /// </summary>
    private void ClipIsland(ShellWindow island)
    {
        if (island.Expanded) { island.SetClip(null); return; }
        var monitor = island.Monitor;
        int full = Px(monitor, IslandExpandedWidth), w = Px(monitor, IslandWidth);
        int left = (full - w) / 2;
        island.SetClip(new Native.RECT { Left = left, Top = 0, Right = left + w, Bottom = Px(monitor, CurrentTheme.BarHeight) });
    }

    private void Tick()
    {
        ShellState.KeepHidden(!_settings.ShowDesktopIcons);
        _system.Refresh();
        if (_tick++ % 6 == 0) _dock?.RefreshTrash();
        ReportPointer();
        KeepWidgetsOnDesktop();

        var fullscreen = FullscreenMonitor();
        var maximized = MaximizedMonitors();
        if (fullscreen == _fullscreenMonitor && maximized.SetEquals(_maximizedMonitors)) return;
        _fullscreenMonitor = fullscreen;
        _maximizedMonitors = maximized;
        UpdateVisibility();
        // Страницы останавливают анимации при полноэкранном приложении, а док прячется при развёрнутом окне.
        Broadcast();
        UpdateWidgetServices();
    }

    /// <summary>
    /// Сообщает страницам дока, где сейчас курсор (в их координатах). Событие «курсор ушёл» в браузере
    /// иногда теряется, когда окно меняет форму; по этим данным страница замечает, что курсора уже нет.
    /// </summary>
    private void ReportPointer()
    {
        Native.GetCursorPos(out var cursor);
        foreach (var w in _windows.Where(w => w.Role == ShellRole.Dock && w.IsVisible))
        {
            var b = w.Bounds;
            double x = (cursor.X - b.Left) / w.Monitor.Scale, y = (cursor.Y - b.Top) / w.Monitor.Scale;
            w.Post(JsonSerializer.Serialize(new { type = "pointer", data = new { x, y } }, Json));
        }
    }

    private void UpdateVisibility()
    {
        foreach (var w in _windows)
        {
            bool shown = w.Monitor.Handle != _fullscreenMonitor && (w.Role != ShellRole.Island || _media.Current is not null);
            w.SetShown(shown);
            // Спрятанный док уносит с собой и размытую подложку; при показе страница пришлёт её место заново.
            if (!shown && _dockBackdrops.TryGetValue(w, out var backdrop)) backdrop.Update(null, IntPtr.Zero);
        }
        // «Остров» лежит поверх панели, а не под ней.
        foreach (var w in _windows.Where(w => w.Role == ShellRole.Island && w.IsVisible)) w.Raise();
    }

    /// <summary>Экран, который целиком занят активным приложением (игра, видео на весь экран), или Zero.</summary>
    private IntPtr FullscreenMonitor()
    {
        var foreground = Native.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return IntPtr.Zero;
        Native.GetWindowThreadProcessId(foreground, out var pid);
        // Пока активна сама оболочка (клик по иконке трея), оставляем всё как было.
        if (pid == Environment.ProcessId) return _fullscreenMonitor;
        if (NotFullscreenClasses.Contains(Native.ClassName(foreground))) return IntPtr.Zero;
        if (!Native.GetWindowRect(foreground, out var r)) return IntPtr.Zero;

        var handle = Native.MonitorFromWindow(foreground, 2 /* ближайший экран */);
        var monitor = _windows.Select(w => w.Monitor).FirstOrDefault(m => m.Handle == handle);
        if (monitor is null) return IntPtr.Zero;
        var b = monitor.Bounds;
        return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom ? handle : IntPtr.Zero;
    }

    /// <summary>
    /// Экраны, на которых поверх остальных лежит развёрнутое окно, — на них док прячется.
    /// Смотрится верхнее окно каждого экрана, а не активное: иначе на втором мониторе док
    /// висел бы поверх развёрнутого окна, пока человек работает на первом.
    /// </summary>
    private HashSet<IntPtr> MaximizedMonitors()
    {
        var decided = new HashSet<IntPtr>();   // экраны, верхнее окно которых уже найдено
        var maximized = new HashSet<IntPtr>();
        int total = _windows.Select(w => w.Monitor.Handle).Distinct().Count();

        // Окна перечисляются сверху вниз, поэтому первое подходящее окно экрана и есть верхнее.
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return true;
            if ((Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64() & Native.WS_EX_TOOLWINDOW) != 0) return true;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == Environment.ProcessId || NotFullscreenClasses.Contains(Native.ClassName(hwnd))) return true;
            // Окна с других виртуальных рабочих столов и приостановленные приложения числятся видимыми, но «скрыты» системой.
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (Native.WindowTitle(hwnd).Length == 0) return true;

            var monitor = Native.MonitorFromWindow(hwnd, 2 /* ближайший экран */);
            if (!decided.Add(monitor)) return true;
            if (Native.IsZoomed(hwnd)) maximized.Add(monitor);
            return decided.Count < total;
        }, IntPtr.Zero);
        return maximized;
    }

    // ---------- данные для страниц ----------

    /// <summary>Какие окна читают какую тему данных.</summary>
    private static ShellRole[] RolesFor(string topic) => topic switch
    {
        "media" => [ShellRole.Island, ShellRole.Desktop], // «остров» и виджет плеера
        "audio" or "hw" or "net" or "disks" => [ShellRole.Desktop],
        "launcher" => [ShellRole.Launcher],
        "dock" or "dockIcons" or "dockHold" => [ShellRole.Dock],
        "menu" => [ShellRole.Menu],
        _ => [ShellRole.Bar]
    };

    private IEnumerable<ShellWindow> AllWindows => _windows.Concat(new[] { _menu, _launcher }.OfType<ShellWindow>());

    /// <summary>Рассылает данные страницам; повтор тех же данных не отправляется.</summary>
    private void Publish(string topic, object? data)
    {
        var json = JsonSerializer.Serialize(new { type = topic, data }, Json);
        if (_topics.TryGetValue(topic, out var last) && last == json) return;
        _topics[topic] = json;
        foreach (var w in AllWindows.Where(w => RolesFor(topic).Contains(w.Role))) w.Post(json);
    }

    private void PublishDock()
    {
        if (_dock is null) return;
        var items = _dock.Items(); // заодно подгружает недостающие иконки
        if (_dock.IconsVersion != _dockIconsVersion)
        {
            _dockIconsVersion = _dock.IconsVersion;
            Publish("dockIcons", _dock.Icons);
        }
        Publish("dock", new { items });
    }

    private void Broadcast()
    {
        foreach (var w in AllWindows) w.Post(StateJson(w));
    }

    private string StateJson(ShellWindow window)
    {
        var theme = CurrentTheme;
        return JsonSerializer.Serialize(new
        {
            type = "state",
            monitor = window.Monitor.Index + 1,
            // Метка времени в адресе заставляет страницу перечитать стили после правок в папке темы.
            theme = new { id = theme.Id, css = $"{theme.CssUrl}?v={File.GetLastWriteTimeUtc(Path.Combine(theme.Dir, "theme.css")).Ticks}" },
            blur = theme.Blur,
            barHeight = theme.BarHeight,
            dockHeight = theme.DockHeight,
            dockMargin = DockBottomMargin,
            dockHidden = _maximizedMonitors.Contains(window.Monitor.Handle),
            islandWidth = IslandWidth,
            language = _settings.Language,
            strings = Strings(_settings.Language),
            widgets = window.Role == ShellRole.Desktop ? WidgetsFor(window.Monitor) : null,
            editing = window.Role == ShellRole.Desktop && _editingWidgets,
            paused = _fullscreenMonitor != IntPtr.Zero
        }, Json);
    }

    // ---------- просьбы страниц ----------

    private void OnMessage(ShellWindow window, JsonElement message)
    {
        switch (Text(message, "type"))
        {
            case "ready":
                window.Post(StateJson(window));
                foreach (var (topic, json) in _topics)
                    if (RolesFor(topic).Contains(window.Role)) window.Post(json);
                break;

            case "tray" when message.TryGetProperty("rect", out var rect):
                if (Text(message, "kind") == "down") window.TakeForeground();
                _tray?.Mouse(Number(message, "id"), Text(message, "kind") ?? "", Text(message, "button"), ToScreen(window, rect));
                // Иконка из скрытого раздела нажата — сам раздел больше не нужен.
                if (window.Role == ShellRole.Menu && Text(message, "kind") == "up") CloseMenu();
                break;

            case "trayOverflow" when message.TryGetProperty("rect", out var more) && message.TryGetProperty("ids", out var hidden) && _tray is not null:
            {
                var ids = hidden.EnumerateArray().Select(e => e.TryGetInt32(out var n) ? n : 0).ToHashSet();
                OpenTrayOverflow(window, ToScreen(window, more), _tray.Snapshot().Where(i => ids.Contains(i.Id)).ToList());
                break;
            }

            case "themeColors" when window.Role == ShellRole.Dock:
                _dockController?.ApplyTheme(ColorOf(message, "background"), ColorOf(message, "foreground"), Text(message, "font") ?? "");
                break;

            case "dock":
                _dockController?.Handle(window, message, message.TryGetProperty("rect", out var itemRect) ? ToScreen(window, itemRect) : default);
                break;

            case "clip" when window.Role == ShellRole.Dock && message.TryGetProperty("rect", out var clip):
            {
                // Страница дока сообщает, какая часть окна сейчас занята; остальное пропускает клики.
                var screen = ToScreen(window, clip);
                var b = window.Bounds;
                window.SetClip(new Native.RECT { Left = screen.Left - b.Left, Top = screen.Top - b.Top, Right = screen.Right - b.Left, Bottom = screen.Bottom - b.Top });
                break;
            }

            case "launch" when Text(message, "id") is { } appId:
                CloseLauncher(returnFocus: false);
                AppCatalog.Launch(appId);
                break;

            case "launcherClose":
                CloseLauncher();
                break;

            case "launcherIcons" when message.TryGetProperty("ids", out var wanted) && wanted.ValueKind == JsonValueKind.Array:
                _apps.LoadIcons(wanted.EnumerateArray().Select(e => e.GetString() ?? "").ToList(),
                    icons => window.Post(JsonSerializer.Serialize(new { type = "launcherIcons", data = icons }, Json)));
                break;

            case "launcherPin" when Text(message, "id") is { } pinId:
                if (!_settings.LauncherPins.Remove(pinId)) _settings.LauncherPins.Add(pinId);
                _settings.Save();
                PublishLauncher();
                break;

            case "clip" when window.Role == ShellRole.Desktop && message.TryGetProperty("rects", out var rects) && rects.ValueKind == JsonValueKind.Array:
            {
                var b = window.Bounds;
                window.SetClipParts(rects.EnumerateArray().Select(r =>
                {
                    var screen = ToScreen(window, r);
                    return new Native.RECT { Left = screen.Left - b.Left, Top = screen.Top - b.Top, Right = screen.Right - b.Left, Bottom = screen.Bottom - b.Top };
                }).ToList());
                break;
            }

            case "surface" when window.Role == ShellRole.Dock && _dockBackdrops.TryGetValue(window, out var backdrop):
                // Страница дока сообщает, где сейчас его подложка, — под неё ставится размытие.
                backdrop.SetTint(CurrentTheme.BlurTint);
                backdrop.Update(
                    CurrentTheme.Blur && window.IsVisible && message.TryGetProperty("rect", out var surface) && surface.ValueKind == JsonValueKind.Object
                        ? ToScreen(window, surface) : null,
                    window.Handle);
                break;

            case "widgetsSave" when window.Role == ShellRole.Desktop && message.TryGetProperty("widgets", out var saved):
                _settings.Widgets[window.Monitor.Device] = JsonSerializer.Deserialize<List<WidgetPlacement>>(saved.GetRawText(), Json) ?? [];
                _settings.Save();
                UpdateWidgetServices();
                break;

            case "weather" when Text(message, "city") is { Length: > 0 } city:
                _ = SendWeatherAsync(window, city);
                break;

            case "focus":
                // Виджету нужно поле ввода (город для погоды), а окно виджетов само клавиатуру не берёт.
                window.FocusKeyboard();
                break;

            case "widgetsEdit" when message.TryGetProperty("value", out var edit):
                SetWidgetEditing(edit.ValueKind == JsonValueKind.True);
                break;

            case "log":
                Log.Write($"Ошибка на странице {window.Role}: {Text(message, "text")}");
                break;

            case "volumeStep":
                _system.StepVolume(Number(message, "delta"));
                break;

            case "open":
                // Штатные панели Windows: быстрые настройки (Wi-Fi, звук, трансляция на экран) и центр уведомлений.
                OpenUri(Text(message, "target") == "notifications" ? "ms-actioncenter:" : "ms-actioncenter:controlcenter/&showFooter=true");
                break;

            case "media":
                switch (Text(message, "action"))
                {
                    case "toggle": _media.Toggle(); break;
                    case "next": _media.Next(); break;
                    case "previous": _media.Previous(); break;
                    case "seek" when message.TryGetProperty("position", out var p) && p.TryGetDouble(out var seconds): _media.Seek(seconds); break;
                }
                break;

            case "islandExpand" when window.Role == ShellRole.Island && message.TryGetProperty("value", out var value):
                window.Expanded = value.ValueKind == JsonValueKind.True;
                ClipIsland(window);
                break;

            case "openMenu" when message.TryGetProperty("rect", out var anchor):
                ToggleSystemMenu(window, ToScreen(window, anchor));
                break;

            case "menuAction":
                RunMenuAction(Text(message, "id"));
                break;
        }
    }

    /// <summary>Цвет, присланный страницей как [красный, зелёный, синий] по 0–255.</summary>
    private static System.Windows.Media.Color ColorOf(JsonElement message, string name)
    {
        byte Part(JsonElement array, int index) =>
            array.GetArrayLength() > index && array[index].TryGetDouble(out var v) ? (byte)Math.Clamp(Math.Round(v), 0, 255) : (byte)0;
        return message.TryGetProperty(name, out var c) && c.ValueKind == JsonValueKind.Array
            ? System.Windows.Media.Color.FromRgb(Part(c, 0), Part(c, 1), Part(c, 2))
            : System.Windows.Media.Colors.Black;
    }

    private static string? Text(JsonElement message, string name) =>
        message.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Number(JsonElement message, string name) =>
        message.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? (int)Math.Round(d) : 0;

    /// <summary>Переводит прямоугольник из координат страницы в координаты экрана.</summary>
    private static Native.RECT ToScreen(ShellWindow window, JsonElement rect)
    {
        double Value(string name) => rect.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : 0;
        var b = window.Bounds;
        int left = b.Left + Px(window.Monitor, Value("x")), top = b.Top + Px(window.Monitor, Value("y"));
        return new Native.RECT { Left = left, Top = top, Right = left + Px(window.Monitor, Value("width")), Bottom = top + Px(window.Monitor, Value("height")) };
    }

    private static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception e) { Log.Write($"Не открылось {uri}: {e.Message}"); }
    }

    // ---------- системное меню ----------

    private List<MenuItem> SystemMenuItems()
    {
        var s = Strings(_settings.Language);
        string T(string key) => s.GetValueOrDefault(key, key);
        return
        [
            new("theme", $"{T("menu.theme")}: {CurrentTheme.NameFor(_settings.Language)}"),
            new("language", $"{T("menu.language")}: {_settings.Language.ToUpperInvariant()}"),
            new("widgets", T(_editingWidgets ? "menu.widgetsDone" : "menu.widgets")),
            new("", Separator: true),
            new("lock", T("menu.lock")),
            new("sleep", T("menu.sleep")),
            new("restart", T("menu.restart"), Confirm: true),
            new("shutdown", T("menu.shutdown"), Confirm: true),
            new("", Separator: true),
            new("quit", T("menu.quit"))
        ];
    }

    private void ToggleSystemMenu(ShellWindow from, Native.RECT anchor) =>
        OpenMenu(from, anchor, "system", SystemMenuItems(), above: false);

    private void OpenMenu(ShellWindow from, Native.RECT anchor, string owner, List<MenuItem> items, bool above)
    {
        if (items.Count == 0) { CloseMenu(); return; }
        double height = MenuPadding + items.Sum(i => i.Separator ? MenuSeparatorHeight : MenuItemHeight);
        OpenPopup(from, anchor, owner, new { items }, above ? MenuWideWidth : MenuWidth, height, above);
    }

    /// <summary>Скрытые иконки трея: сетка иконок под стрелочкой.</summary>
    private void OpenTrayOverflow(ShellWindow from, Native.RECT anchor, IReadOnlyList<TrayIcons.Item> icons)
    {
        if (icons.Count == 0) { CloseMenu(); return; }
        int columns = Math.Min(icons.Count, TrayGridColumns);
        int rows = (icons.Count + columns - 1) / columns;
        OpenPopup(from, anchor, "tray", new { tray = icons, columns },
            columns * TrayGridCell + MenuPadding, rows * TrayGridCell + MenuPadding, above: false);
    }

    /// <summary>
    /// Открывает всплывающее окно у указанного места экрана. Повторный вызов с тем же владельцем закрывает его —
    /// так кнопка работает как переключатель. Размеры — в единицах, не зависящих от масштаба.
    /// </summary>
    private void OpenPopup(ShellWindow from, Native.RECT anchor, string owner, object content, double widthUnits, double heightUnits, bool above)
    {
        if (_menu is null) return;
        bool sameOwner = _menu.IsVisible && _menuOwner == owner;
        CloseMenu();
        if (sameOwner) return;

        var monitor = from.Monitor;
        int width = Px(monitor, widthUnits), height = Px(monitor, heightUnits), gap = Px(monitor, MenuGap);
        // Меню панели выпадает вниз от кнопки, меню дока поднимается над иконкой по её центру.
        int left = above ? anchor.Left + anchor.Width / 2 - width / 2 : anchor.Left;
        left = Math.Clamp(left, monitor.Bounds.Left + gap, Math.Max(monitor.Bounds.Left + gap, monitor.Bounds.Right - width - gap));
        int top = above ? anchor.Top - height - gap : from.Bounds.Bottom + gap;

        _menuAnchor = anchor;
        _menuOwner = owner;
        _menu.Monitor = monitor;
        _menu.SetBounds(new Native.RECT { Left = left, Top = top, Right = left + width, Bottom = top + height });
        _menu.Post(StateJson(_menu));
        _topics.Remove("menu"); // окно открывается заново — отправляем содержимое, даже если оно не изменилось
        Publish("menu", content);
        _menu.SetShown(true);
        _menu.Raise();
        WatchOutsideClicks(true);
    }

    private void CloseMenu()
    {
        _menuOwner = null;
        _menu?.SetShown(false);
        WatchOutsideClicks(_launcher is { IsVisible: true });
    }

    /// <summary>
    /// Пока открыто меню или лаунчер, следим за нажатиями мыши по всему экрану: нажатие мимо закрывает их.
    /// На потерю фокуса полагаться нельзя — окна оболочки фокус не держат.
    /// </summary>
    private void WatchOutsideClicks(bool on)
    {
        if (on == (_mouseHook != IntPtr.Zero)) return;
        if (!on)
        {
            Native.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            return;
        }
        _mouseProc = (code, wParam, lParam) =>
        {
            int message = wParam.ToInt32();
            bool pressed = message is Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN;
            if (code >= 0 && pressed)
            {
                var point = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.POINT>(lParam);
                // Нажатие на саму кнопку меню не считается: его обработает панель и закроет меню сама.
                if (_menu is { IsVisible: true } && !Contains(_menu.Bounds, point) && !Contains(_menuAnchor, point))
                    Application.Current.Dispatcher.BeginInvoke(CloseMenu);
                if (_launcher is { IsVisible: true } && !Contains(_launcher.Bounds, point))
                    Application.Current.Dispatcher.BeginInvoke(() => CloseLauncher());
            }
            return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        };
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, Native.GetModuleHandle(null), 0);
    }

    private static bool Contains(Native.RECT r, Native.POINT p) =>
        p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    private void RunMenuAction(string? id)
    {
        if (id is not null && _dockController?.RunMenuAction(id) == true)
        {
            CloseMenu();
            return;
        }

        switch (id)
        {
            // Тема и язык меняются на месте, меню остаётся открытым с обновлёнными подписями.
            case "theme":
                var ids = _themes.Select(t => t.Id).ToList();
                SetTheme(ids[(ids.IndexOf(CurrentTheme.Id) + 1) % ids.Count]);
                Publish("menu", new { items = SystemMenuItems() });
                return;
            case "language":
                var languages = Languages();
                SetLanguage(languages[(languages.IndexOf(_settings.Language) + 1) % languages.Count]);
                Publish("menu", new { items = SystemMenuItems() });
                return;
        }

        CloseMenu();
        switch (id)
        {
            case "lock": Native.LockWorkStation(); break;
            case "sleep": Native.SetSuspendState(false, false, false); break;
            case "restart": Shutdown("/r /t 0"); break;
            case "shutdown": Shutdown("/s /t 0"); break;
            case "widgets": SetWidgetEditing(!_editingWidgets); break;
            case "quit": Exit(); break;
        }
    }

    private static void Shutdown(string arguments)
    {
        try { Process.Start(new ProcessStartInfo("shutdown.exe", arguments) { CreateNoWindow = true, UseShellExecute = false })?.Dispose(); }
        catch (Exception e) { Log.Write($"Команда завершения работы не выполнена: {e.Message}"); }
    }

    // ---------- виджеты ----------

    private List<WidgetPlacement> WidgetsFor(MonitorInfo monitor)
    {
        if (!_settings.Widgets.TryGetValue(monitor.Device, out var widgets))
        {
            // Первый запуск: на основном экране сразу стоит набор виджетов, остальные экраны пустые.
            widgets = monitor.IsPrimary
                ?
                [
                    new WidgetPlacement { Id = "clock-default", Type = "clock", X = 0, Y = 0, W = 4, H = 2 },
                    new WidgetPlacement { Id = "calendar-default", Type = "calendar", X = 0, Y = 2, W = 4, H = 4 },
                    new WidgetPlacement { Id = "weather-default", Type = "weather", X = 4, Y = 0, W = 4, H = 2 },
                    new WidgetPlacement { Id = "hardware-default", Type = "hardware", X = 4, Y = 2, W = 4, H = 2 },
                    new WidgetPlacement { Id = "player-default", Type = "player", X = 4, Y = 4, W = 4, H = 3 }
                ]
                : [];
            _settings.Widgets[monitor.Device] = widgets;
        }
        return widgets;
    }

    /// <summary>
    /// Включает только те источники данных, которые нужны стоящим на столе виджетам:
    /// эквалайзер слушает звук лишь при виджете плеера, железо опрашивается лишь при своём виджете.
    /// При полноэкранном приложении всё останавливается.
    /// </summary>
    private void UpdateWidgetServices()
    {
        var inUse = _windows.Where(w => w.Role == ShellRole.Desktop)
            .SelectMany(w => WidgetsFor(w.Monitor)).Select(w => w.Type).ToHashSet();
        bool running = _fullscreenMonitor == IntPtr.Zero;

        _spectrum.SetActive(running && inUse.Contains("player"));

        _statsInUse = inUse;
        bool stats = running && (inUse.Contains("hardware") || inUse.Contains("network") || inUse.Contains("disks"));
        if (stats && !_hardwareTimer.IsEnabled)
        {
            _statsTick = 0;
            PublishStats();
            _hardwareTimer.Start();
        }
        else if (!stats) _hardwareTimer.Stop();
    }

    /// <summary>Показатели для виджетов «Монитор железа», «Сеть» и «Диски» — только для тех, что стоят на столе.</summary>
    private void PublishStats()
    {
        if (_statsInUse.Contains("hardware")) Publish("hw", _hardware.Read());
        if (_statsInUse.Contains("network")) Publish("net", _hardware.ReadNetwork());
        // Место на дисках меняется медленно — раз в полминуты достаточно.
        if (_statsInUse.Contains("disks") && _statsTick % 20 == 0) Publish("disks", HardwareInfo.ReadDisks());
        _statsTick++;
    }

    private async Task SendWeatherAsync(ShellWindow window, string city)
    {
        var report = await _weather.GetAsync(city, _settings.Language);
        window.Post(JsonSerializer.Serialize(new { type = "weather", data = report }, Json));
    }

    private void KeepWidgetsOnDesktop()
    {
        foreach (var w in _windows.Where(w => w.Role == ShellRole.Desktop)) w.KeepOnDesktop();
    }

    private void SetWidgetEditing(bool editing)
    {
        if (editing == _editingWidgets) return;
        _editingWidgets = editing;
        foreach (var w in _windows.Where(w => w.Role == ShellRole.Desktop)) w.SetAboveWindows(editing);
        // Панель, док и «остров» остаются поверх затемнённого слоя виджетов.
        foreach (var w in _windows.Where(w => w.Role != ShellRole.Desktop && w.IsVisible)) w.Raise();
        Broadcast();
    }

    // ---------- лаунчер ----------

    private void ToggleLauncher()
    {
        if (_launcher is null) return;
        if (_launcher.IsVisible) { CloseLauncher(); return; }

        CloseMenu();
        _dockController?.HidePreview();
        _apps.Refresh(); // на случай, если с прошлого раза что-то установили или удалили

        // Лаунчер открывается на том экране, где сейчас курсор.
        Native.GetCursorPos(out var cursor);
        var monitor = _windows.Select(w => w.Monitor).FirstOrDefault(m => Contains(m.Bounds, cursor)) ?? _launcher.Monitor;
        var m = monitor.Bounds;
        int width = Math.Min(Px(monitor, LauncherWidth), m.Width), height = Math.Min(Px(monitor, LauncherHeight), m.Height);
        int left = m.Left + (m.Width - width) / 2, top = m.Top + (m.Height - height) / 3;

        _launcher.Monitor = monitor;
        _launcher.SetBounds(new Native.RECT { Left = left, Top = top, Right = left + width, Bottom = top + height });
        _launcher.Post(StateJson(_launcher));
        _launcher.Post("{\"type\":\"launcherOpen\"}");
        _launcherReturnTo = Native.GetForegroundWindow();
        _launcher.ShowFocused();
        WatchOutsideClicks(true);
    }

    /// <param name="returnFocus">Вернуть клавиатуру программе, из которой лаунчер вызвали. При запуске программы
    /// из лаунчера не нужно: фокус должна получить она.</param>
    private void CloseLauncher(bool returnFocus = true)
    {
        if (_launcher is not { IsVisible: true }) return;
        _launcher.SetShown(false);
        if (returnFocus && _launcherReturnTo != IntPtr.Zero) Native.SetForegroundWindow(_launcherReturnTo);
        WatchOutsideClicks(_menu is { IsVisible: true });
    }

    private void PublishLauncher()
    {
        PublishDock(); // со списком программ док может дозагрузить иконки приложений из Магазина
        Publish("launcher", new { apps = _apps.Apps, pinned = _settings.LauncherPins });
        // Иконки первого экрана и закреплённых программ готовятся заранее, чтобы при открытии не появляться с задержкой.
        _apps.LoadIcons(_settings.LauncherPins.Concat(_apps.Apps.Where(a => !a.System).Take(LauncherWarmIcons).Select(a => a.Id)).ToList(), _ => { });
    }

    // ---------- тема, язык, обои ----------

    private void SetTheme(string? id)
    {
        _themes = Themes.LoadAll();
        if (_themes.All(t => t.Id != id)) return;
        _settings.Theme = id!;
        _settings.Save();
        foreach (var w in _windows)
        {
            w.SetBounds(BoundsFor(w));
            if (w.Role == ShellRole.Island) ClipIsland(w);
            if (w.Role == ShellRole.Bar) w.SetBlur(CurrentTheme.Blur, CurrentTheme.BlurTint);
        }
        if (!CurrentTheme.Blur) foreach (var b in _dockBackdrops.Values) b.Update(null, IntPtr.Zero);
        ApplyWallpaper();
        Broadcast();
        Log.Write($"Тема: {id}");
    }

    private void SetLanguage(string? language)
    {
        if (language is null || !Languages().Contains(language)) return;
        _settings.Language = language;
        _settings.Save();
        Broadcast();
        Log.Write($"Язык: {language}");
    }

    private void ApplyWallpaper()
    {
        var path = _settings.Wallpaper is { } custom && File.Exists(custom) ? custom : CurrentTheme.WallpaperPath;
        if (path is not null) ShellState.SetWallpaper(path);
        else ShellState.ResetWallpaper();
    }

    // ---------- переводы ----------

    private static string LocalesDir => Path.Combine(AppPaths.Ui, "locales");

    private static List<string> Languages() =>
        Directory.GetFiles(LocalesDir, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)!).Order().ToList();

    private static Dictionary<string, string> Strings(string language)
    {
        var file = Path.Combine(LocalesDir, language + ".json");
        if (!File.Exists(file)) file = Path.Combine(LocalesDir, "en.json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [];
    }

    // ---------- горячая клавиша выхода ----------

    private void OnThreadMessage(ref MSG msg, ref bool handled)
    {
        if (msg.message == Native.WM_HOTKEY && msg.wParam.ToInt32() == ExitHotKeyId)
        {
            handled = true;
            Exit();
        }
    }
}
