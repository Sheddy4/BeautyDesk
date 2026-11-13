using System.Globalization;
using System.IO;
using System.Text.Json;

using BeautyDesk.Services;

namespace BeautyDesk;

/// <summary>Один виджет на рабочем столе: что это и где стоит. Место и размер — в клетках сетки.</summary>
internal sealed class WidgetPlacement
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; } = 2;
    public int H { get; set; } = 2;
    /// <summary>Собственные настройки виджета, например город для погоды.</summary>
    public Dictionary<string, string> Options { get; set; } = [];
}

/// <summary>Пользовательские настройки, хранятся в %APPDATA%\BeautyDesk\settings.json.</summary>
internal sealed class Settings
{
    public string Theme { get; set; } = "retro";
    public string Language { get; set; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";
    public bool ShowDesktopIcons { get; set; }
    /// <summary>Своя картинка вместо обоев темы; null — брать из темы.</summary>
    public string? Wallpaper { get; set; }
    /// <summary>Закреплённое в доке; null — ещё не настраивалось, при первом запуске заполнится само.</summary>
    public List<DockPin>? Dock { get; set; }
    /// <summary>Программы, закреплённые в верхней части лаунчера (их имена в папке «Приложения»).</summary>
    public List<string> LauncherPins { get; set; } = [];
    /// <summary>Виджеты на рабочем столе, отдельно для каждого экрана (ключ — системное имя экрана).</summary>
    public Dictionary<string, List<WidgetPlacement>> Widgets { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppPaths.Settings))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.Settings)) ?? new Settings();
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            Log.Write($"Настройки не прочитаны, берём значения по умолчанию: {e.Message}");
        }
        return new Settings();
    }

    public void Save() => File.WriteAllText(AppPaths.Settings, JsonSerializer.Serialize(this, Json));
}
