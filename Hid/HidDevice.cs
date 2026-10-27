using System.Runtime.InteropServices;
using BarracudaTray.Interop;
using Microsoft.Win32.SafeHandles;

namespace BarracudaTray.Hid;

/// <summary>One feature-report field as Windows' HID parser sees it.</summary>
internal sealed record HidField(
    byte ReportId,
    ushort UsagePage,
    ushort Usage,
    ushort LinkCollection,
    ushort LinkUsage,
    ushort DataIndex,
    ushort BitSize,
    ushort ReportCount,
    int LogicalMax,
    bool IsButton)
{
    /// <summary>A single usage repeated ReportCount times; written with HidP_SetUsageValueArray.</summary>
    public bool IsArray => !IsButton && ReportCount > 1;

    public override string ToString() =>
        $"rid=0x{ReportId:X2} page=0x{UsagePage:X2} usage=0x{Usage:X2} link=0x{LinkUsage:X2} idx={DataIndex} " +
        $"bits={BitSize} count={ReportCount} max={LogicalMax}{(IsButton ? " button" : "")}";
}

/// <summary>
/// A single HID top-level collection opened for feature reports. Reports are built with the
/// HidP_* parser calls so field offsets always come from the device's own descriptor.
/// </summary>
internal sealed class HidDevice : IDisposable
{
    readonly SafeFileHandle _handle;
    readonly IntPtr _preparsed;
    FileStream? _input;
    FileStream? _output;

    public string Path { get; }
    public ushort UsagePage { get; }
    public ushort Usage { get; }
    public int InputReportLength { get; }
    public int OutputReportLength { get; }
    public int FeatureReportLength { get; }
    public IReadOnlyList<HidField> FeatureFields { get; }

    HidDevice(string path, SafeFileHandle handle, IntPtr preparsed)
    {
        Path = path;
        _handle = handle;
        _preparsed = preparsed;
        Check(NativeMethods.HidP_GetCaps(preparsed, out var caps), "HidP_GetCaps");
        UsagePage = caps.UsagePage;
        Usage = caps.Usage;
        InputReportLength = caps.InputReportByteLength;
        OutputReportLength = caps.OutputReportByteLength;
        FeatureReportLength = caps.FeatureReportByteLength;
        FeatureFields = ReadFeatureFields(caps.NumberFeatureValueCaps, caps.NumberFeatureButtonCaps);
    }

