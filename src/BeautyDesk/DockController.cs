using System.Text.Json;
using System.Windows.Threading;
using BeautyDesk.Services;

namespace BeautyDesk;

/// <summary>Пункт выпадающего меню. Confirm — срабатывает только со второго нажатия.</summary>
internal sealed record MenuItem(string Id, string Label = "", bool Separator = false, bool Confirm = false);

/// <summary>
/// Поведение дока со стороны программы: принимает действия страницы (клик, правый клик, наведение,
/// перетаскивание), показывает превью окон и собирает меню для иконок.
/// </summary>
internal sealed class DockController
{
    private const int PreviewShowDelayMs = 350, PreviewHideDelayMs = 250;

    private readonly Dock _dock;
    private readonly PreviewWindow _preview = new();
    private readonly DispatcherTimer _showTimer = new() { Interval = TimeSpan.FromMilliseconds(PreviewShowDelayMs) };
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(PreviewHideDelayMs) };
    private readonly Func<string, string> _translate;
    private readonly Action<ShellWindow, Native.RECT, string, List<MenuItem>> _openMenu;
    private (string ItemId, Native.RECT Anchor, MonitorInfo Monitor)? _pendingPreview;
    private (string ItemId, Native.RECT Anchor, MonitorInfo Monitor)? _shownPreview;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private string? _menuItemId;
    private bool _previewShown;

    /// <summary>Превью появилось или исчезло. Пока оно на экране, док не должен прятаться.</summary>
    public event Action<bool>? PreviewShownChanged;

    /// <param name="translate">Перевод строки интерфейса по ключу.</param>
    /// <param name="openMenu">Открыть меню над иконкой: окно дока, место иконки на экране, владелец меню, пункты.</param>
    public DockController(Dock dock, Func<string, string> translate, Action<ShellWindow, Native.RECT, string, List<MenuItem>> openMenu)
    {
        _dock = dock;
        _translate = translate;
        _openMenu = openMenu;

        _showTimer.Tick += (_, _) => { _showTimer.Stop(); ShowPendingPreview(); };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            // Сообщение страницы «курсор ушёл с иконки» может прийти уже после того, как курсор оказался
            // на превью. Поэтому перед закрытием смотрим, где курсор на самом деле.
            if (!_preview.ContainsCursor()) HidePreview();
        };
        // После закрытия окна крестиком превью перерисовывается: окно исчезает не мгновенно.
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            if (_shownPreview is { } shown && _preview.IsVisible) Show(shown);
        };
        _preview.WindowCloseRequested += handle =>
        {
            _dock.Window(handle.ToInt64().ToString())?.Close();
            Restart(_refreshTimer);
        };
        _preview.HoverChanged += inside => { if (inside) _hideTimer.Stop(); else Restart(_hideTimer); };
        _preview.WindowChosen += handle =>
        {
            _dock.Window(handle.ToInt64().ToString())?.BringToFront();
            HidePreview();
        };
    }

    /// <summary>Сообщение страницы дока: { type: "dock", action, id, rect, ids }.</summary>
    public void Handle(ShellWindow window, JsonElement message, Native.RECT anchor)
    {
        string id = message.TryGetProperty("id", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        switch (message.TryGetProperty("action", out var a) ? a.GetString() : null)
        {
            case "click":
                HidePreview();
                _dock.Activate(id);
                break;
            case "menu":
                HidePreview();
                _menuItemId = id;
                _openMenu(window, anchor, "dock:" + id, MenuFor(id));
                break;
            case "hover":
                _hideTimer.Stop();
                _pendingPreview = (id, anchor, window.Monitor);
                // Уже открытое превью переключается на соседнюю иконку сразу, без задержки.
                if (_preview.IsVisible) ShowPendingPreview(); else Restart(_showTimer);
                break;
            case "unhover":
                _showTimer.Stop();
                _pendingPreview = null;
                if (_preview.IsVisible) Restart(_hideTimer);
                break;
            case "reorder" when message.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array:
                HidePreview();
                _dock.Reorder(ids.EnumerateArray().Select(e => e.GetString() ?? "").ToList(), id);
                break;
        }
    }

    /// <summary>Выбран пункт меню иконки. Возвращает false, если пункт не относится к доку.</summary>
    public bool RunMenuAction(string actionId)
    {
        if (!actionId.StartsWith("dock.") || _menuItemId is not { } item) return false;
        switch (actionId)
        {
            case "dock.open": _dock.Launch(item); break;
            case "dock.pin": _dock.Pin(item); break;
            case "dock.unpin": _dock.Unpin(item); break;
            case "dock.close": _dock.CloseWindows(item); break;
            case "dock.emptyTrash": Dock.EmptyTrash(); break;
            default:
                if (actionId.StartsWith("dock.window:")) _dock.Window(actionId["dock.window:".Length..])?.BringToFront();
                break;
        }
        return true;
    }

    public void HidePreview()
    {
        _showTimer.Stop();
        _hideTimer.Stop();
        _refreshTimer.Stop();
        _pendingPreview = null;
        _shownPreview = null;
        _preview.HidePreview();
        SetPreviewShown(false);
    }

    public void Close() => _preview.Close();

    public void ApplyTheme(System.Windows.Media.Color background, System.Windows.Media.Color foreground, string fontFamily) =>
        _preview.ApplyTheme(background, foreground, fontFamily);

    private List<MenuItem> MenuFor(string itemId)
    {
        var items = new List<MenuItem>();
        if (itemId == "trash")
        {
            items.Add(new("dock.open", _translate("dock.open")));
            items.Add(new("dock.emptyTrash", _translate("dock.emptyTrash")));
            return items;
        }

        var windows = _dock.WindowsOf(itemId);
        foreach (var w in windows)
            items.Add(new("dock.window:" + w.Handle.ToInt64(), string.IsNullOrWhiteSpace(w.Title) ? "—" : w.Title));
        if (windows.Count > 0) items.Add(new("", Separator: true));

        items.Add(new("dock.open", _translate(windows.Count > 0 ? "dock.newWindow" : "dock.open")));
        // Папки в доке пока только закреплённые; закрепить можно запущенную программу.
        if (_dock.IsPinned(itemId)) items.Add(new("dock.unpin", _translate("dock.unpin")));
        else if (windows.Count > 0) items.Add(new("dock.pin", _translate("dock.pin")));
        if (windows.Count > 0) items.Add(new("dock.close", _translate(windows.Count > 1 ? "dock.closeAll" : "dock.close")));
        return items;
    }

    private void ShowPendingPreview()
    {
        if (_pendingPreview is { } pending) Show(pending);
    }

    private void Show((string ItemId, Native.RECT Anchor, MonitorInfo Monitor) target)
    {
        var windows = _dock.WindowsOf(target.ItemId).Select(w => (w.Handle, w.Title ?? "")).ToList();
        if (windows.Count == 0) { HidePreview(); return; }
        _shownPreview = target;
        _preview.ShowFor(windows, target.Anchor, target.Monitor);
        SetPreviewShown(true);
    }

    private void SetPreviewShown(bool shown)
    {
        if (shown == _previewShown) return;
        _previewShown = shown;
        PreviewShownChanged?.Invoke(shown);
    }

    private static void Restart(DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }
}
