using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace BeautyDesk.Services;

/// <summary>
/// Что сейчас играет на компьютере. Данные берутся у Windows (те же, что на экране блокировки
/// и в быстрых настройках), поэтому работает с любым плеером, который их туда отдаёт.
/// </summary>
internal sealed class MediaInfo
{
    /// <param name="Position">Позиция в секундах на момент <paramref name="UpdatedAt"/>.</param>
    /// <param name="Duration">Длина в секундах; 0 — плеер её не сообщает.</param>
    /// <param name="UpdatedAt">Когда позиция была верной, миллисекунды Unix.</param>
    public sealed record Snapshot(string Title, string Artist, string? Cover, bool Playing, double Position, double Duration, long UpdatedAt);

    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string? _cover;
    private int _version;

    /// <summary>null — ничего не играет.</summary>
    public Snapshot? Current { get; private set; }
    public event Action? Changed;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception e)
        {
            Log.Write($"Сведения о музыке недоступны: {e.Message}");
            return;
        }
        _manager.CurrentSessionChanged += (_, _) => _ui.BeginInvoke(Attach);
        Attach();
    }

    public void Toggle() => Run(s => _ = s.TryTogglePlayPauseAsync());
    public void Next() => Run(s => _ = s.TrySkipNextAsync());
    public void Previous() => Run(s => _ = s.TrySkipPreviousAsync());
    public void Seek(double seconds) => Run(s => _ = s.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(seconds).Ticks));

    private void Run(Action<GlobalSystemMediaTransportControlsSession> command)
    {
        try { if (_session is { } s) command(s); }
        catch (Exception e) { Log.Write($"Команда плееру не выполнена: {e.Message}"); }
    }

    private void Attach()
    {
        var session = _manager!.GetCurrentSession();
        _session = session;
        if (session is not null)
        {
            // События приходят из чужого потока; старые сеансы отсекаются сравнением с текущим.
            session.MediaPropertiesChanged += (s, _) => _ui.BeginInvoke(() => { if (s == _session) Refresh(reloadCover: true); });
            session.PlaybackInfoChanged += (s, _) => _ui.BeginInvoke(() => { if (s == _session) Refresh(reloadCover: false); });
            session.TimelinePropertiesChanged += (s, _) => _ui.BeginInvoke(() => { if (s == _session) Refresh(reloadCover: false); });
        }
        Refresh(reloadCover: true);
    }

    private async void Refresh(bool reloadCover)
    {
        var session = _session;
        int version = ++_version;
        Snapshot? next = null;
        try
        {
            if (session is not null)
            {
                var props = await session.TryGetMediaPropertiesAsync();
                if (!string.IsNullOrEmpty(props?.Title))
                {
                    if (reloadCover) _cover = await EncodeCoverAsync(props.Thumbnail);
                    if (version != _version) return; // пока читали обложку, пришло более свежее обновление

                    var timeline = session.GetTimelineProperties();
                    bool playing = session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    next = new Snapshot(props.Title, props.Artist ?? "", _cover, playing,
                        timeline.Position.TotalSeconds,
                        Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds),
                        timeline.LastUpdatedTime.ToUnixTimeMilliseconds());
                }
            }
        }
        catch (Exception e)
        {
            Log.Write($"Сведения о треке не прочитаны: {e.Message}");
        }

        if (version != _version || next == Current) return;
        Current = next;
        Changed?.Invoke();
    }

    /// <summary>Обложка уменьшается до 160 px и передаётся странице как data-URL.</summary>
    private static async Task<string?> EncodeCoverAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null) return null;
        try
        {
            using var source = await thumbnail.OpenReadAsync();
            using var input = source.AsStreamForRead();
            var buffer = new MemoryStream();
            await input.CopyToAsync(buffer);
            buffer.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 160;
            image.StreamSource = buffer;
            image.EndInit();
            image.Freeze();

            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = new MemoryStream();
            encoder.Save(output);
            return "data:image/jpeg;base64," + Convert.ToBase64String(output.ToArray());
        }
        catch (Exception e)
        {
            Log.Write($"Обложка не прочитана: {e.Message}");
            return null;
        }
    }
}
