using BarracudaTray.Hid;

namespace BarracudaTray.Headset;

/// <summary>
/// Razer's vendor protocol on usage page 0xFF14, report 0x02 (64 bytes). The layout comes from the
/// community notes in openrazer/openrazer#2704:
/// <code>
/// event  02 0A 60 seq 00 00 LEN 00 80 80 PROP 02 N DATA... [62]=XOR(1..61)
/// reply  02 02 60 00  00 00 LEN 00 80 80 PROP 01 N DATA...
/// get    02 00 60 00  00 00 04  00 00 80 PROP 00 00         [62]=XOR(0..61)
/// set    02 00 60 00  00 00 LEN 00 00 80 PROP|80 00 N DATA...
/// </code>
/// LEN counts the bytes from offset 9 to the end of DATA (4 + N); byte 11 tells request (00),
/// reply (01) and event (02) apart.
/// </summary>
internal sealed class RazerVendorChannel : IDisposable
{
    const ushort VendorPage = 0xFF14;
    const byte ReportId = 0x02;
    const int ReportLength = 64;

    readonly HidDevice _hid;
    Thread? _reader;

    /// <summary>Raised on the reader thread for every valid event or reply, with the property's data bytes.</summary>
    public event Action<HeadsetProperty, byte[]>? PropertyReceived;

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
    public bool Request(HeadsetProperty property, TimeSpan timeout) => Send((byte)property, [], timeout);

    /// <summary>
    /// Writes one property (request id | 0x80). Only for properties seen written by Synapse in the
    /// community captures; the headset answers with an event carrying the stored value.
    /// </summary>
    public bool Set(HeadsetProperty property, ReadOnlySpan<byte> data, TimeSpan timeout) =>
        Send((byte)((byte)property | 0x80), data, timeout);

    bool Send(byte property, ReadOnlySpan<byte> data, TimeSpan timeout)
    {
        var report = new byte[ReportLength];
        report[0] = ReportId;
        report[2] = 0x60;
        report[6] = (byte)(4 + data.Length);
        report[9] = 0x80;
        report[10] = property;
        report[12] = (byte)data.Length;
        data.CopyTo(report.AsSpan(13));
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
                if (TryParse(report, out var property, out var data))
                    PropertyReceived?.Invoke(property, data);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Closed?.Invoke();
        }
    }

    static bool TryParse(byte[] report, out HeadsetProperty property, out byte[] data)
    {
        property = default;
        data = [];
        if (report.Length < ReportLength || report[0] != ReportId || report[2] != 0x60 || report[62] != Xor(report, 1, 61))
            return false;
        // A write is acknowledged under its own id (property | 0x80) with a status byte, not the value.
        if ((report[10] & 0x80) != 0)
            return false;
        property = (HeadsetProperty)report[10];
        data = report[13..Math.Min(13 + report[12], 62)];
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
    EqPreset = 0x13,
    EqCurve = 0x15,
    SidetoneEnabled = 0x18,
    SidetoneLevel = 0x19,
    DongleLink = 0x20,
    Battery = 0x21,
    DoNotDisturb = 0x27,
    Charging = 0x2A,
    PowerSaving = 0x2C,
    Lighting = 0x40,
    MicMute = 0x55,
}
