using BarracudaTray.Hid;

namespace BarracudaTray.Lighting;

/// <summary>
/// A HID LampArray (usage page 0x59) driven straight through feature reports. Unlike the
/// Windows.Devices.Lights API this needs no foreground window or Dynamic Lighting registration.
/// </summary>
internal sealed class LampArrayDevice : IDisposable
{
    const ushort LampArrayPage = 0x59;

    // Usage IDs from the HID Usage Tables, LampArray page.
    const ushort UsageLampArray = 0x01;
    const ushort UsageLampCount = 0x03;
    const ushort UsageMinUpdateInterval = 0x08;
    const ushort UsageLampId = 0x21;
    const ushort UsageRed = 0x51;
    const ushort UsageGreen = 0x52;
    const ushort UsageBlue = 0x53;
    const ushort UsageIntensity = 0x54;
    const ushort UsageUpdateFlags = 0x55;
    const ushort UsageAutonomousMode = 0x71;

    const uint LampUpdateComplete = 0x01;

    readonly HidDevice _hid;
    readonly byte _updateReportId;
    readonly Channel _count;
    readonly Channel _flags;
    readonly Channel _ids;
    readonly Channel _red;
    readonly Channel _green;
    readonly Channel _blue;
    readonly Channel? _intensity;
    readonly HidField _autonomous;
    readonly int _lampsPerReport;

    public int LampCount { get; }
    public TimeSpan MinUpdateInterval { get; }
    public string Path => _hid.Path;

    LampArrayDevice(HidDevice hid)
    {
        _hid = hid;
        var fields = hid.FeatureFields.Where(f => f.UsagePage == LampArrayPage).ToList();

        // LampMultiUpdateReport. The Barracuda X Chroma ignores LampRangeUpdateReport, so ranges are never used.
        _updateReportId = Require(fields, UsageRed).ReportId;
        var update = fields.Where(f => f.ReportId == _updateReportId).ToList();
        _count = Channel.Of(hid, update, UsageLampCount);
        _flags = Channel.Of(hid, update, UsageUpdateFlags);
        _ids = Channel.Of(hid, update, UsageLampId);
        _red = Channel.Of(hid, update, UsageRed);
        _green = Channel.Of(hid, update, UsageGreen);
        _blue = Channel.Of(hid, update, UsageBlue);
        _intensity = Channel.TryOf(hid, update, UsageIntensity);
        _lampsPerReport = new[] { _ids.Slots, _red.Slots, _green.Slots, _blue.Slots }.Min();

        _autonomous = Require(fields, UsageAutonomousMode);

        var lampCount = fields.FirstOrDefault(f => f.Usage == UsageLampCount && f.ReportId != _updateReportId)
            ?? throw new IOException("LampArrayAttributesReport not found");
        var attributes = hid.ReadFeature(lampCount.ReportId);
        LampCount = (int)hid.GetValue(attributes, lampCount);
        var interval = fields.FirstOrDefault(f => f.Usage == UsageMinUpdateInterval && f.ReportId == lampCount.ReportId);
        MinUpdateInterval = interval is null ? TimeSpan.Zero : TimeSpan.FromMicroseconds(hid.GetValue(attributes, interval));
    }

    public static LampArrayDevice? Find(ushort vendorId, ushort productId)
    {
        foreach (var path in HidDevice.FindPaths(vendorId, productId))
        {
            var hid = HidDevice.TryOpen(path);
            if (hid is null)
                continue;
            if (hid.UsagePage != LampArrayPage || hid.Usage != UsageLampArray)
            {
                hid.Dispose();
                continue;
            }
            try
            {
                return new LampArrayDevice(hid);
            }
            catch
            {
                hid.Dispose();
                throw;
            }
        }
        return null;
    }

    /// <summary>Autonomous mode hands the lamps back to the headset's own firmware effect.</summary>
    public void SetAutonomous(bool autonomous)
    {
        var report = _hid.CreateFeatureReport(_autonomous.ReportId);
        _hid.SetData(report, [(_autonomous, autonomous ? 1u : 0u)]);
        _hid.SendFeature(report);
    }

