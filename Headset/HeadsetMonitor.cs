using BarracudaTray.Lighting;
using Timer = System.Threading.Timer;

namespace BarracudaTray.Headset;

/// <param name="DonglePresent">The HyperSpeed dongle is plugged in.</param>
/// <param name="Linked">The headset is reachable through the dongle; false also covers "on Bluetooth" and "off". Null until known.</param>
internal sealed record HeadsetStatus(
    bool DonglePresent,
    bool? Linked,
    int? Battery = null,
    bool Charging = false,
    bool? MicMuted = null,
    bool? LightingOn = null,
    bool? DoNotDisturb = null,
    EqPreset? Eq = null,
    sbyte[]? EqCurve = null,
    bool? SidetoneOn = null,
    int? SidetoneLevel = null,
    int? PowerSavingMinutes = null)
{
    public static HeadsetStatus Absent { get; } = new(false, null);
}

/// <summary>EQ preset ids as the headset reports them (not sequential).</summary>
internal enum EqPreset : byte
{
    Game = 0x07,
    Music = 0x08,
    Movie = 0x09,
    Custom = 0xFF,
}

/// <summary>
/// Tracks the headset through its vendor events and reads or writes its settings. The dongle stays
/// enumerated while the headset is off or on Bluetooth, so USB presence alone says nothing about
/// whether commands reach the headset.
/// </summary>
internal sealed class HeadsetMonitor : IDisposable
{
    public const int SidetoneMaxLevel = 15;
    public const int EqBands = 10;

    static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(3);
    static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(1);
    static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(2);
    static readonly TimeSpan RequestSpacing = TimeSpan.FromMilliseconds(100);

    // Read after every link-up. Only properties Synapse itself queries are on this list: asking
    // for anything else (0x20) once left the dongle refusing all output reports until replugged.
    static readonly HeadsetProperty[] Settings =
    [
        HeadsetProperty.Charging,
        HeadsetProperty.EqPreset,
        HeadsetProperty.EqCurve,
        HeadsetProperty.SidetoneEnabled,
        HeadsetProperty.SidetoneLevel,
        HeadsetProperty.PowerSaving,
    ];

    readonly object _gate = new();
    readonly object _io = new(); // one request or write in flight at a time
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

    public void Set(HeadsetProperty property, params byte[] data) => Set([(property, data)]);

    /// <summary>
    /// Writes settings in the background, in order, and reads each back so the status shows what
    /// the headset actually stored.
    /// </summary>
    public void Set(IReadOnlyList<(HeadsetProperty Property, byte[] Data)> writes) => Task.Run(() =>
    {
        var channel = Channel();
        if (channel is null)
            return;
        lock (_io)
        {
            try
            {
                foreach (var (property, data) in writes)
                {
                    if (!channel.Set(property, data, WriteTimeout))
                        return;
                    Thread.Sleep(RequestSpacing);
                    channel.Request(property, WriteTimeout);
                    Thread.Sleep(RequestSpacing);
                }
            }
            catch (IOException)
            {
            }
        }
    });

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

    RazerVendorChannel? Channel()
    {
        lock (_gate)
            return _channel;
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

        // The headset only announces itself when its link comes up, so ask whether it is already there.
        // The battery reply flips Linked to true and, through OnProperty, reads the rest.
        if (Request(channel, HeadsetProperty.Battery))
            Thread.Sleep(ReplyTimeout);
        Update(s => s is { DonglePresent: true, Linked: null } ? s with { Linked = false } : s);
    }

    bool Request(RazerVendorChannel channel, HeadsetProperty property)
    {
        lock (_io)
        {
            try
            {
                return channel.Request(property, WriteTimeout);
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    /// <summary>Reads every setting once; the answers arrive as events.</summary>
    void ReadSettings() => Task.Run(() =>
    {
        foreach (var property in Settings)
        {
            if (Channel() is not { } channel || !Request(channel, property))
                return;
            Thread.Sleep(RequestSpacing);
        }
    });

    void OnProperty(HeadsetProperty property, byte[] data)
    {
        if (data.Length == 0)
            return;
        var value = data[0];
        var linkedBefore = Status.Linked == true;
        Update(s => property switch
        {
            HeadsetProperty.DongleLink => s with { Linked = value != 0 },
            // Anything else can only arrive over a live link.
            HeadsetProperty.Battery => s with { Linked = true, Battery = value },
            HeadsetProperty.Charging => s with { Linked = true, Charging = value != 0 },
            HeadsetProperty.MicMute => s with { Linked = true, MicMuted = value != 0 },
            HeadsetProperty.Lighting => s with { Linked = true, LightingOn = value != 0 },
            HeadsetProperty.DoNotDisturb => s with { Linked = true, DoNotDisturb = value != 0 },
            HeadsetProperty.EqPreset => s with { Linked = true, Eq = (EqPreset)value },
            HeadsetProperty.EqCurve when data.Length >= EqBands =>
                s with { Linked = true, EqCurve = data[..EqBands].Select(b => (sbyte)b).ToArray() },
            HeadsetProperty.SidetoneEnabled => s with { Linked = true, SidetoneOn = value != 0 },
            HeadsetProperty.SidetoneLevel => s with { Linked = true, SidetoneLevel = value },
            HeadsetProperty.PowerSaving => s with { Linked = true, PowerSavingMinutes = value },
            _ => s with { Linked = true },
        });
        if (!linkedBefore && Status.Linked == true)
            ReadSettings();
    }

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
