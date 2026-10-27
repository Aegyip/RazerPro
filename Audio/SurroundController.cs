namespace BarracudaTray.Audio;

/// <summary>
/// Turns virtual 7.1 on and off from the UI thread: makes VB-CABLE the default playback device so
/// games send their 7.1 mix there, runs the <see cref="SurroundEngine"/>, and hands the default back
/// to the headset when switched off or on exit (otherwise the PC would go silent).
/// The cable stays enabled the whole time, renamed to "Barracuda 7.1": enabling it on demand made
/// apps such as Discord treat every switch-on as a newly plugged device and offer to move to it.
/// The communications role (Discord and other voice apps) moves to VB-CABLE too: with the headset
/// held exclusively, anything still playing to it directly would be silent.
/// </summary>
internal sealed class SurroundController : IDisposable
{
    public const string HeadsetName = "Barracuda X Chroma";

    /// <summary>What VB-CABLE's input is renamed to, so the output picker shows something meaningful.</summary>
    public const string SurroundDeviceName = "Barracuda 7.1";

    /// <summary>What VB-CABLE's output is renamed to; it only feeds the engine and is never a microphone.</summary>
    const string EngineSourceName = "Barracuda 7.1 (motor)";

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
        CableId = PrepareCable().Render;
    }

    /// <summary>The cable's playback endpoint ("Barracuda 7.1"), if VB-CABLE is installed.</summary>
    public string? CableId { get; private set; }

    /// <summary>
    /// The user picked another playback device while 7.1 was on. Switching 7.1 off must keep their
    /// pick instead of handing the default back to the headset, and the pick has to cover every
    /// role: the taskbar's list may leave communications on the cable, where Discord would go silent.
    /// </summary>
    public void HandOffDefault(string deviceId)
    {
        _ownsDefault = false;
        try
        {
            CoreAudio.SetDefaultEndpoint(deviceId);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or UnauthorizedAccessException)
        {
            AppLog.Write($"Varsayılan cihaz tüm rollere taşınamadı: {ex.Message}");
        }
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
    }

    /// <summary>
    /// Makes sure VB-CABLE's input and output are enabled and carry our names, disables any other
    /// VB-CABLE endpoint, and returns the two ids. Only changes what is not already so, so after the
    /// first run it touches nothing and apps see no device come or go.
    /// </summary>
    public static (string? Render, string? Capture) PrepareCable()
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
                    if (e.Description != SurroundDeviceName)
                        CoreAudio.Rename(e.Id, SurroundDeviceName);
                }
                if (e.Active != ours)
                    CoreAudio.SetEnabled(e.Id, ours);
            }

            string? capture = null;
            foreach (var e in CoreAudio.AllEndpoints(DataFlow.Capture).Where(e => e.Interface.Contains(CableInterface)))
            {
                var ours = e.Description is CableOutputName or EngineSourceName;
                if (ours)
                {
                    capture = e.Id;
                    if (e.Description != EngineSourceName)
                        CoreAudio.Rename(e.Id, EngineSourceName);
                }
                if (e.Active != ours)
                    CoreAudio.SetEnabled(e.Id, ours);
            }
            return (render, capture);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or UnauthorizedAccessException)
        {
            AppLog.Write($"7.1: VB-CABLE cihazları hazırlanamadı: {ex.Message}");
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
            var (cableRender, cableCapture) = PrepareCable();
            CableId = cableRender;
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
            RestoreDefault();
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
