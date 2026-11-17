using System.Diagnostics;
using System.Windows;

namespace BeautyDesk;

internal static class Program
{
    private const string SingleInstance = "BeautyDesk.SingleInstance", QuitSignal = "BeautyDesk.Quit";

    [STAThread]
    private static int Main(string[] args)
    {
        // Тот же exe в режиме сторожа: ждёт завершения основного процесса и возвращает Windows как было.
        if (args.Length == 2 && args[0] == "--watchdog" && int.TryParse(args[1], out var parentPid))
        {
            RunWatchdog(parentPid);
            return 0;
        }

        // «BeautyDesk.exe --quit» просит работающий экземпляр выйти как положено, с возвратом панели задач.
        // Так его закрывают установщик и удаление программы: простое завершение процесса убило бы и сторожа.
        if (args.Length == 1 && args[0] == "--quit")
        {
            if (EventWaitHandle.TryOpenExisting(QuitSignal, out var signal))
            {
                signal.Set();
                signal.Dispose();
                for (int i = 0; i < 50 && Mutex.TryOpenExisting(SingleInstance, out var running); i++)
                {
                    running.Dispose();
                    Thread.Sleep(100);
                }
            }
            return 0;
        }

        using var single = new Mutex(true, SingleInstance, out bool isFirst);
        if (!isFirst) return 0;

        ShellState.RestoreIfStale();
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--watchdog {Environment.ProcessId}")
        {
            UseShellExecute = false
        })?.Dispose();

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var host = new DesktopHost();

        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitSignal);
        new Thread(() =>
        {
            quit.WaitOne();
            app.Dispatcher.BeginInvoke(host.Exit);
        })
        { IsBackground = true }.Start();

        // Ошибка в одном месте интерфейса не должна оставлять человека без панели и дока: записываем её
        // и работаем дальше. Но если ошибки сыплются подряд, что-то сломано всерьёз — тогда честно падаем,
        // и сторож возвращает Windows в обычный вид.
        var recentErrors = new Queue<DateTime>();
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Write($"Необработанная ошибка: {e.Exception}");
            var now = DateTime.UtcNow;
            recentErrors.Enqueue(now);
            while (recentErrors.Count > 0 && now - recentErrors.Peek() > TimeSpan.FromSeconds(10)) recentErrors.Dequeue();
            if (recentErrors.Count <= 5) e.Handled = true;
            else ShellState.Restore();
        };
        app.SessionEnding += (_, _) => ShellState.Restore();
        app.Startup += async (_, _) =>
        {
            try
            {
                await host.StartAsync();
            }
            catch (Exception e)
            {
                Log.Write($"Запуск не удался: {e}");
                ShellState.Restore();
                app.Shutdown(1);
            }
        };

        return app.Run();
    }

    private static void RunWatchdog(int parentPid)
    {
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            parent.WaitForExit();
        }
        catch (ArgumentException)
        {
            // Основной процесс уже завершился.
        }
        ShellState.Restore(onlyIfOwnerPid: parentPid);
    }
}
