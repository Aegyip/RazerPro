namespace BarracudaTray.Audio;

/// <summary>
/// Turns virtual 7.1 on and off from the UI thread: makes VB-CABLE the default playback device so
/// games send their 7.1 mix there, runs the <see cref="SurroundEngine"/>, and hands the default back
/// to the headset when switched off or on exit (otherwise the PC would go silent).
/// </summary>
internal sealed class SurroundController : IDisposable
{
    // VB-CABLE also adds a "CABLE In 16ch" playback device; the plain "CABLE Input" is the one used.
    public const string CableRenderName = "CABLE Input (VB-Audio";
    public const string CableCaptureName = "CABLE Output (VB-Audio";
    public const string HeadsetName = "Barracuda X Chroma";

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
            // VB-CABLE's naming is from the cable's point of view: games play into "CABLE Input"
            // (a render endpoint) and the engine records "CABLE Output" (a capture endpoint).
            var cableRender = CoreAudio.FindEndpoint(DataFlow.Render, CableRenderName);
            var cableCapture = CoreAudio.FindEndpoint(DataFlow.Capture, CableCaptureName);
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

            if (CoreAudio.MixFormat(cableRender).Channels < 8)
                _problem = "Oyunlar şu an stereo gönderiyor: CABLE Input'u 7.1 yap (Ses → Yapılandır)";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or ArgumentException or UnauthorizedAccessException)
        {
            Fail($"Hata: {ex.Message}", retry: true);
            return;
        }
        Changed?.Invoke();
    }

    void Fail(string problem, bool retry)
    {
        StopEngine();
        _problem = problem;
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
