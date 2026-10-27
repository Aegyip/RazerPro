using BarracudaTray.Lighting;
using Timer = System.Threading.Timer;

namespace BarracudaTray.Headset;

/// <param name="DonglePresent">The HyperSpeed dongle is plugged in.</param>
/// <param name="Linked">The headset is reachable through the dongle; false also covers "on Bluetooth" and "off". Null until known.</param>
internal sealed record HeadsetStatus(bool DonglePresent, bool? Linked, int? Battery, bool Charging, bool? MicMuted, bool? LightingOn)
{
    public static HeadsetStatus Absent { get; } = new(false, null, null, false, null, null);
}

/// <summary>
/// Tracks the headset through its vendor events. The dongle stays enumerated while the headset is
/// off or on Bluetooth, so USB presence alone says nothing about whether commands reach the headset.
/// </summary>
internal sealed class HeadsetMonitor : IDisposable
{
    static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(3);
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(1);
    static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(2);

    readonly object _gate = new();
    readonly Timer _timer;
    RazerVendorChannel? _channel;
    HeadsetStatus _status = HeadsetStatus.Absent;
    bool _disposed;

    /// <summary>Raised on a background thread whenever <see cref="Status"/> changes.</summary>
    public event Action<HeadsetStatus>? StatusChanged;

    public HeadsetMonitor() => _timer = new Timer(_ => Scan(), null, TimeSpan.Zero, RescanInterval);

    public HeadsetStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
            _channel?.Dispose();
            _channel = null;
        }
    }

    void Scan()
    {
        RazerVendorChannel channel;
        lock (_gate)
        {
            if (_disposed || _channel is not null)
                return;
            var opened = RazerVendorChannel.Open(LightingEngine.VendorId, LightingEngine.ProductId);
            if (opened is null)
                return;
            _channel = channel = opened;
        }
        channel.PropertyReceived += OnProperty;
        channel.Closed += () => OnClosed(channel);
        Update(_ => HeadsetStatus.Absent with { DonglePresent = true });
        channel.Start();

        // The headset only announces itself when its link comes up, so ask once whether it is already there.
        try
        {
            if (channel.Request(HeadsetProperty.Battery, RequestTimeout))
                Thread.Sleep(ReplyTimeout);
        }
        catch (IOException)
        {
        }
        Update(s => s is { DonglePresent: true, Linked: null } ? s with { Linked = false } : s);
    }

    void OnProperty(HeadsetProperty property, byte value) => Update(s => property switch
    {
        HeadsetProperty.DongleLink => s with { Linked = value != 0 },
        // Anything else can only arrive over a live link.
        HeadsetProperty.Battery => s with { Linked = true, Battery = value },
        HeadsetProperty.Charging => s with { Linked = true, Charging = value != 0 },
        HeadsetProperty.MicMute => s with { Linked = true, MicMuted = value != 0 },
        HeadsetProperty.Lighting => s with { Linked = true, LightingOn = value != 0 },
        _ => s with { Linked = true },
    });

    void OnClosed(RazerVendorChannel channel)
    {
        lock (_gate)
        {
            if (_channel != channel)
                return;
            _channel = null;
        }
        channel.Dispose();
        Update(_ => HeadsetStatus.Absent);
    }

    void Update(Func<HeadsetStatus, HeadsetStatus> change)
    {
        HeadsetStatus status;
        lock (_gate)
        {
            if (_disposed)
                return;
            status = change(_status);
            if (status == _status)
                return;
            _status = status;
        }
        StatusChanged?.Invoke(status);
    }
}
