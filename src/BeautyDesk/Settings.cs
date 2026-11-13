using System.Globalization;
using System.IO;
using System.Text.Json;

using BeautyDesk.Services;

namespace BeautyDesk;

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
