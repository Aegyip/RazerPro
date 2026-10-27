namespace BarracudaTray;

internal static class Program
{
    /// <summary>Set by "--exit" so the installer can close the running app cleanly (it has no window).</summary>
    public const string ExitEventName = @"Local\BarracudaTray.Exit";

    [STAThread]
    static int Main(string[] args)
    {
        if (args is ["--exit"])
            return RequestExit();
        if (args.Length > 0)
            return Cli.Run(args);

        using var mutex = new Mutex(true, @"Local\BarracudaTray", out var firstInstance);
        if (!firstInstance)
            return 0;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
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
