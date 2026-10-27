namespace BarracudaTray.Audio;

/// <summary>
/// Turns virtual 7.1 on and off from the UI thread: makes VB-CABLE the default playback device so
/// games send their 7.1 mix there, runs the <see cref="SurroundEngine"/>, and hands the default back
/// to the headset when switched off or on exit (otherwise the PC would go silent). While 7.1 is off
/// the VB-CABLE devices are disabled so they stay out of Windows' device lists.
/// The communications role (Discord and other voice apps) moves to VB-CABLE too: with the headset
/// held exclusively, anything still playing to it directly would be silent.
/// </summary>
internal sealed class SurroundController : IDisposable
{
    public const string HeadsetName = "Barracuda X Chroma";

    /// <summary>What VB-CABLE's input is renamed to, so the output picker shows something meaningful.</summary>
    public const string SurroundDeviceName = "Barracuda 7.1";

    // VB-CABLE's endpoints are found by their device ("VB-Audio Virtual Cable") and their own name.
    // Its naming is from the cable's point of view: games play into "CABLE Input" (a playback
    // device) and the engine records "CABLE Output" (a recording device). Any other VB-CABLE
    // endpoint, such as "CABLE In 16ch", is unused and kept disabled.
    const string CableInterface = "VB-Audio";
    const string CableInputName = "CABLE Input";
    const string CableOutputName = "CABLE Output";

    readonly SynchronizationContext _ui;
    readonly System.Windows.Forms.Timer _retry = new() { Interval = 3000 };
    SurroundEngine? _engine;
    (bool Enabled, string Profile, bool Exclusive) _wanted;
    bool _ownsDefault;
    string _problem = "";

    /// <summary>Raised on the UI thread when <see cref="Status"/> changes for a reason other than latency.</summary>
    public event Action? Changed;

    public SurroundController(SynchronizationContext ui)
    {
        _ui = ui;
        _retry.Tick += (_, _) =>
        {
            _retry.Stop();
            Restart();
        };
    }

    public bool IsRunning => _engine is not null;

    public string Status =>
        _problem.Length > 0 ? _problem
        : _engine is { } engine ? $"≈ {engine.LatencyMs:F0} ms · {engine.Mode}"
        : "Kapalı";

    public void Apply(bool enabled, string profile, bool exclusive)
    {
        _wanted = (enabled, profile, exclusive);
        Restart();
    }

    public void Dispose()
    {
        _retry.Dispose();
        StopEngine();
        RestoreDefault();
        ShowCable(false);
    }

    /// <summary>VB-CABLE's input and output if they are currently enabled; changes nothing.</summary>
    public static (string? Render, string? Capture) FindCable()
    {
        var render = CoreAudio.AllEndpoints(DataFlow.Render)
            .FirstOrDefault(e => e.Active && e.Interface.Contains(CableInterface) && e.Description is CableInputName or SurroundDeviceName);
        var capture = CoreAudio.AllEndpoints(DataFlow.Capture)
            .FirstOrDefault(e => e.Active && e.Interface.Contains(CableInterface) && e.Description == CableOutputName);
        return (render.Id, capture.Id);
    }

    /// <summary>
    /// Enables (or disables) VB-CABLE's input and output and returns their ids; extra VB-CABLE
    /// endpoints are always disabled. The input is renamed the first time it is shown.
    /// </summary>
    public static (string? Render, string? Capture) ShowCable(bool show)
    {
        try
        {
            string? render = null;
            foreach (var e in CoreAudio.AllEndpoints(DataFlow.Render).Where(e => e.Interface.Contains(CableInterface)))
            {
                var ours = e.Description is CableInputName or SurroundDeviceName;
                if (ours)
                {
                    render = e.Id;
                    if (show && e.Description != SurroundDeviceName)
                        CoreAudio.Rename(e.Id, SurroundDeviceName);
                }
                if (e.Active != (ours && show))
                    CoreAudio.SetEnabled(e.Id, ours && show);
            }

            string? capture = null;
            foreach (var e in CoreAudio.AllEndpoints(DataFlow.Capture).Where(e => e.Interface.Contains(CableInterface)))
            {
                var ours = e.Description == CableOutputName;
                if (ours)
                    capture = e.Id;
                if (e.Active != (ours && show))
                    CoreAudio.SetEnabled(e.Id, ours && show);
            }
            return (render, capture);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or UnauthorizedAccessException)
        {
            AppLog.Write($"7.1: VB-CABLE cihazları {(show ? "açılamadı" : "gizlenemedi")}: {ex.Message}");
            return (null, null);
        }
    }

