using System.Text;

namespace BarracudaTray.Audio;

/// <summary>The seven virtual speakers of a 7.1 layout (LFE is folded into the center).</summary>
internal enum VirtualSpeaker
{
    FrontLeft,
    FrontRight,
    Center,
    BackLeft,
    BackRight,
    SideLeft,
    SideRight,
}

/// <summary>
/// Head-related impulse responses from a HeSuVi profile: for each virtual speaker, what reaches
/// the left and the right ear.
/// </summary>
internal sealed class HrirSet
{
    public const int SampleRate = 48000;

    // HeSuVi's 14-channel order: FL→L FL→R SL→L SL→R BL→L BL→R C→L FR→R FR→L SR→R SR→L BR→R BR→L C→R.
    // 7-channel files hold only the left half; channel i of the 14 then reads file channel i % 7,
    // which mirrors the left side onto the right (the same reuse Equalizer APO applies).
    static readonly (VirtualSpeaker Speaker, int LeftEar, int RightEar)[] Layout =
    [
        (VirtualSpeaker.FrontLeft, 0, 1),
        (VirtualSpeaker.FrontRight, 8, 7),
        (VirtualSpeaker.Center, 6, 13),
        (VirtualSpeaker.BackLeft, 4, 5),
        (VirtualSpeaker.BackRight, 12, 11),
        (VirtualSpeaker.SideLeft, 2, 3),
        (VirtualSpeaker.SideRight, 10, 9),
    ];

    readonly float[][] _left = new float[7][];
    readonly float[][] _right = new float[7][];

    public string Name { get; }
    public int Length { get; }

    HrirSet(string name, float[][] channels)
    {
        Name = name;
        Length = channels[0].Length;
        foreach (var (speaker, leftEar, rightEar) in Layout)
        {
            _left[(int)speaker] = channels[leftEar % channels.Length];
            _right[(int)speaker] = channels[rightEar % channels.Length];
        }
    }

    public ReadOnlySpan<float> Left(VirtualSpeaker speaker) => _left[(int)speaker];
    public ReadOnlySpan<float> Right(VirtualSpeaker speaker) => _right[(int)speaker];

    public static HrirSet Load(string path)
    {
        var channels = WavReader.Read(path, out var rate);
        if (rate != SampleRate)
            throw new InvalidDataException($"{Path.GetFileName(path)} is {rate} Hz; only {SampleRate} Hz profiles are supported");
        if (channels.Length is not (7 or 14))
            throw new InvalidDataException($"{Path.GetFileName(path)} has {channels.Length} channels; expected 7 or 14");
        return new HrirSet(Path.GetFileNameWithoutExtension(path), channels);
    }
}

/// <summary>The HeSuVi profiles copied into the app's data folder.</summary>
internal static class HrirLibrary
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BarracudaTray", "hrir");

    /// <summary>Profile names (file names without .wav) with HeSuVi's description where known.</summary>
    public static IReadOnlyList<(string Name, string Description)> List()
    {
        if (!Directory.Exists(Folder))
            return [];
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var info = Path.Combine(Folder, "info.csv");
        if (File.Exists(info))
        {
            foreach (var line in File.ReadAllLines(info, Encoding.UTF8))
            {
                var split = line.TrimStart('﻿').Split(';', 2);
                if (split.Length == 2)
                    descriptions[split[0]] = split[1];
            }
        }
        return Directory.EnumerateFiles(Folder, "*.wav")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(n => (n, descriptions.GetValueOrDefault(n, n)))
            .ToList();
    }

    public static string PathOf(string name) => Path.Combine(Folder, name + ".wav");
}

/// <summary>Reads PCM (16/24/32-bit) and float WAV files into per-channel float arrays.</summary>
internal static class WavReader
{
    public static float[][] Read(string path, out int sampleRate)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (new string(reader.ReadChars(4)) != "RIFF")
            throw new InvalidDataException("Not a RIFF file");
        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
            throw new InvalidDataException("Not a WAVE file");

        int channels = 0, bits = 0;
        var isFloat = false;
        sampleRate = 0;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadInt32();
            var next = reader.BaseStream.Position + size + (size & 1);
            if (id == "fmt ")
            {
                var tag = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadUInt16();
                bits = reader.ReadUInt16();
                isFloat = tag == 3;
                if (tag == 0xFFFE && size >= 40)
                {
                    reader.ReadBytes(8);
                    isFloat = reader.ReadUInt16() == 3;
                }
            }
            else if (id == "data")
            {
                if (channels == 0)
                    throw new InvalidDataException("data chunk before fmt chunk");
                var frames = size / (channels * bits / 8);
                var result = new float[channels][];
                for (var c = 0; c < channels; c++)
                    result[c] = new float[frames];
                for (var f = 0; f < frames; f++)
                    for (var c = 0; c < channels; c++)
                        result[c][f] = ReadSample(reader, bits, isFloat);
                return result;
            }
            reader.BaseStream.Position = next;
        }
        throw new InvalidDataException("No data chunk");
    }

    static float ReadSample(BinaryReader reader, int bits, bool isFloat) => (bits, isFloat) switch
    {
        (32, true) => reader.ReadSingle(),
        (16, false) => reader.ReadInt16() / 32768f,
        (24, false) => ((reader.ReadByte() | reader.ReadByte() << 8 | reader.ReadSByte() << 16)) / 8388608f,
        (32, false) => reader.ReadInt32() / 2147483648f,
        _ => throw new InvalidDataException($"Unsupported {bits}-bit {(isFloat ? "float" : "PCM")} samples"),
    };
}
