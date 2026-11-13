using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace BeautyDesk.Services;

/// <summary>
/// Все установленные программы — тот же список, что показывает меню «Пуск», включая приложения
/// из Магазина. Берётся из системной папки «Приложения» (shell:AppsFolder).
/// Чтение списка и иконок идёт в отдельных потоках, чтобы интерфейс не замирал.
/// </summary>
internal sealed class AppCatalog
{
    /// <param name="Id">Имя программы внутри папки «Приложения»; по нему она запускается и получает иконку.</param>
    public sealed record App(string Id, string Name);

    private const string AppsFolder = "shell:AppsFolder";

    // В папке «Приложения» лежат и справка, и ссылки на сайты, и программы удаления — в лаунчере они лишние.
    private static readonly string[] SkippedEndings = [".url", ".chm", ".txt", ".html", ".htm", ".pdf", ".rtf"];
    private static readonly string[] SkippedWords = ["uninstall", "удалить", "удаление", "деинсталл"];

    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<string, string> _icons = [];
    private bool _loading;

    public IReadOnlyList<App> Apps { get; private set; } = [];
    /// <summary>Список программ прочитан (или перечитан).</summary>
    public event Action? Loaded;

    /// <summary>Перечитывает список программ в фоне.</summary>
    public void Refresh()
    {
        if (_loading) return;
        _loading = true;
        RunInBackground(() =>
        {
            var apps = ReadApps();
            _ui.BeginInvoke(() =>
            {
                _loading = false;
                if (apps is null) return;
                Apps = apps;
                Loaded?.Invoke();
            });
        });
    }

    /// <summary>Загружает иконки для указанных программ и отдаёт их одним пакетом (id -> data-URL).</summary>
    public void LoadIcons(IReadOnlyList<string> ids, Action<Dictionary<string, string>> done)
    {
        var ready = new Dictionary<string, string>();
        var missing = new List<string>();
        foreach (var id in ids.Distinct())
        {
            if (_icons.TryGetValue(id, out var cached)) ready[id] = cached;
            else missing.Add(id);
        }
        if (missing.Count == 0) { done(ready); return; }

        RunInBackground(() =>
        {
            var loaded = new Dictionary<string, string>();
            foreach (var id in missing)
                if (IconLoader.DataUrl($@"{AppsFolder}\{id}", 64) is { } icon) loaded[id] = icon;
            _ui.BeginInvoke(() =>
            {
                foreach (var (id, icon) in loaded) _icons[id] = ready[id] = icon;
                done(ready);
            });
        });
    }

    public static void Launch(string id)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppsFolder}\\{id}\"") { UseShellExecute = false })?.Dispose(); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { Log.Write($"Не запустилось {id}: {e.Message}"); }
    }

    private static List<App>? ReadApps()
    {
        try
        {
            if (Type.GetTypeFromProgID("Shell.Application") is not { } type) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                var apps = new List<App>();
                foreach (dynamic item in shell.NameSpace(AppsFolder).Items())
                {
                    string name = item.Name, id = item.Path;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                    if (SkippedEndings.Any(e => id.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;
                    if (SkippedWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
                    apps.Add(new App(id, name));
                }
                return apps
                    .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                    .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            finally { Marshal.ReleaseComObject(shell); }
        }
        catch (Exception e)
        {
            Log.Write($"Список программ не прочитан: {e.Message}");
            return null;
        }
    }

    /// <summary>Объекты оболочки Windows требуют потока с однопоточной моделью COM.</summary>
    private static void RunInBackground(Action work)
    {
        var thread = new Thread(() =>
        {
            try { work(); }
            catch (Exception e) { Log.Write($"Фоновая работа со списком программ: {e.Message}"); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}
