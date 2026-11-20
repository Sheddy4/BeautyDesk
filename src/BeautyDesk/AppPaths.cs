using System.IO;

namespace BeautyDesk;

/// <summary>Где лежат файлы приложения и пользователя.</summary>
internal static class AppPaths
{
    /// <summary>Настройки и пользовательские темы: %APPDATA%\BeautyDesk.</summary>
    public static readonly string Roaming = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeautyDesk"));

    /// <summary>Служебные данные этой машины (лог, файл восстановления, кэш WebView2): %LOCALAPPDATA%\BeautyDesk.</summary>
    public static readonly string Local = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeautyDesk"));

    public static readonly string Ui = Path.Combine(AppContext.BaseDirectory, "ui");
    public static readonly string BuiltInThemes = Path.Combine(AppContext.BaseDirectory, "themes");
    public static readonly string UserThemes = Ensure(Path.Combine(Roaming, "themes"));
    /// <summary>Сторонние виджеты: *.js или папка с widget.js. Подгружаются на рабочий стол сами.</summary>
    public static readonly string UserWidgets = Ensure(Path.Combine(Roaming, "widgets"));
    public static readonly string Settings = Path.Combine(Roaming, "settings.json");
    public static readonly string RestoreState = Path.Combine(Local, "restore.json");
    public static readonly string WebViewData = Path.Combine(Local, "WebView2");
    public static readonly string LogFile = Path.Combine(Local, "log.txt");

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}

internal static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}");
        }
        catch { /* лог не должен ронять приложение */ }
    }
}