    /// <summary>Sets every lamp; <paramref name="colors"/> holds one entry per lamp id.</summary>
    public void SetColors(ReadOnlySpan<LampColor> colors)
    {
        for (var start = 0; start < LampCount; start += _lampsPerReport)
        {
            var n = Math.Min(_lampsPerReport, LampCount - start);
            var ids = new uint[n];
            var red = new uint[n];
            var green = new uint[n];
            var blue = new uint[n];
            var intensity = new uint[n];
            for (var i = 0; i < n; i++)
            {
                var c = colors[start + i];
                ids[i] = (uint)(start + i);
                red[i] = _red.Scale(c.R);
                green[i] = _green.Scale(c.G);
                blue[i] = _blue.Scale(c.B);
                intensity[i] = _intensity?.Scale(255) ?? 0;
            }

            var report = _hid.CreateFeatureReport(_updateReportId);
            var data = new List<(HidField, uint)>();
            _count.Write(_hid, report, [(uint)n], data);
            _flags.Write(_hid, report, [start + n >= LampCount ? LampUpdateComplete : 0], data);
            _ids.Write(_hid, report, ids, data);
            _red.Write(_hid, report, red, data);
            _green.Write(_hid, report, green, data);
            _blue.Write(_hid, report, blue, data);
            _intensity?.Write(_hid, report, intensity, data);
            _hid.SetData(report, data);
            _hid.SendFeature(report);
        }
    }

    public byte[] ReadFeature(byte reportId) => _hid.ReadFeature(reportId);

    public void Dispose() => _hid.Dispose();

    static HidField Require(List<HidField> fields, ushort usage) =>
        fields.FirstOrDefault(f => f.Usage == usage) ?? throw new IOException($"LampArray usage 0x{usage:X2} not found");

    /// <summary>
    /// One per-lamp quantity of LampMultiUpdateReport. Descriptors declare it either as one usage
    /// with a report count (an array) or as the usage repeated once per lamp slot.
    /// </summary>
    sealed class Channel
    {
        readonly List<HidField> _fields;

        Channel(List<HidField> fields) => _fields = fields;

        public int Slots => _fields.Count == 1 ? _fields[0].ReportCount : _fields.Count;

        public static Channel Of(HidDevice hid, List<HidField> fields, ushort usage) =>
            TryOf(hid, fields, usage) ?? throw new IOException($"LampMultiUpdateReport usage 0x{usage:X2} not found");

        /// <summary>Repeated usages are put in slot order by where they sit in the report.</summary>
        public static Channel? TryOf(HidDevice hid, List<HidField> fields, ushort usage)
        {
            var matches = fields.Where(f => f.Usage == usage).ToList();
            if (matches.Count > 1)
                matches = matches.OrderBy(hid.ProbeOffset).ToList();
            return matches.Count == 0 ? null : new Channel(matches);
        }

        public uint Scale(byte value) => (uint)(value * (long)_fields[0].LogicalMax / 255);

        public void Write(HidDevice hid, byte[] report, ReadOnlySpan<uint> values, List<(HidField, uint)> data)
        {
            if (_fields.Count == 1 && _fields[0].IsArray)
            {
                hid.SetArray(report, _fields[0], values);
                return;
            }
            for (var i = 0; i < values.Length; i++)
                data.Add((_fields[i], values[i]));
        }
    }
}

internal readonly record struct LampColor(byte R, byte G, byte B)
{
    public static LampColor Off => default;

    public LampColor Scale(double k) =>
        new((byte)Math.Round(R * k), (byte)Math.Round(G * k), (byte)Math.Round(B * k));

    public static LampColor FromHue(double hue)
    {
        var h = (hue % 360 + 360) % 360 / 60;
        var x = (byte)Math.Round(255 * (1 - Math.Abs(h % 2 - 1)));
        return (int)h switch
        {
            0 => new(255, x, 0),
            1 => new(x, 255, 0),
            2 => new(0, 255, x),
            3 => new(0, x, 255),
            4 => new(x, 0, 255),
            _ => new(255, 0, x),
        };
    }

    public static bool TryParse(string? hex, out LampColor color)
    {
        color = default;
        var s = hex?.TrimStart('#');
        if (s is not { Length: 6 } || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v))
            return false;
        color = new((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}
