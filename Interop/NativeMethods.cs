using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BarracudaTray.Interop;

internal enum HidReportType
{
    Input = 0,
    Output = 1,
    Feature = 2,
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct HidpCaps
{
    public ushort Usage;
    public ushort UsagePage;
    public ushort InputReportByteLength;
    public ushort OutputReportByteLength;
    public ushort FeatureReportByteLength;
    public fixed ushort Reserved[17];
    public ushort NumberLinkCollectionNodes;
    public ushort NumberInputButtonCaps;
    public ushort NumberInputValueCaps;
    public ushort NumberInputDataIndices;
    public ushort NumberOutputButtonCaps;
    public ushort NumberOutputValueCaps;
    public ushort NumberOutputDataIndices;
    public ushort NumberFeatureButtonCaps;
    public ushort NumberFeatureValueCaps;
    public ushort NumberFeatureDataIndices;
}

// HIDP_VALUE_CAPS; the Range/NotRange union is flattened onto the Range names
// (NotRange.Usage == UsageMin, NotRange.DataIndex == DataIndexMin).
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct HidpValueCaps
{
    public ushort UsagePage;
    public byte ReportID;
    public byte IsAlias;
    public ushort BitField;
    public ushort LinkCollection;
    public ushort LinkUsage;
    public ushort LinkUsagePage;
    public byte IsRange;
    public byte IsStringRange;
    public byte IsDesignatorRange;
    public byte IsAbsolute;
    public byte HasNull;
    public byte Reserved;
    public ushort BitSize;
    public ushort ReportCount;
    public fixed ushort Reserved2[5];
    public uint UnitsExp;
    public uint Units;
    public int LogicalMin;
    public int LogicalMax;
    public int PhysicalMin;
    public int PhysicalMax;
    public ushort UsageMin;
    public ushort UsageMax;
    public ushort StringMin;
    public ushort StringMax;
    public ushort DesignatorMin;
    public ushort DesignatorMax;
    public ushort DataIndexMin;
    public ushort DataIndexMax;
}

// HIDP_BUTTON_CAPS, union flattened the same way as HidpValueCaps.
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct HidpButtonCaps
{
    public ushort UsagePage;
    public byte ReportID;
    public byte IsAlias;
    public ushort BitField;
    public ushort LinkCollection;
    public ushort LinkUsage;
    public ushort LinkUsagePage;
    public byte IsRange;
    public byte IsStringRange;
    public byte IsDesignatorRange;
    public byte IsAbsolute;
    public ushort ReportCount;
    public ushort Reserved2;
    public fixed uint Reserved[9];
    public ushort UsageMin;
    public ushort UsageMax;
    public ushort StringMin;
    public ushort StringMax;
    public ushort DesignatorMin;
    public ushort DesignatorMax;
    public ushort DataIndexMin;
    public ushort DataIndexMax;
}

[StructLayout(LayoutKind.Sequential)]
internal struct HidpData
{
    public ushort DataIndex;
    public ushort Reserved;
    public uint RawValue;
}

internal static class NativeMethods
{
    public const int HidpStatusSuccess = 0x00110000;

    const uint GenericRead = 0x80000000;
    const uint GenericWrite = 0x40000000;
    const uint FileShareReadWrite = 0x00000003;
    const uint OpenExisting = 3;
    const uint FileFlagOverlapped = 0x40000000;

    [DllImport("hid.dll")]
    public static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_SetFeature(SafeFileHandle device, byte[] report, int length);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetFeature(SafeFileHandle device, byte[] report, int length);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetInputReport(SafeFileHandle device, byte[] report, int length);

    [DllImport("hid.dll")]
    public static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps caps);

    [DllImport("hid.dll")]
    public static extern int HidP_GetValueCaps(HidReportType type, [Out] HidpValueCaps[] caps, ref ushort length, IntPtr preparsedData);

    [DllImport("hid.dll")]
    public static extern int HidP_GetButtonCaps(HidReportType type, [Out] HidpButtonCaps[] caps, ref ushort length, IntPtr preparsedData);

    [DllImport("hid.dll")]
    public static extern int HidP_InitializeReportForID(HidReportType type, byte reportId, IntPtr preparsedData, byte[] report, uint length);

    [DllImport("hid.dll")]
    public static extern int HidP_SetData(HidReportType type, [In] HidpData[] data, ref uint dataLength, IntPtr preparsedData, byte[] report, uint length);

    [DllImport("hid.dll")]
    public static extern int HidP_SetUsageValueArray(HidReportType type, ushort usagePage, ushort linkCollection, ushort usage,
        byte[] usageValue, ushort usageValueByteLength, IntPtr preparsedData, byte[] report, uint length);

    [DllImport("hid.dll")]
    public static extern int HidP_GetUsageValue(HidReportType type, ushort usagePage, ushort linkCollection, ushort usage,
        out uint usageValue, IntPtr preparsedData, byte[] report, uint length);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClassGuid, string? deviceId, uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClassGuid, string? deviceId, char[] buffer, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    public static SafeFileHandle OpenReadWrite(string path, bool overlapped = false) =>
        CreateFileW(path, GenericRead | GenericWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting,
            overlapped ? FileFlagOverlapped : 0, IntPtr.Zero);
}
