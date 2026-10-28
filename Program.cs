namespace BarracudaTray;

internal static class Program
{
    /// <summary>Set by "--exit" so the installer can close the running app cleanly (it has no window).</summary>
    public const string ExitEventName = @"Local\BarracudaTray.Exit";

    /// <summary>Set by a second launch, so the running copy can say it is already there.</summary>
    public const string ShowEventName = @"Local\BarracudaTray.Show";

    [STAThread]
    static int Main(string[] args)
    {
        if (args is ["--exit"])
            return RequestExit();
        if (args.Length > 0)
            return Cli.Run(args);

        using var mutex = new Mutex(true, @"Local\BarracudaTray", out var firstInstance);
        if (!firstInstance)
        {
            // Launched again (Start menu, a double click): the tray icon may just be hidden in the
            // taskbar's overflow, so have the running copy say so instead of exiting silently.
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show))
                using (show)
                    show.Set();
            return 0;
        }

        AppLog.Write($"Barracuda Tray {Application.ProductVersion} başladı (pid {Environment.ProcessId})");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write($"Beklenmeyen hata, uygulama kapanıyor: {e.ExceptionObject}");
        // A failing menu click or timer is logged and the app keeps running.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => AppLog.Write($"Beklenmeyen hata: {e.Exception}");

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
        AppLog.Write("Barracuda Tray kapandı");
        return 0;
    }

    static int RequestExit()
    {
        if (!EventWaitHandle.TryOpenExisting(ExitEventName, out var exit))
            return 0;
        using (exit)
            exit.Set();
        // Give the app time to hand the default playback device back before files are replaced.
        using var running = new Mutex(false, @"Local\BarracudaTray");
        try
        {
            if (running.WaitOne(TimeSpan.FromSeconds(5)))
                running.ReleaseMutex();
        }
        catch (AbandonedMutexException)
        {
        }
        return 0;
    }
}
