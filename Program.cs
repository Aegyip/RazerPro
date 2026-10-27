namespace BarracudaTray;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0)
            return Cli.Run(args);

        using var mutex = new Mutex(true, @"Local\BarracudaTray", out var firstInstance);
        if (!firstInstance)
            return 0;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
        return 0;
    }
}
