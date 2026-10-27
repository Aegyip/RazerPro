using System.Runtime.InteropServices;

namespace BarracudaTray.Audio;

/// <summary>The parts of a WAVEFORMATEX(TENSIBLE) the engine cares about.</summary>
internal sealed record AudioFormat(int Channels, int SampleRate, int BitsPerSample, bool IsFloat, uint ChannelMask)
{
    const ushort TagPcm = 1;
    const ushort TagFloat = 3;
    const ushort TagExtensible = 0xFFFE;
    static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00AA00389B71");
    static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");

    public int BytesPerSample => BitsPerSample / 8;
    public int BlockAlign => Channels * BytesPerSample;

    public static AudioFormat Read(IntPtr format)
    {
        var tag = (ushort)Marshal.ReadInt16(format, 0);
        var channels = Marshal.ReadInt16(format, 2);
        var rate = Marshal.ReadInt32(format, 4);
        var bits = Marshal.ReadInt16(format, 14);
        if (tag != TagExtensible)
            return new(channels, rate, bits, tag == TagFloat, 0);
        var mask = (uint)Marshal.ReadInt32(format, 20);
        var subtype = Marshal.PtrToStructure<Guid>(format + 24);
        return new(channels, rate, bits, subtype == SubtypeFloat, mask);
    }

    /// <summary>Reads and frees a format the audio stack allocated with CoTaskMemAlloc.</summary>
    public static AudioFormat ReadAndFree(IntPtr format)
    {
        try
        {
            return Read(format);
        }
        finally
        {
            Marshal.FreeCoTaskMem(format);
        }
    }

    /// <summary>A WAVEFORMATEXTENSIBLE in unmanaged memory; free it with Marshal.FreeHGlobal.</summary>
    public IntPtr Allocate()
    {
        var p = Marshal.AllocHGlobal(40);
        Marshal.WriteInt16(p, 0, unchecked((short)TagExtensible));
        Marshal.WriteInt16(p, 2, (short)Channels);
        Marshal.WriteInt32(p, 4, SampleRate);
        Marshal.WriteInt32(p, 8, SampleRate * BlockAlign);
        Marshal.WriteInt16(p, 12, (short)BlockAlign);
        Marshal.WriteInt16(p, 14, (short)BitsPerSample);
        Marshal.WriteInt16(p, 16, 22);
        Marshal.WriteInt16(p, 18, (short)BitsPerSample);
        Marshal.WriteInt32(p, 20, unchecked((int)ChannelMask));
        Marshal.StructureToPtr(IsFloat ? SubtypeFloat : SubtypePcm, p + 24, false);
        return p;
    }

    public override string ToString() =>
        $"{Channels}ch {SampleRate} Hz {BitsPerSample}-bit {(IsFloat ? "float" : "PCM")} mask 0x{ChannelMask:X}";
}
