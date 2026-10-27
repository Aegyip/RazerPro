using System.Runtime.InteropServices;

namespace BarracudaTray.Audio;

// Minimal WASAPI / MMDevice interop. Interfaces list every method in vtable order; methods the
// app never calls keep their slot with a placeholder signature.

internal enum DataFlow
{
    Render = 0,
    Capture = 1,
}

internal enum Role
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

internal enum ShareMode
{
    Shared = 0,
    Exclusive = 1,
}

internal static class CoreAudio
{
    public const uint DeviceStateActive = 0x1;
    public const uint ClsctxAll = 0x17;
    public const uint StreamFlagsEventCallback = 0x00040000;
    public const uint BufferFlagsSilent = 0x2;
    public const int ErrorBufferSizeNotAligned = unchecked((int)0x88890019);

    public static readonly Guid IidAudioClient3 = new("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42");
    public static readonly Guid IidAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    public static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    static readonly PropertyKey FriendlyNameKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    public static IMMDeviceEnumerator CreateEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

    public static IEnumerable<(string Id, string Name)> Endpoints(IMMDeviceEnumerator enumerator, DataFlow flow)
    {
        var devices = enumerator.EnumAudioEndpoints(flow, DeviceStateActive);
        for (uint i = 0; i < devices.GetCount(); i++)
        {
            var device = devices.Item(i);
            yield return (device.GetId(), FriendlyName(device));
        }
    }

    public static string FriendlyName(IMMDevice device)
    {
        var store = device.OpenPropertyStore(0);
        var key = FriendlyNameKey;
        store.GetValue(ref key, out var value);
        try
        {
            return value.VarType == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.Pointer) ?? "" : "";
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    /// <summary>First active endpoint whose name contains <paramref name="name"/>; call from any one thread.</summary>
    public static string? FindEndpoint(DataFlow flow, string name)
    {
        var enumerator = CreateEnumerator();
        try
        {
            return Endpoints(enumerator, flow).FirstOrDefault(d => d.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).Id;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    public static AudioFormat MixFormat(string deviceId)
    {
        var enumerator = CreateEnumerator();
        var device = enumerator.GetDevice(deviceId);
        var client = Activate(device);
        try
        {
            return AudioFormat.ReadAndFree(client.GetMixFormat());
        }
        finally
        {
            Marshal.ReleaseComObject(client);
            Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>
    /// Flips a capture endpoint's mute and returns the new state. On the headset this becomes the
    /// USB Audio Class mute request Synapse sends, and the headset mirrors it in its mic-mute event.
    /// </summary>
    public static bool ToggleMute(string deviceId)
    {
        var enumerator = CreateEnumerator();
        var device = enumerator.GetDevice(deviceId);
        var iid = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
        var volume = (IAudioEndpointVolume)device.Activate(ref iid, ClsctxAll, IntPtr.Zero);
        try
        {
            var context = Guid.Empty;
            var mute = !volume.GetMute();
            volume.SetMute(mute, ref context);
            return mute;
        }
        finally
        {
            Marshal.ReleaseComObject(volume);
            Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    public static IAudioClient3 Activate(IMMDevice device)
    {
        var iid = IidAudioClient3;
        return (IAudioClient3)device.Activate(ref iid, ClsctxAll, IntPtr.Zero);
    }

    public static void SetDefaultEndpoint(string deviceId)
    {
        var policy = (IPolicyConfig)new PolicyConfigComObject();
        try
        {
            // Communications is left alone so voice apps keep the headset itself.
            policy.SetDefaultEndpoint(deviceId, Role.Console);
            policy.SetDefaultEndpoint(deviceId, Role.Multimedia);
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PropVariant value);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey(Guid formatId, uint propertyId)
{
    public Guid FormatId = formatId;
    public uint PropertyId = propertyId;
}

// PROPVARIANT is 24 bytes on x64; only the type tag and the pointer member are read.
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort VarType;
    [FieldOffset(8)] public IntPtr Pointer;
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject;

[ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
internal class PolicyConfigComObject;

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    IMMDeviceCollection EnumAudioEndpoints(DataFlow dataFlow, uint stateMask);
    IMMDevice GetDefaultAudioEndpoint(DataFlow dataFlow, Role role);
    IMMDevice GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id);
    void RegisterEndpointNotificationCallback(IntPtr client);
    void UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    uint GetCount();
    IMMDevice Item(uint index);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object Activate(ref Guid iid, uint clsCtx, IntPtr activationParams);

    IPropertyStore OpenPropertyStore(uint access);

    [return: MarshalAs(UnmanagedType.LPWStr)]
    string GetId();

    uint GetState();
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    uint GetCount();
    PropertyKey GetAt(uint index);
    void GetValue(ref PropertyKey key, out PropVariant value);
    void SetValue(ref PropertyKey key, ref PropVariant value);
    void Commit();
}

/// <summary>IAudioClient, IAudioClient2 and IAudioClient3 in one vtable.</summary>
[ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient3
{
    void Initialize(ShareMode shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
    uint GetBufferSize();
    long GetStreamLatency();
    uint GetCurrentPadding();
    [PreserveSig]
    int IsFormatSupported(ShareMode shareMode, IntPtr format, IntPtr closestMatch);
    IntPtr GetMixFormat();
    void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    void Start();
    void Stop();
    void Reset();
    void SetEventHandle(IntPtr eventHandle);

    [return: MarshalAs(UnmanagedType.IUnknown)]
    object GetService(ref Guid iid);

    void IsOffloadCapable(int category, out int offloadCapable);
    void SetClientProperties(IntPtr properties);
    void GetBufferSizeLimits(IntPtr format, int eventDriven, out long minDuration, out long maxDuration);

    void GetSharedModeEnginePeriod(IntPtr format, out uint defaultPeriod, out uint fundamentalPeriod, out uint minPeriod, out uint maxPeriod);
    void GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint currentPeriod);
    void InitializeSharedAudioStream(uint streamFlags, uint periodInFrames, IntPtr format, IntPtr audioSessionGuid);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    void RegisterControlChangeNotify(IntPtr notify);
    void UnregisterControlChangeNotify(IntPtr notify);
    uint GetChannelCount();
    void SetMasterVolumeLevel(float levelDb, ref Guid context);
    void SetMasterVolumeLevelScalar(float level, ref Guid context);
    float GetMasterVolumeLevel();
    float GetMasterVolumeLevelScalar();
    void SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
    void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
    float GetChannelVolumeLevel(uint channel);
    float GetChannelVolumeLevelScalar(uint channel);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);

    [return: MarshalAs(UnmanagedType.Bool)]
    bool GetMute();
}

[ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMeterInformation
{
    float GetPeakValue();
}

[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    IntPtr GetBuffer(uint framesRequested);
    void ReleaseBuffer(uint framesWritten, uint flags);
}

[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    void GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    void ReleaseBuffer(uint frames);
    uint GetNextPacketSize();
}

// Undocumented but stable since Vista; it is how the Sound control panel sets the default device.
[ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    void GetMixFormat();
    void GetDeviceFormat();
    void ResetDeviceFormat();
    void SetDeviceFormat();
    void GetProcessingPeriod();
    void SetProcessingPeriod();
    void GetShareMode();
    void SetShareMode();
    void GetPropertyValue();
    void SetPropertyValue();
    void SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
    void SetEndpointVisibility();
}
