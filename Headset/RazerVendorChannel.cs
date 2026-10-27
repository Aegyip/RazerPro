using BarracudaTray.Hid;

namespace BarracudaTray.Headset;

/// <summary>
/// Razer's vendor protocol on usage page 0xFF14, report 0x02 (64 bytes). The layout comes from the
/// community notes in openrazer/openrazer#2704:
/// <code>
/// event  02 0A 60 seq 00 00 05 00 80 80 PROP 02 01 VALUE ... [62]=XOR(1..61)
/// reply  02 02 60 00  00 00 05 00 80 80 PROP 01 01 VALUE ...
/// get    02 00 60 00  00 00 04 00 00 80 PROP ...           [62]=XOR(0..61)
/// </code>
/// </summary>
internal sealed class RazerVendorChannel : IDisposable
{
    const ushort VendorPage = 0xFF14;
    const byte ReportId = 0x02;
    const int ReportLength = 64;

    readonly HidDevice _hid;
    Thread? _reader;

    /// <summary>Raised on the reader thread for every valid event or reply.</summary>
    public event Action<HeadsetProperty, byte>? PropertyReceived;

    /// <summary>Raised on the reader thread when the dongle is unplugged.</summary>
    public event Action? Closed;

    RazerVendorChannel(HidDevice hid) => _hid = hid;

    public static RazerVendorChannel? Open(ushort vendorId, ushort productId)
    {
        foreach (var path in HidDevice.FindPaths(vendorId, productId))
        {
            var hid = HidDevice.TryOpen(path);
            if (hid is { UsagePage: VendorPage, InputReportLength: ReportLength })
                return new RazerVendorChannel(hid);
            hid?.Dispose();
        }
        return null;
    }

    public void Start()
    {
        _reader ??= new Thread(Read) { IsBackground = true, Name = "Razer vendor reader" };
        _reader.Start();
    }

    /// <summary>
    /// Asks for one property; the answer arrives through <see cref="PropertyReceived"/>. Only ask for
    /// properties known to answer: an unsupported request (0x20) left the dongle refusing every later
    /// output report until it was replugged. Returns false when the dongle did not take the request.
    /// </summary>
    public bool Request(HeadsetProperty property, TimeSpan timeout)
    {
        var report = new byte[ReportLength];
        report[0] = ReportId;
        report[2] = 0x60;
        report[6] = 0x04;
        report[9] = 0x80;
        report[10] = (byte)property;
        report[62] = Xor(report, 0, 61);
        return _hid.TryWriteOutput(report, timeout);
    }

    public void Dispose() => _hid.Dispose();

    void Read()
    {
        try
        {
            while (true)
            {
                var report = _hid.ReadInput();
                if (TryParse(report, out var property, out var value))
                    PropertyReceived?.Invoke(property, value);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Closed?.Invoke();
        }
    }

    static bool TryParse(byte[] report, out HeadsetProperty property, out byte value)
    {
        property = default;
        value = 0;
        if (report.Length < ReportLength || report[0] != ReportId || report[2] != 0x60 || report[62] != Xor(report, 1, 61))
            return false;
        property = (HeadsetProperty)report[10];
        value = report[13];
        return true;
    }

    static byte Xor(byte[] data, int first, int last)
    {
        byte x = 0;
        for (var i = first; i <= last; i++)
            x ^= data[i];
        return x;
    }
}

internal enum HeadsetProperty : byte
{
    DongleLink = 0x20,
    Battery = 0x21,
    DoNotDisturb = 0x27,
    Charging = 0x2A,
    Lighting = 0x40,
    MicMute = 0x55,
}
