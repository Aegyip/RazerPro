using System.Diagnostics;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace BarracudaTray;

/// <summary>
/// Start with Windows through a per-user Task Scheduler logon task; no admin rights needed.
/// The Run registry key was used first, but Explorer skipped that entry at every logon while it
/// started the ones next to it (seen in the Shell-Core log), and it starts Run entries one by one
/// about 30 s apart anyway. The task starts the app as soon as the user logs on.
/// </summary>
internal static class StartupRegistration
{
    const string TaskName = "Barracuda Tray";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "BarracudaTray";

    static bool? _enabled;

    /// <summary>Whether the logon task exists; checked once and then kept, since it needs a process.</summary>
    public static bool IsEnabled => _enabled ??= Schtasks($"/Query /TN \"{TaskName}\"", out _) == 0;

    /// <summary>Turns an old Run-key registration into the logon task, once.</summary>
    public static void MigrateFromRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(RunValue) is null)
            return;
        try
        {
            Set(true);
            AppLog.Write("Windows ile başlatma, Run kaydından Görev Zamanlayıcı'ya taşındı");
        }
        catch (IOException ex)
        {
            AppLog.Write($"Görev Zamanlayıcı'ya taşınamadı: {ex.Message}");
        }
    }

    public static void Set(bool enabled)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            key.DeleteValue(RunValue, throwOnMissingValue: false);

        if (!enabled)
        {
            Schtasks($"/Delete /TN \"{TaskName}\" /F", out _);
            _enabled = false;
            return;
        }

        var file = Path.Combine(Path.GetTempPath(), $"BarracudaTray-task-{Environment.ProcessId}.xml");
        File.WriteAllText(file, TaskXml(), Encoding.Unicode);
        try
        {
            if (Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F", out var output) != 0)
                throw new IOException($"Görev oluşturulamadı: {output.Trim()}");
        }
        finally
        {
            File.Delete(file);
        }
        _enabled = true;
    }

    // Priority 4 is "normal"; tasks otherwise default to below-normal, which the audio engine does not want.
    // No time limit (tasks otherwise stop after 72 h) and no AC-power requirement, so laptops start it on battery.
    static string TaskXml()
    {
        var user = SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
        var command = SecurityElement.Escape(Application.ExecutablePath);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Barracuda X Chroma: ışıklar, 7.1 ve kulaklık ayarları</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    static int Schtasks(string arguments, out string output)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start) ?? throw new IOException("schtasks.exe başlatılamadı");
        output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(10_000);
        return process.ExitCode;
    }
}