    public static IReadOnlyList<string> FindPaths(ushort vendorId, ushort productId)
    {
        NativeMethods.HidD_GetHidGuid(out var guid);
        if (NativeMethods.CM_Get_Device_Interface_List_SizeW(out var size, ref guid, null, 0) != 0 || size <= 1)
            return [];
        var buffer = new char[size];
        if (NativeMethods.CM_Get_Device_Interface_ListW(ref guid, null, buffer, size, 0) != 0)
            return [];

        var needle = $"vid_{vendorId:x4}&pid_{productId:x4}";
        return new string(buffer)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Returns null for collections Windows keeps for itself (keyboard, consumer control, ...).</summary>
    public static HidDevice? TryOpen(string path)
    {
        var handle = NativeMethods.OpenReadWrite(path);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        if (!NativeMethods.HidD_GetPreparsedData(handle, out var preparsed))
        {
            handle.Dispose();
            return null;
        }
        return new HidDevice(path, handle, preparsed);
    }

    public byte[] CreateFeatureReport(byte reportId)
    {
        var report = new byte[FeatureReportLength];
        Check(NativeMethods.HidP_InitializeReportForID(HidReportType.Feature, reportId, _preparsed, report, (uint)report.Length),
            "HidP_InitializeReportForID");
        return report;
    }

    /// <summary>Writes single-value fields and buttons by data index. A button with value 0 is left off.</summary>
    public void SetData(byte[] report, IEnumerable<(HidField Field, uint Value)> values)
    {
        var data = values
            .Where(v => !v.Field.IsButton || v.Value != 0)
            .Select(v => new HidpData { DataIndex = v.Field.DataIndex, RawValue = v.Value })
            .ToArray();
        if (data.Length == 0)
            return;
        var count = (uint)data.Length;
        Check(NativeMethods.HidP_SetData(HidReportType.Feature, data, ref count, _preparsed, report, (uint)report.Length),
            "HidP_SetData");
    }

    public void SetArray(byte[] report, HidField field, ReadOnlySpan<uint> values)
    {
        if (field.BitSize % 8 != 0)
            throw new NotSupportedException($"{field.BitSize}-bit array fields are not supported");
        var bytesPerValue = field.BitSize / 8;
        var buffer = new byte[bytesPerValue * field.ReportCount];
        for (var i = 0; i < values.Length && i < field.ReportCount; i++)
            for (var b = 0; b < bytesPerValue; b++)
                buffer[i * bytesPerValue + b] = (byte)(values[i] >> (8 * b));
        Check(NativeMethods.HidP_SetUsageValueArray(HidReportType.Feature, field.UsagePage, field.LinkCollection, field.Usage,
            buffer, (ushort)buffer.Length, _preparsed, report, (uint)report.Length), "HidP_SetUsageValueArray");
    }

    /// <summary>
    /// Byte offset of a single-value field, found by writing it into a blank report. Needed because
    /// Windows numbers the usages of one main item in reverse, so data indices don't follow the layout.
    /// </summary>
    public int ProbeOffset(HidField field)
    {
        var blank = CreateFeatureReport(field.ReportId);
        var probe = (byte[])blank.Clone();
        SetData(probe, [(field, 1u)]);
        for (var i = 1; i < probe.Length; i++)
            if (probe[i] != blank[i])
                return i;
        throw new IOException($"Could not locate field {field}");
    }

    public uint GetValue(byte[] report, HidField field)
    {
        Check(NativeMethods.HidP_GetUsageValue(HidReportType.Feature, field.UsagePage, field.LinkCollection, field.Usage,
            out var value, _preparsed, report, (uint)report.Length), "HidP_GetUsageValue");
        return value;
    }

    /// <summary>Called with every feature report before it is sent; used by the CLI to show traffic.</summary>
    public static Action<byte[]>? Trace { get; set; }

    /// <summary>
    /// Report ID plus the size of every field carrying that ID. Windows pads feature reports to the
    /// collection's longest one, which some firmware rejects, so reports are sent at this length.
    /// </summary>
    public int ReportLength(byte reportId) =>
        1 + (FeatureFields.Where(f => f.ReportId == reportId).Sum(f => f.BitSize * f.ReportCount) + 7) / 8;

    public void SendFeature(byte[] report)
    {
        var length = Math.Min(report.Length, ReportLength(report[0]));
        Trace?.Invoke(report[..length]);
        if (!NativeMethods.HidD_SetFeature(_handle, report, length))
            throw new IOException($"HidD_SetFeature(0x{report[0]:X2}) failed: {Marshal.GetLastPInvokeErrorMessage()}");
    }

    public byte[] ReadFeature(byte reportId)
    {
        var report = new byte[FeatureReportLength];
        report[0] = reportId;
        if (!NativeMethods.HidD_GetFeature(_handle, report, report.Length))
            throw new IOException($"HidD_GetFeature(0x{reportId:X2}) failed: {Marshal.GetLastPInvokeErrorMessage()}");
        return report;
    }

    /// <summary>
    /// Sends an output report, giving up after <paramref name="timeout"/>. A write the device never
    /// accepts would otherwise block forever and leave the process unkillable, so it goes through a
    /// separate overlapped handle whose pending I/O can be cancelled.
    /// </summary>
    public bool TryWriteOutput(byte[] report, TimeSpan timeout)
    {
        Trace?.Invoke(report);
        if (_output is null)
        {
            var handle = NativeMethods.OpenReadWrite(Path, overlapped: true);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new IOException($"Cannot open {Path} for writing: {Marshal.GetLastPInvokeErrorMessage()}");
            }
            _output = new FileStream(handle, FileAccess.Write, 0, isAsync: true);
        }
        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            _output.WriteAsync(report, cancel.Token).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Asks the device for its current input report instead of waiting for one.</summary>
    public byte[]? TryGetInputReport(byte reportId)
    {
        var report = new byte[InputReportLength];
        report[0] = reportId;
        return NativeMethods.HidD_GetInputReport(_handle, report, report.Length) ? report : null;
    }

    /// <summary>Blocks until the device sends an input report.</summary>
    public byte[] ReadInput()
    {
        // The stream owns nothing extra: disposing the device closes the same handle.
        _input ??= new FileStream(_handle, FileAccess.Read, 0, false);
        var report = new byte[InputReportLength];
        var read = _input.Read(report);
        return report[..read];
    }

    public void Dispose()
    {
        _output?.Dispose();
        NativeMethods.HidD_FreePreparsedData(_preparsed);
        _handle.Dispose();
    }

    List<HidField> ReadFeatureFields(ushort valueCapCount, ushort buttonCapCount)
    {
        var fields = new List<HidField>();

        if (valueCapCount > 0)
        {
            var caps = new HidpValueCaps[valueCapCount];
            var count = valueCapCount;
            Check(NativeMethods.HidP_GetValueCaps(HidReportType.Feature, caps, ref count, _preparsed), "HidP_GetValueCaps");
            foreach (var c in caps.AsSpan(0, count))
            {
                var max = c.LogicalMax > c.LogicalMin ? c.LogicalMax : (int)Math.Min(int.MaxValue, (1L << c.BitSize) - 1);
                if (c.IsRange != 0)
                {
                    for (var i = 0; i <= c.UsageMax - c.UsageMin; i++)
                        fields.Add(new HidField(c.ReportID, c.UsagePage, (ushort)(c.UsageMin + i), c.LinkCollection, c.LinkUsage,
                            (ushort)(c.DataIndexMin + i), c.BitSize, 1, max, false));
                }
                else
                {
                    fields.Add(new HidField(c.ReportID, c.UsagePage, c.UsageMin, c.LinkCollection, c.LinkUsage,
                        c.DataIndexMin, c.BitSize, c.ReportCount, max, false));
                }
            }
        }

        if (buttonCapCount > 0)
        {
            var caps = new HidpButtonCaps[buttonCapCount];
            var count = buttonCapCount;
            Check(NativeMethods.HidP_GetButtonCaps(HidReportType.Feature, caps, ref count, _preparsed), "HidP_GetButtonCaps");
            foreach (var c in caps.AsSpan(0, count))
            {
                var last = c.IsRange != 0 ? c.UsageMax - c.UsageMin : 0;
                for (var i = 0; i <= last; i++)
                    fields.Add(new HidField(c.ReportID, c.UsagePage, (ushort)(c.UsageMin + i), c.LinkCollection, c.LinkUsage,
                        (ushort)(c.DataIndexMin + i), 1, 1, 1, true));
            }
        }

        // Data indices follow descriptor order, which is what repeated usages are matched by.
        return fields.OrderBy(f => f.ReportId).ThenBy(f => f.DataIndex).ToList();
    }

    static void Check(int status, string function)
    {
        if (status != NativeMethods.HidpStatusSuccess)
            throw new IOException($"{function} failed: 0x{status:X8}");
    }
}
