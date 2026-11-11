using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace BeautyDesk;

/// <summary>
/// Дирижёр приложения: держит по панели и доку на каждом экране, раздаёт им тему и язык,
/// следит за полноэкранными программами и возвращает Windows в исходный вид при выходе.
/// </summary>
internal sealed class DesktopHost
{
    private const int ExitHotKeyId = 1;
    private const uint VK_Q = 0x51;

    // Окна самой Windows, которые занимают весь экран, но полноэкранным приложением не являются.
    private static readonly HashSet<string> NotFullscreenClasses =
    [
        "Progman", "WorkerW", "Shell_TrayWnd", "XamlExplorerHostIslandWindow",
        "Windows.UI.Core.CoreWindow", "MultitaskingViewFrame", "ForegroundStaging"
    ];

    private readonly Settings _settings = Settings.Load();
    private readonly List<ShellWindow> _windows = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _displayChanged = new() { Interval = TimeSpan.FromSeconds(1) };
    private IReadOnlyList<Theme> _themes = Themes.LoadAll();
    private CoreWebView2Environment? _environment;
    private bool _paused;
    private bool _exiting;

    public async Task StartAsync()
    {
        if (_themes.Count == 0)
            throw new InvalidOperationException($"Не найдено ни одной темы в {AppPaths.BuiltInThemes}");

        ShellState.Hide(!_settings.ShowDesktopIcons);
        ApplyWallpaper();

        _environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewData);
        await BuildWindowsAsync();

        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        // Смена разрешения или подключение экрана присылает несколько событий подряд — ждём, пока утихнет.
        _displayChanged.Tick += async (_, _) => { _displayChanged.Stop(); await BuildWindowsAsync(); };
        SystemEvents.DisplaySettingsChanged += (_, _) => { _displayChanged.Stop(); _displayChanged.Start(); };

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
        foreach (var w in _windows) w.Close();
        _windows.Clear();
        ShellState.Restore();
        Application.Current.Shutdown();
    }

    private Theme CurrentTheme => _themes.FirstOrDefault(t => t.Id == _settings.Theme) ?? _themes[0];

    // ---------- окна ----------

    private async Task BuildWindowsAsync()
    {
        foreach (var w in _windows) w.Close();
        _windows.Clear();

        foreach (var monitor in Monitors.All())
        foreach (var role in new[] { ShellRole.Bar, ShellRole.Dock })
        {
            var window = new ShellWindow(role, monitor, CurrentTheme);
            window.MessageReceived += OnMessage;
            _windows.Add(window);
            await window.InitAsync(_environment!);
        }
    }

    private void Tick()
    {
        ShellState.KeepHidden(!_settings.ShowDesktopIcons);

        var fullscreen = FullscreenMonitor();
        foreach (var w in _windows)
            w.SetSuppressed(w.Monitor.Handle == fullscreen);

        bool paused = fullscreen != IntPtr.Zero;
        if (paused != _paused)
        {
            _paused = paused;
            Broadcast();
        }
    }

    /// <summary>Экран, который целиком занят активным приложением (игра, видео на весь экран), или Zero.</summary>
    private IntPtr FullscreenMonitor()
    {
        var foreground = Native.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return IntPtr.Zero;
        Native.GetWindowThreadProcessId(foreground, out var pid);
        if (pid == Environment.ProcessId) return IntPtr.Zero;
        if (NotFullscreenClasses.Contains(Native.ClassName(foreground))) return IntPtr.Zero;
        if (!Native.GetWindowRect(foreground, out var r)) return IntPtr.Zero;

        var handle = Native.MonitorFromWindow(foreground, 2 /* ближайший экран */);
        var monitor = _windows.Select(w => w.Monitor).FirstOrDefault(m => m.Handle == handle);
        if (monitor is null) return IntPtr.Zero;
        var b = monitor.Bounds;
        return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom ? handle : IntPtr.Zero;
    }

    // ---------- общение со страницами ----------

    private void OnMessage(ShellWindow window, JsonElement message)
    {
        var type = message.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "ready":
                window.Post(StateJson(window));
                break;
            case "setTheme" when message.TryGetProperty("id", out var id):
                SetTheme(id.GetString());
                break;
            case "setLanguage" when message.TryGetProperty("language", out var lang):
                SetLanguage(lang.GetString());
                break;
            case "exit":
                Exit();
                break;
        }
    }

    private void SetTheme(string? id)
    {
        _themes = Themes.LoadAll();
        if (_themes.All(t => t.Id != id)) return;
        _settings.Theme = id!;
        _settings.Save();
        foreach (var w in _windows) w.ApplyTheme(CurrentTheme);
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

    private void Broadcast()
    {
        foreach (var w in _windows) w.Post(StateJson(w));
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
            themes = _themes.Select(t => new { id = t.Id, name = t.NameFor(_settings.Language) }),
            language = _settings.Language,
            languages = Languages(),
            strings = Strings(_settings.Language),
            paused = _paused
        });
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
