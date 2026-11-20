using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BeautyDesk.Services;

/// <summary>Картинки для фоторамки: список файлов в папке и уменьшенная копия нужной картинки.</summary>
internal static class Photos
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };

    public static List<string> List(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(f => Extensions.Contains(Path.GetExtension(f)))
                .Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    /// <summary>Картинка, уменьшенная до ширины <paramref name="width"/>, как data-URL. Оригинал не трогается.</summary>
    public static string? Load(string path, int width)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // файл не остаётся занятым
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = width;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = new MemoryStream();
            encoder.Save(output);
            return "data:image/jpeg;base64," + Convert.ToBase64String(output.ToArray());
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UriFormatException or ArgumentException or FileFormatException or InvalidOperationException)
        {
            Log.Write($"Фото не открылось: {Path.GetFileName(path)}: {e.Message}");
            return null;
        }
    }
}

/// <summary>
/// Курсы валют для виджета. Источник — бесплатный сервис open.er-api.com без ключа и учётной записи;
/// курсы обновляются раз в сутки. Наружу уходит только код базовой валюты.
/// </summary>
internal sealed class Currency
{
    public sealed record Rates(string Base, IReadOnlyDictionary<string, double> Values, string? Error, long UpdatedAt);

    private static readonly TimeSpan Fresh = TimeSpan.FromHours(1);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Dictionary<string, (DateTime At, Rates Rates)> _cache = [];

    public Currency() => _http.DefaultRequestHeaders.UserAgent.ParseAdd("BeautyDesk");

    public async Task<Rates> GetAsync(string baseCode)
    {
        baseCode = baseCode.ToUpperInvariant();
        if (baseCode.Length != 3 || !baseCode.All(char.IsAsciiLetterUpper)) return new Rates(baseCode, new Dictionary<string, double>(), "badCode", 0);
        if (_cache.TryGetValue(baseCode, out var cached) && DateTime.UtcNow - cached.At < Fresh) return cached.Rates;
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync($"https://open.er-api.com/v6/latest/{baseCode}"));
            var root = doc.RootElement;
            if (root.GetProperty("result").GetString() != "success") return new Rates(baseCode, new Dictionary<string, double>(), "badCode", 0);
            var values = root.GetProperty("rates").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble());
            var rates = new Rates(baseCode, values, null, root.GetProperty("time_last_update_unix").GetInt64() * 1000);
            _cache[baseCode] = (DateTime.UtcNow, rates);
            return rates;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Write($"Курсы валют не получены: {e.Message}");
            return cached.Rates ?? new Rates(baseCode, new Dictionary<string, double>(), "offline", 0);
        }
    }
}

/// <summary>Открытие ярлыков из виджета «Быстрые папки и сайты».</summary>
internal static class Links
{
    /// <summary>Открывает папку, файл или адрес сайта. Всё остальное (команды, другие схемы) не открывается.</summary>
    public static void Open(string target)
    {
        try
        {
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            else if (Directory.Exists(target) || File.Exists(target))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = false })?.Dispose();
            else
                Log.Write($"Ярлык ведёт в никуда: {target}");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { Log.Write($"Не открылось {target}: {e.Message}"); }
    }
}
