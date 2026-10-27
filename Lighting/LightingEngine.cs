using System.Diagnostics;
using Timer = System.Threading.Timer;

namespace BarracudaTray.Lighting;

internal enum LightingEffect
{
    Static,
    Breathing,
    Spectrum,
}

internal sealed record LightingState(bool Enabled, LampColor Color, int Brightness, LightingEffect Effect, bool Autonomous);

/// <summary>
/// Keeps the headset showing the current <see cref="LightingState"/>: finds it, renders effects
/// on a background timer, and reconnects after the dongle or headset drops out.
/// </summary>
internal sealed class LightingEngine : IDisposable
{
    public const ushort VendorId = 0x1532;
    public const ushort ProductId = 0x0574; // Barracuda X Chroma HyperSpeed dongle

    static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(3);
    // The headset forgets host-set colors when it powers off, so a still state is re-sent this often.
    static readonly TimeSpan StaticRefresh = TimeSpan.FromSeconds(5);
    // Light updates travel over the same 2.4 GHz link as the audio, so effects stay at 15 fps.
    static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(66);
    static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(1);
    const double BreathingPeriodSeconds = 4;
    const double SpectrumPeriodSeconds = 8;

    readonly object _gate = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Timer _timer;
    LampArrayDevice? _device;
    LightingState _state;
    bool _dirty = true;
    TimeSpan? _lastScan;
    TimeSpan _lastSend;
    bool _disposed;

    public LightingEngine(LightingState initial)
    {
        _state = initial;
        _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, PeriodFor(initial));
    }

    /// <summary>Re-sends the current state now, e.g. right after the headset links up again.</summary>
    public void Refresh() => Apply(_state);

    public void Apply(LightingState state)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _state = state;
            _dirty = true;
            _timer.Change(TimeSpan.Zero, PeriodFor(state));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
            _device?.Dispose();
            _device = null;
        }
    }

    static TimeSpan PeriodFor(LightingState state) =>
        state.Enabled && !state.Autonomous && state.Effect != LightingEffect.Static ? FrameInterval : IdleInterval;

    void Tick()
    {
        // Timer callbacks can overlap when a HID write stalls; a skipped frame is fine.
        if (!Monitor.TryEnter(_gate))
            return;
        try
        {
            if (_disposed)
                return;
            var now = _clock.Elapsed;
            if (_device is null)
            {
                if (_lastScan is { } last && now - last < RescanInterval)
                    return;
                _lastScan = now;
                _device = LampArrayDevice.Find(VendorId, ProductId);
                if (_device is null)
                    return;
                _dirty = true;
            }

            if (_state.Autonomous)
            {
                if (_dirty)
                    _device.SetAutonomous(true);
                _dirty = false;
                return;
            }

            var animated = _state.Enabled && _state.Effect != LightingEffect.Static;
            var since = now - _lastSend;
            if (animated ? since < _device.MinUpdateInterval : !_dirty && since < StaticRefresh)
                return;
            // Re-sent with every still refresh too, in case the headset power-cycled back into its own effect.
            if (!animated || _dirty)
                _device.SetAutonomous(false);
            _device.SetColors(Render(_device.LampCount, now.TotalSeconds));
            _lastSend = now;
            _dirty = false;
        }
        catch (IOException)
        {
            _device?.Dispose();
            _device = null;
            _lastScan = _clock.Elapsed;
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    LampColor[] Render(int lampCount, double seconds)
    {
        var colors = new LampColor[lampCount];
        if (!_state.Enabled)
            return colors;

        var level = Math.Clamp(_state.Brightness, 0, 100) / 100.0;
        if (_state.Effect == LightingEffect.Breathing)
            level *= 0.05 + 0.95 * (1 - Math.Cos(2 * Math.PI * seconds / BreathingPeriodSeconds)) / 2;

        for (var i = 0; i < lampCount; i++)
        {
            var color = _state.Effect == LightingEffect.Spectrum
                ? LampColor.FromHue(seconds / SpectrumPeriodSeconds * 360 + i * 360.0 / lampCount)
                : _state.Color;
            colors[i] = color.Scale(level);
        }
        return colors;
    }
}
