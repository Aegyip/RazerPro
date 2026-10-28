namespace BarracudaTray;

/// <summary>
/// A short running log in %APPDATA%\BarracudaTray\log.txt, for finding out after the fact why 7.1
/// or the headset misbehaved. Kept small: it starts over once it passes 256 KB.
/// </summary>
internal static class AppLog
{
    const long MaxBytes = 256 * 1024;
    static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BarracudaTray", "log.txt");

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                    File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the app down; leave a trace next to the temp files instead.
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "BarracudaTray-log-error.txt"),
                        $"{DateTime.Now:HH:mm:ss}  {ex.GetType().Name}: {ex.Message}  ({message}){Environment.NewLine}");
                }
                catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
