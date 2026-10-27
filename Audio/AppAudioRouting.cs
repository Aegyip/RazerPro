using System.Runtime.InteropServices;

namespace BarracudaTray.Audio;

/// <summary>
/// Per-app output devices ("App volume and device preferences" in Settings). An app pinned to the
/// headset ignores the default device, so it bypasses 7.1 and, while the engine holds the headset
/// in exclusive mode, goes silent. This is the "Reset" button of the Volume mixer, minus the volumes.
/// </summary>
internal static class AppAudioRouting
{
    const string ClassName = "Windows.Media.Internal.AudioPolicyConfig";

    // The interface id changed in Windows 10 21H2; the older one is kept for earlier builds.
    static readonly Guid[] FactoryIids =
    [
        new("ab3d4648-e242-459f-b02f-541c70306324"),
        new("2a59116d-6c4f-45e0-a74f-707e3fef9258"),
    ];

    /// <summary>Removes every app's pinned audio device so all apps follow the default again.</summary>
    public static void ClearAll()
    {
        var factory = Create();
        try
        {
            Marshal.ThrowExceptionForHR(factory.ClearAllPersistedApplicationDefaultEndpoints());
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    static IAudioPolicyConfigFactory Create()
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString(ClassName, ClassName.Length, out var name));
        try
        {
            var last = 0;
            foreach (var iid in FactoryIids)
            {
                var id = iid;
                last = RoGetActivationFactory(name, ref id, out var factory);
                if (last >= 0)
                {
                    try
                    {
                        return (IAudioPolicyConfigFactory)Marshal.GetObjectForIUnknown(factory);
                    }
                    finally
                    {
                        Marshal.Release(factory);
                    }
                }
            }
            Marshal.ThrowExceptionForHR(last);
            throw new InvalidOperationException("AudioPolicyConfig is unavailable");
        }
        finally
        {
            WindowsDeleteString(name);
        }
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    // Undocumented; same vtable as the one EarTrumpet uses. Only the last method is called, the rest
    // keep their slots. Declared as IUnknown with the three IInspectable slots spelled out.
    [ComImport, Guid("ab3d4648-e242-459f-b02f-541c70306324"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioPolicyConfigFactory
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();
        void AddCtxVolumeChange();
        void RemoveCtxVolumeChanged();
        void AddRingerVibrateStateChanged();
        void RemoveRingerVibrateStateChange();
        void SetVolumeGroupGainForId();
        void GetVolumeGroupGainForId();
        void GetActiveVolumeGroupForEndpointId();
        void GetVolumeGroupsForEndpoint();
        void GetCurrentVolumeContext();
        void SetVolumeGroupMuteForId();
        void GetVolumeGroupMuteForId();
        void SetRingerVibrateState();
        void GetRingerVibrateState();
        void SetPreferredChatApplication();
        void ResetPreferredChatApplication();
        void GetPreferredChatApplication();
        void GetCurrentChatApplications();
        void AddChatContextChanged();
        void RemoveChatContextChanged();
        void SetPersistedDefaultAudioEndpoint();
        void GetPersistedDefaultAudioEndpoint();

        [PreserveSig]
        int ClearAllPersistedApplicationDefaultEndpoints();
    }
}
