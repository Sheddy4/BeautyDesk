using System.IO;
using System.Text.Json;

namespace BeautyDesk;

/// <summary>
/// Тема — папка с theme.json (название, размеры, обои) и theme.css (цвета, шрифты, стили).
/// Встроенные темы лежат рядом с программой, пользовательские — в %APPDATA%\BeautyDesk\themes.
/// </summary>
internal sealed record Theme(
    string Id,
    string Dir,
    bool IsUser,
    IReadOnlyDictionary<string, string> Names,
    double BarHeight,
    double DockWidth,
    double DockHeight,
    string? WallpaperPath)
{
    public const string BuiltInHost = "themes.beautydesk.example";
    public const string UserHost = "userthemes.beautydesk.example";

    public string CssUrl => $"https://{(IsUser ? UserHost : BuiltInHost)}/{Id}/theme.css";

    public string NameFor(string language) =>
        Names.TryGetValue(language, out var name) ? name
        : Names.TryGetValue("en", out var en) ? en
        : Id;
}

internal static class Themes
{
    /// <summary>Читает все темы с диска заново — так правки в папке темы подхватываются при переключении.</summary>
    public static IReadOnlyList<Theme> LoadAll()
    {
        var list = new List<Theme>();
        foreach (var (root, isUser) in new[] { (AppPaths.BuiltInThemes, false), (AppPaths.UserThemes, true) })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root).Order())
            {
                var theme = TryLoad(dir, isUser);
                // Пользовательская тема не может подменить встроенную с тем же именем папки.
                if (theme is not null && list.All(t => t.Id != theme.Id)) list.Add(theme);
            }
        }
        return list;
    }

    private static Theme? TryLoad(string dir, bool isUser)
    {
        var manifest = Path.Combine(dir, "theme.json");
        if (!File.Exists(manifest) || !File.Exists(Path.Combine(dir, "theme.css"))) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = doc.RootElement;

            var names = new Dictionary<string, string>();
            if (root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.Object)
                foreach (var p in name.EnumerateObject())
                    names[p.Name] = p.Value.GetString() ?? "";

            string? wallpaper = null;
            if (root.TryGetProperty("wallpaper", out var w) && w.GetString() is { Length: > 0 } file)
            {
                var full = Path.GetFullPath(Path.Combine(dir, file));
                // Обои должны лежать внутри папки темы.
                if (full.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar) && File.Exists(full))
                    wallpaper = full;
            }

            return new Theme(
                Id: Path.GetFileName(dir),
                Dir: dir,
                IsUser: isUser,
                Names: names,
                BarHeight: Number(root, "barHeight", 30, 20, 80),
                DockWidth: Number(root, "dockWidth", 560, 200, 2000),
                DockHeight: Number(root, "dockHeight", 64, 32, 200),
                WallpaperPath: wallpaper);
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
        {
            Log.Write($"Тема в {dir} пропущена: {e.Message}");
            return null;
        }
    }

    private static double Number(JsonElement root, string key, double fallback, double min, double max) =>
        root.TryGetProperty(key, out var v) && v.TryGetDouble(out var d) ? Math.Clamp(d, min, max) : fallback;
}
