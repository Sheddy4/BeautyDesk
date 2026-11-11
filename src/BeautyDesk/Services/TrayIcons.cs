using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ManagedShell.WindowsTray;

namespace BeautyDesk.Services;

/// <summary>
/// Иконки трея чужих программ для верхней панели. Сами иконки и пересылку кликов даёт ManagedShell;
/// здесь они превращаются в данные для страницы и обратно — в клики по настоящим иконкам.
/// </summary>
internal sealed class TrayIcons
{
    public sealed record Item(int Id, string Title, string? Icon);

    // Громкость, сеть и питание панель рисует сама — системные дубли не показываем.
    private static readonly HashSet<Guid> OwnIndicators =
    [
        Guid.Parse(NotificationArea.VOLUME_GUID), Guid.Parse(NotificationArea.NETWORK_GUID), Guid.Parse(NotificationArea.POWER_GUID)
    ];

    private readonly NotificationArea _area;
    private readonly Dictionary<NotifyIcon, int> _ids = [];
    private readonly ConditionalWeakTable<ImageSource, string> _encoded = [];
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private int _nextId = 1;

    /// <summary>Набор иконок, их картинки или подсказки изменились.</summary>
    public event Action? Changed;

    public TrayIcons(NotificationArea area)
    {
        _area = area;
        // Некоторые программы обновляют подсказку каждую секунду — собираем изменения в пачку.
        _debounce.Tick += (_, _) => { _debounce.Stop(); Changed?.Invoke(); };

        foreach (var icon in _area.TrayIcons) Track(icon);
        _area.TrayIcons.CollectionChanged += OnCollectionChanged;
    }

    public IReadOnlyList<Item> Snapshot() =>
        _area.TrayIcons
            .Where(i => !i.IsHidden && !OwnIndicators.Contains(i.GUID))
            .Select(i => new Item(_ids[i], i.Title ?? "", Encode(i.Icon)))
            .ToList();

    /// <summary>Передаёт действие мыши настоящей иконке.</summary>
    /// <param name="kind">enter, leave, down или up.</param>
    /// <param name="placement">Где иконка нарисована на экране — программа открывает меню рядом.</param>
    public void Mouse(int id, string kind, string? button, Native.RECT placement)
    {
        var icon = _ids.FirstOrDefault(p => p.Value == id).Key;
        if (icon is null) return;

        var mouseButton = button == "right" ? MouseButton.Right : button == "middle" ? MouseButton.Middle : MouseButton.Left;
        uint cursor = Native.CursorParam();
        int doubleClick = (int)Native.GetDoubleClickTime();
        switch (kind)
        {
            case "enter":
                icon.Placement = new ManagedShell.Interop.NativeMethods.Rect
                {
                    Left = placement.Left, Top = placement.Top, Right = placement.Right, Bottom = placement.Bottom
                };
                icon.IconMouseEnter(cursor);
                break;
            case "leave": icon.IconMouseLeave(cursor); break;
            case "down": icon.IconMouseDown(mouseButton, cursor, doubleClick); break;
            case "up": icon.IconMouseUp(mouseButton, cursor, doubleClick); break;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (NotifyIcon icon in e.OldItems)
            {
                icon.PropertyChanged -= OnIconChanged;
                _ids.Remove(icon);
            }
        if (e.NewItems is not null)
            foreach (NotifyIcon icon in e.NewItems) Track(icon);
        Schedule();
    }

    private void Track(NotifyIcon icon)
    {
        if (_ids.ContainsKey(icon)) return;
        _ids[icon] = _nextId++;
        icon.PropertyChanged += OnIconChanged;
    }

    private void OnIconChanged(object? sender, PropertyChangedEventArgs e) => Schedule();

    private void Schedule()
    {
        if (!_debounce.IsEnabled) _debounce.Start();
    }

    private string? Encode(ImageSource? source)
    {
        if (source is not BitmapSource bitmap) return null;
        if (_encoded.TryGetValue(source, out var cached)) return cached;
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            var url = "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
            _encoded.Add(source, url);
            return url;
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
