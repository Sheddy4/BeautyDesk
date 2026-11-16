using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace BeautyDesk.Services;

/// <summary>
/// Спектр звука, который сейчас играет на компьютере, — для полос эквалайзера в виджете плеера.
/// Слушается то, что идёт в колонки или наушники; микрофон не используется, ничего не записывается.
/// </summary>
internal sealed class AudioSpectrum : IDisposable
{
    public const int Bands = 24;

    // Окно анализа длинное (около 85 мс), чтобы различать низкие частоты, но сдвигается понемногу,
    // поэтому полосы обновляются так же часто, как и при коротком окне.
    private const int FftPower = 12, FftSize = 1 << FftPower, Hop = 1024;
    private const double LowestHz = 40, HighestHz = 16000;
    private const float FloorDb = -70, RangeDb = 60; // что тише — считается тишиной; столько децибел занимает вся высота полосы

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly object _gate = new();
    private readonly float[] _samples = new float[FftSize];
    private readonly float[] _latest = new float[Bands];
    private readonly float[] _shown = new float[Bands];
    private readonly MMDeviceEnumerator _devices = new();
    private WasapiLoopbackCapture? _capture;
    private string? _deviceId; // устройство вывода, которое сейчас слушаем
    private int _filled;
    private bool _wanted;
    private bool _silent = true;
    private int _retryIn;

    /// <summary>Новые высоты полос, каждая от 0 до 1. Приходит около 30 раз в секунду, пока звук есть.</summary>
    public event Action<float[]>? Changed;

    public AudioSpectrum()
    {
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Включает или выключает прослушивание. Выключено — когда виджета плеера нет или открыто полноэкранное приложение.</summary>
    public void SetActive(bool active)
    {
        _wanted = active;
        if (active)
        {
            Start();
            _timer.Start();
        }
        else
        {
            Stop();
            _timer.Stop();
            Array.Clear(_shown);
            if (!_silent) { _silent = true; Changed?.Invoke(new float[Bands]); }
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        Stop();
        _devices.Dispose();
    }

    private void Start()
    {
        if (_capture is not null) return;
        try
        {
            if (!_devices.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return;
            var device = _devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var capture = new WasapiLoopbackCapture(device);
            capture.DataAvailable += OnData;
            // Устройство вывода отключили — захват останавливается сам; позже попробуем начать заново.
            capture.RecordingStopped += (_, _) => { if (ReferenceEquals(_capture, capture)) Stop(); };
            capture.StartRecording();
            _capture = capture;
            _deviceId = device.ID;
            Log.Write($"Эквалайзер слушает «{device.FriendlyName}»: {capture.WaveFormat.SampleRate} Гц, каналов {capture.WaveFormat.Channels}");
        }
        catch (Exception e)
        {
            Log.Write($"Звук для эквалайзера недоступен: {e.Message}");
            _capture = null;
        }
    }

    /// <summary>
    /// Звук могли переключить на другое устройство (подключили монитор с колонками, наушники).
    /// Старое при этом никуда не делось и просто молчит, поэтому сравниваем с текущим устройством по умолчанию.
    /// </summary>
    private bool OutputDeviceChanged()
    {
        try
        {
            if (!_devices.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return false;
            using var current = _devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return current.ID != _deviceId;
        }
        catch (Exception) { return false; }
    }

    private void Stop()
    {
        var capture = _capture;
        _capture = null;
        if (capture is null) return;
        try
        {
            capture.DataAvailable -= OnData;
            capture.StopRecording();
            capture.Dispose();
        }
        catch (Exception e) { Log.Write($"Захват звука остановлен с ошибкой: {e.Message}"); }
        lock (_gate) { Array.Clear(_latest); _filled = 0; }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (sender is not WasapiLoopbackCapture capture) return;
        var format = capture.WaveFormat;
        if (format.BitsPerSample != 32 || format.Channels < 1) return; // общий формат Windows — 32-битные числа с плавающей точкой
        int frame = 4 * format.Channels;

        lock (_gate)
        {
            for (int offset = 0; offset + frame <= e.BytesRecorded; offset += frame)
            {
                float mono = 0;
                for (int c = 0; c < format.Channels; c++) mono += BitConverter.ToSingle(e.Buffer, offset + c * 4);
                _samples[_filled++] = mono / format.Channels;
                if (_filled < FftSize) continue;
                Analyse(format.SampleRate);
                Array.Copy(_samples, Hop, _samples, 0, FftSize - Hop);
                _filled = FftSize - Hop;
            }
        }
    }

    private void Analyse(int sampleRate)
    {
        var fft = new Complex[FftSize];
        for (int i = 0; i < FftSize; i++)
            fft[i].X = (float)(_samples[i] * FastFourierTransform.HannWindow(i, FftSize));
        FastFourierTransform.FFT(true, FftPower, fft);

        // Полосы распределены по логарифмической шкале: так слышит ухо, и низкие частоты не сливаются в одну полосу.
        double ratio = Math.Pow(HighestHz / LowestHz, 1.0 / Bands);
        for (int band = 0; band < Bands; band++)
        {
            double fromHz = LowestHz * Math.Pow(ratio, band), toHz = fromHz * ratio;
            int from = Math.Clamp((int)(fromHz * FftSize / sampleRate), 1, FftSize / 2 - 1);
            int to = Math.Clamp((int)(toHz * FftSize / sampleRate), from + 1, FftSize / 2);
            float peak = 0;
            for (int bin = from; bin < to; bin++)
                peak = Math.Max(peak, MathF.Sqrt(fft[bin].X * fft[bin].X + fft[bin].Y * fft[bin].Y));
            float db = 20 * MathF.Log10(peak + 1e-9f);
            _latest[band] = Math.Max(_latest[band], Math.Clamp((db - FloorDb) / RangeDb, 0, 1));
        }
    }

    private void Tick()
    {
        // Раз в пару секунд: если звук переключили на другое устройство или захват оборвался — начинаем заново.
        if (_wanted && --_retryIn <= 0)
        {
            _retryIn = 60;
            if (_capture is not null && OutputDeviceChanged()) Stop();
            if (_capture is null) Start();
        }

        bool any = false;
        lock (_gate)
        {
            for (int i = 0; i < Bands; i++)
            {
                // Вверх полоса прыгает сразу, вниз опускается плавно.
                _shown[i] = Math.Max(_latest[i], _shown[i] * 0.84f);
                _latest[i] *= 0.5f; // в тишине Windows перестаёт присылать звук — значения должны гаснуть сами
                if (_shown[i] < 0.01f) _shown[i] = 0;
                any |= _shown[i] > 0;
            }
        }
        if (!any && _silent) return;
        _silent = !any;
        Changed?.Invoke(_shown.Select(v => MathF.Round(v, 2)).ToArray());
    }
}