    void Restart()
    {
        _retry.Stop();
        StopEngine();
        _problem = "";
        if (!_wanted.Enabled)
        {
            RestoreDefault();
            ShowCable(false);
            Changed?.Invoke();
            return;
        }

        HrirSet hrir;
        try
        {
            hrir = HrirSet.Load(HrirLibrary.PathOf(_wanted.Profile));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Fail($"Profil yüklenemedi: {ex.Message}", retry: false);
            return;
        }

        try
        {
            var (cableRender, cableCapture) = ShowCable(true);
            if (cableRender is null || cableCapture is null)
            {
                Fail("VB-CABLE kurulu değil", retry: false);
                return;
            }
            var headset = CoreAudio.FindEndpoint(DataFlow.Render, HeadsetName);
            if (headset is null)
            {
                Fail("Kulaklığın ses cihazı bulunamadı", retry: true);
                return;
            }

            var sevenOne = EnsureSevenOne(cableRender);
            CoreAudio.SetDefaultEndpoint(cableRender);
            _ownsDefault = true;

            var engine = new SurroundEngine(cableCapture, headset, hrir, _wanted.Exclusive);
            engine.Failed += message => _ui.Post(_ =>
            {
                if (_engine == engine)
                    Fail($"Hata: {message}", retry: true);
            }, null);
            _engine = engine;
            engine.Start();
            AppLog.Write($"7.1 açıldı: profil {_wanted.Profile}, {(_wanted.Exclusive ? "özel" : "paylaşımlı")} mod, " +
                $"CABLE {(sevenOne ? "7.1" : "stereo")}");

            if (!sevenOne)
                _problem = $"Oyunlar şu an stereo gönderiyor: {SurroundDeviceName} 7.1 yapılamadı";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or ArgumentException or UnauthorizedAccessException)
        {
            Fail($"Hata: {ex.Message}", retry: true);
            return;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Games only send 7.1 when the default device's mix format has eight channels. VB-CABLE
    /// installs as stereo, so it is switched to 7.1 here instead of asking for the control panel.
    /// </summary>
    public static bool EnsureSevenOne(string cableRender)
    {
        if (CoreAudio.MixFormat(cableRender).Channels >= 8)
            return true;
        const uint SevenOne = 0x63F;
        try
        {
            CoreAudio.SetDeviceFormat(cableRender,
                new AudioFormat(8, HrirSet.SampleRate, 24, false, SevenOne),
                new AudioFormat(8, HrirSet.SampleRate, 32, true, SevenOne));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
        return CoreAudio.MixFormat(cableRender).Channels >= 8;
    }

    void Fail(string problem, bool retry)
    {
        StopEngine();
        _problem = problem;
        AppLog.Write($"7.1: {problem}{(retry ? " (3 sn sonra yeniden denenecek)" : "")}");
        if (retry)
            _retry.Start();
        else
        {
            RestoreDefault();
            ShowCable(false);
        }
        Changed?.Invoke();
    }

    void StopEngine()
    {
        _engine?.Dispose();
        _engine = null;
    }

    void RestoreDefault()
    {
        if (!_ownsDefault)
            return;
        try
        {
            if (CoreAudio.FindEndpoint(DataFlow.Render, HeadsetName) is { } headset)
                CoreAudio.SetDefaultEndpoint(headset);
            _ownsDefault = false;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or UnauthorizedAccessException)
        {
            // Left as owner so the next attempt (switching off, exiting) tries again.
        }
    }
}
