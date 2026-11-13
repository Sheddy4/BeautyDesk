using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace BeautyDesk.Services;

/// <summary>
/// Погода для виджета. Источник — бесплатный сервис Open-Meteo: ему не нужны ни ключ, ни учётная запись.
/// Наружу уходит только название города (чтобы найти его координаты) и сами координаты.
/// </summary>
internal sealed class Weather
{
    public sealed record Day(string Date, int Code, int Max, int Min);

    /// <param name="Query">Город так, как его ввёл человек, — по нему виджет узнаёт свой ответ.</param>
    /// <param name="Error">null — всё хорошо; notFound — город не найден; offline — нет связи с сервисом.</param>
    /// <param name="Code">Код погоды по классификации ВМО (0 — ясно, 61 — дождь и так далее).</param>
    public sealed record Report(string Query, string? Error, string? Name, int Temperature, int FeelsLike, int Code,
        int Wind, int Humidity, bool IsDay, IReadOnlyList<Day> Days);

    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(15);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Dictionary<string, (DateTime At, Report Report)> _cache = [];

    public Weather()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BeautyDesk");
    }

    public async Task<Report> GetAsync(string city, string language)
    {
        string key = $"{city.Trim().ToLowerInvariant()}|{language}";
        if (_cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < Fresh) return cached.Report;

        Report report;
        try
        {
            report = await FetchAsync(city.Trim(), language);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Write($"Погода не получена: {e.Message}");
            // Старые данные лучше, чем сообщение об ошибке.
            return cached.Report ?? Failed(city, "offline");
        }
        // Ненайденный город не запоминаем надолго: возможно, это была опечатка, которую тут же исправят.
        if (report.Error is null) _cache[key] = (DateTime.UtcNow, report);
        return report;
    }

    private async Task<Report> FetchAsync(string city, string language)
    {
        var inv = CultureInfo.InvariantCulture;
        using var places = JsonDocument.Parse(await _http.GetStringAsync(
            $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language={language}&format=json"));
        if (!places.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            return Failed(city, "notFound");

        var place = results[0];
        double latitude = place.GetProperty("latitude").GetDouble(), longitude = place.GetProperty("longitude").GetDouble();
        string name = place.GetProperty("name").GetString() ?? city;

        using var forecast = JsonDocument.Parse(await _http.GetStringAsync(
            "https://api.open-meteo.com/v1/forecast" +
            $"?latitude={latitude.ToString(inv)}&longitude={longitude.ToString(inv)}" +
            "&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m,relative_humidity_2m,is_day" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min&wind_speed_unit=ms&timezone=auto&forecast_days=4"));

        var now = forecast.RootElement.GetProperty("current");
        var daily = forecast.RootElement.GetProperty("daily");
        var days = new List<Day>();
        for (int i = 0; i < daily.GetProperty("time").GetArrayLength(); i++)
        {
            days.Add(new Day(
                daily.GetProperty("time")[i].GetString() ?? "",
                daily.GetProperty("weather_code")[i].GetInt32(),
                Round(daily.GetProperty("temperature_2m_max")[i]),
                Round(daily.GetProperty("temperature_2m_min")[i])));
        }

        return new Report(city, null, name,
            Round(now.GetProperty("temperature_2m")), Round(now.GetProperty("apparent_temperature")),
            now.GetProperty("weather_code").GetInt32(), Round(now.GetProperty("wind_speed_10m")),
            Round(now.GetProperty("relative_humidity_2m")), now.GetProperty("is_day").GetInt32() == 1, days);
    }

    private static int Round(JsonElement value) => (int)Math.Round(value.GetDouble());

    private static Report Failed(string city, string error) => new(city, error, null, 0, 0, 0, 0, 0, true, []);
}
