using System.Runtime.InteropServices;
using BarracudaTray.Interop;

namespace BarracudaTray.Audio;

/// <summary>
/// Virtual 7.1 for a stereo headset: takes the multichannel mix games send to VB-CABLE, renders it
/// binaurally with a HeSuVi profile and plays it on the headset. Everything runs on one MMCSS
/// "Pro Audio" thread, which owns every COM object it creates.
/// </summary>
internal sealed class SurroundEngine : IDisposable
{
    const int Block = BinauralConvolver.BlockSize;
    const double LfeCutoffHz = 111; // HeSuVi folds a low-passed LFE into the center speaker

    readonly string _sourceId;
    readonly string _targetId;
    readonly HrirSet _hrir;
    readonly bool _exclusiveOutput;
    readonly ManualResetEvent _stop = new(false);
    readonly Thread _thread;
    double _latencyMs;
    string _mode = "";

    /// <summary>Raised on the audio thread when the stream dies; the engine is stopped by then.</summary>
    public event Action<string>? Failed;

    /// <param name="sourceId">Capture endpoint carrying the 7.1 mix (VB-CABLE's output).</param>
    /// <param name="targetId">Render endpoint of the headset.</param>
    /// <param name="exclusiveOutput">Hold the headset exclusively: lowest latency, but other apps playing to it go silent.</param>
    public SurroundEngine(string sourceId, string targetId, HrirSet hrir, bool exclusiveOutput)
    {
        _sourceId = sourceId;
        _targetId = targetId;
        _hrir = hrir;
        _exclusiveOutput = exclusiveOutput;
        _thread = new Thread(Run) { IsBackground = true, Name = "Surround engine", Priority = ThreadPriority.Highest };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    /// <summary>Estimated delay this engine adds, from VB-CABLE's capture buffer to the headset's buffer.</summary>
    public double LatencyMs => Volatile.Read(ref _latencyMs);

    /// <summary>How the stream ended up opened, e.g. "özel mod 16-bit · kaynak 8 kanal".</summary>
    public string Mode => Volatile.Read(ref _mode);

    /// <summary>Where the latency comes from, in frames; for the CLI.</summary>
    public string Breakdown => Volatile.Read(ref _breakdown);
    string _breakdown = "";

    public void Start() => _thread.Start();

    public void Dispose()
    {
        _stop.Set();
        if (_thread.IsAlive)
            _thread.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }

    void Run()
    {
        uint taskIndex = 0;
        var mmcss = NativeMethods.AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
        var formats = new List<IntPtr>();
        var com = new List<object>();
        try
        {
            using var captureEvent = new AutoResetEvent(false);
            using var renderEvent = new AutoResetEvent(false);
            var enumerator = Track(com, CoreAudio.CreateEnumerator());

            // Taking VB-CABLE's capture side exclusively also picks its channel count ourselves, so the
            // recording format in Windows' Sound settings does not matter.
            var sourceDevice = Track(com, enumerator.GetDevice(_sourceId));
            var (capture, source, capturePeriod, captureExclusive) = Open(sourceDevice, captureEvent, formats, com, exclusive: true,
            [
                new(8, HrirSet.SampleRate, 24, false, 0x63F),
                new(8, HrirSet.SampleRate, 16, false, 0x63F),
                new(6, HrirSet.SampleRate, 24, false, 0x60F),
                new(2, HrirSet.SampleRate, 24, false, 0x3),
                new(2, HrirSet.SampleRate, 16, false, 0x3),
            ]);
            if (source.SampleRate != HrirSet.SampleRate)
                throw new InvalidOperationException($"VB-CABLE 48000 Hz olmalı; şu an {source}");
            var captureClient = Track(com, (IAudioCaptureClient)GetService(capture, CoreAudio.IidAudioCaptureClient));

            // Exclusive output skips the Windows mixer, the biggest single delay on the way out, but
            // then nothing else (Discord, which plays to the headset as the communications device)
            // can be heard. Shared output is the default for that reason.
            var targetDevice = Track(com, enumerator.GetDevice(_targetId));
            var (render, target, renderFrames, renderExclusive) = Open(targetDevice, renderEvent, formats, com, _exclusiveOutput,
            [
                new(2, HrirSet.SampleRate, 32, true, 0x3),
                new(2, HrirSet.SampleRate, 24, false, 0x3),
                new(2, HrirSet.SampleRate, 16, false, 0x3),
                new(2, HrirSet.SampleRate, 32, false, 0x3),
            ]);
            var renderClient = Track(com, (IAudioRenderClient)GetService(render, CoreAudio.IidAudioRenderClient));
            Volatile.Write(ref _mode,
                $"çıkış {(renderExclusive ? "özel" : "paylaşımlı")} {target.BitsPerSample}-bit · " +
                $"giriş {(captureExclusive ? "özel" : "paylaşımlı")} {source.Channels} kanal");

            var exclusive = renderExclusive;
            var pipeline = new Pipeline(_hrir, source, capturePeriod, renderFrames);
            WriteSilence(renderClient, render, target, exclusive ? renderFrames : (int)render.GetBufferSize());
            capture.Start();
            render.Start();

            WaitHandle[] handles = [_stop, captureEvent, renderEvent];
            var timeouts = 0;
            while (true)
            {
                var signaled = WaitHandle.WaitAny(handles, 500);
                if (signaled == 0)
                    break;
                if (signaled == WaitHandle.WaitTimeout)
                {
                    if (++timeouts >= 4)
                        throw new TimeoutException("Ses cihazı yanıt vermiyor");
                    continue;
                }
                timeouts = 0;

                pipeline.Capture(captureClient);
                if (signaled != 2)
                    continue;
                var frames = exclusive ? renderFrames : (int)(render.GetBufferSize() - render.GetCurrentPadding());
                if (frames == 0)
                    continue;
                var buffer = renderClient.GetBuffer((uint)frames);
                pipeline.Render(buffer, frames, target);
                renderClient.ReleaseBuffer((uint)frames, 0);
                Volatile.Write(ref _latencyMs, pipeline.LatencyFrames * 1000.0 / HrirSet.SampleRate);
                Volatile.Write(ref _breakdown, pipeline.Breakdown);
            }
            capture.Stop();
            render.Stop();
        }
        catch (Exception ex)
        {
            // Top of a background thread: anything escaping here would take the whole tray app down,
            // and COM failures surface as several exception types (COM, argument, access, cast...).
            Failed?.Invoke(ex.Message);
        }
        finally
        {
            // Released explicitly so an exclusive stream gives the headset back immediately.
            for (var i = com.Count - 1; i >= 0; i--)
                Marshal.FinalReleaseComObject(com[i]);
            foreach (var format in formats)
                Marshal.FreeHGlobal(format);
            if (mmcss != IntPtr.Zero)
                NativeMethods.AvRevertMmThreadCharacteristics(mmcss);
        }
    }

    static T Track<T>(List<object> com, T value) where T : class
    {
        com.Add(value);
        return value;
    }

    static object GetService(IAudioClient3 client, Guid iid) => client.GetService(ref iid);

    /// <summary>Opens a shared stream at the smallest engine period the driver allows; returns that period in frames.</summary>
    static int InitializeShared(IAudioClient3 client, AudioFormat format, AutoResetEvent ready, List<IntPtr> formats)
    {
        var native = format.Allocate();
        formats.Add(native);
        try
        {
            client.GetSharedModeEnginePeriod(native, out _, out _, out var minimum, out _);
            client.InitializeSharedAudioStream(CoreAudio.StreamFlagsEventCallback, minimum, native, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or NotImplementedException)
        {
            // Another stream already fixed the engine period, or the driver only supports the default one.
            client.Initialize(ShareMode.Shared, CoreAudio.StreamFlagsEventCallback, 0, 0, native, IntPtr.Zero);
        }
        client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
        client.GetCurrentSharedModeEnginePeriod(out var current, out var period);
        Marshal.FreeCoTaskMem(current);
        return (int)period;
    }

    /// <summary>
    /// Opens the first exclusive format the device takes, at its minimum period, or falls back to a
    /// shared stream in the mix format. Frames is the period (shared) or buffer (exclusive) size.
    /// </summary>
    static (IAudioClient3 Client, AudioFormat Format, int Frames, bool Exclusive) Open(
        IMMDevice device, AutoResetEvent ready, List<IntPtr> formats, List<object> com, bool exclusive, AudioFormat[] candidates)
    {
        if (exclusive)
        {
            foreach (var format in candidates)
            {
                var client = CoreAudio.Activate(device);
                var native = format.Allocate();
                formats.Add(native);
                try
                {
                    client.GetDevicePeriod(out _, out var minimum);
                    try
                    {
                        client.Initialize(ShareMode.Exclusive, CoreAudio.StreamFlagsEventCallback, minimum, minimum, native, IntPtr.Zero);
                    }
                    catch (COMException ex) when (ex.HResult == CoreAudio.ErrorBufferSizeNotAligned)
                    {
                        // The documented retry: a fresh client with the period rounded to the driver's buffer.
                        var aligned = (long)(10_000_000.0 * client.GetBufferSize() / format.SampleRate + 0.5);
                        Marshal.FinalReleaseComObject(client);
                        client = CoreAudio.Activate(device);
                        client.Initialize(ShareMode.Exclusive, CoreAudio.StreamFlagsEventCallback, aligned, aligned, native, IntPtr.Zero);
                    }
                    client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
                    com.Add(client);
                    return (client, format, (int)client.GetBufferSize(), true);
                }
                catch (Exception ex) when (ex is COMException or ArgumentException or UnauthorizedAccessException)
                {
                    // Format refused, or exclusive use disabled for the device: try the next candidate.
                    Marshal.FinalReleaseComObject(client);
                }
            }
        }

        var shared = Track(com, CoreAudio.Activate(device));
        var mix = AudioFormat.ReadAndFree(shared.GetMixFormat());
        var period = InitializeShared(shared, mix, ready, formats);
        return (shared, mix, period, false);
    }

    static void WriteSilence(IAudioRenderClient client, IAudioClient3 audio, AudioFormat format, int frames)
    {
        frames = Math.Min(frames, (int)(audio.GetBufferSize() - audio.GetCurrentPadding()));
        if (frames <= 0)
            return;
        client.GetBuffer((uint)frames);
        client.ReleaseBuffer((uint)frames, CoreAudio.BufferFlagsSilent);
    }

    /// <summary>Capture → per-speaker blocks → convolution → stereo FIFO → render. Audio thread only.</summary>
    sealed class Pipeline
    {
        readonly BinauralConvolver _convolver;
        readonly int _channels;
        readonly int[] _speakerOf; // per source channel: VirtualSpeaker index, or -1
        readonly int _lfeChannel;
        readonly Biquad _lfeFilter = Biquad.LowPass(LfeCutoffHz, HrirSet.SampleRate);
        readonly SampleFifo _input;
        readonly SampleFifo _output;
        readonly float[] _block;
        readonly float[][] _speakers = new float[7][];
        readonly float[] _lfe = new float[Block];
        readonly float[] _left = new float[Block];
        readonly float[] _right = new float[Block];
        readonly float[] _stereo = new float[Block * 2];
        readonly AudioFormat _source;
        readonly float[] _converted;
        readonly int _capturePeriod;
        readonly int _renderFrames;
        readonly int _maxTarget;
        int _target;  // stereo frames buffered before playback starts
        int _underruns;
        int _largestBurst = Block;
        bool _primed;
        double _level;

        public Pipeline(HrirSet hrir, AudioFormat source, int capturePeriod, int renderFrames)
        {
            _convolver = new BinauralConvolver(hrir);
            _source = source;
            _channels = source.Channels;
            _converted = new float[HrirSet.SampleRate / 10 * _channels];
            (_speakerOf, _lfeChannel) = MapChannels(source);
            _block = new float[Block * _channels];
            for (var s = 0; s < 7; s++)
                _speakers[s] = new float[Block];
            _input = new SampleFifo(HrirSet.SampleRate * _channels);
            _output = new SampleFifo(HrirSet.SampleRate * 2);
            _capturePeriod = capturePeriod;
            _renderFrames = renderFrames;
            // The cushion starts at what one headset buffer needs and grows a block per underrun, so it
            // settles at the smallest size the capture timing allows; anything beyond that is pure delay.
            _target = renderFrames + Block;
            _maxTarget = capturePeriod + renderFrames + 4 * Block;
        }

        /// <summary>Capture period + one block + what sits in the FIFO + the headset buffer.</summary>
        public double LatencyFrames => _capturePeriod + Block + _level + _renderFrames;

        public string Breakdown =>
            $"capture {_capturePeriod} + block {Block} + queue {_level:F0} (target {_target}, underruns {_underruns}) " +
            $"+ render {_renderFrames} frames · peak in {TakePeak(ref _inputPeak):F3} out {TakePeak(ref _outputPeak):F3}";

        // Loudest sample since the last Breakdown read, for checking that audio actually flows.
        float _inputPeak;
        float _outputPeak;

        static float TakePeak(ref float peak)
        {
            var value = peak;
            peak = 0;
            return value;
        }

        static void TrackPeak(ref float peak, ReadOnlySpan<float> samples)
        {
            foreach (var s in samples)
                peak = Math.Max(peak, Math.Abs(s));
        }

        public unsafe void Capture(IAudioCaptureClient client)
        {
            while (client.GetNextPacketSize() > 0)
            {
                client.GetBuffer(out var data, out var frames, out var flags, out _, out _);
                var samples = (int)frames * _channels;
                if ((flags & CoreAudio.BufferFlagsSilent) != 0)
                    _input.WriteZeros(samples);
                else if (_source.IsFloat)
                    _input.Write(new ReadOnlySpan<float>((void*)data, samples));
                else
                    _input.Write(Convert((byte*)data, samples));
                client.ReleaseBuffer(frames);
            }

            var before = _output.Count / 2;
            while (_input.Count >= _block.Length)
            {
                _input.Read(_block);
                TrackPeak(ref _inputPeak, _block);
                Process();
                TrackPeak(ref _outputPeak, _stereo);
            }
            _largestBurst = Math.Max(_largestBurst, _output.Count / 2 - before);
        }

        unsafe ReadOnlySpan<float> Convert(byte* data, int samples)
        {
            var output = _converted.AsSpan(0, Math.Min(samples, _converted.Length));
            switch (_source.BytesPerSample)
            {
                case 2:
                    for (var i = 0; i < output.Length; i++)
                        output[i] = ((short*)data)[i] / 32768f;
                    break;
                case 3:
                    for (var i = 0; i < output.Length; i++)
                    {
                        var p = data + 3 * i;
                        output[i] = (p[0] | p[1] << 8 | (sbyte)p[2] << 16) / 8388608f;
                    }
                    break;
                default:
                    for (var i = 0; i < output.Length; i++)
                        output[i] = ((int*)data)[i] / 2147483648f;
                    break;
            }
            return output;
        }

        void Process()
        {
            foreach (var speaker in _speakers)
                Array.Clear(speaker);
            for (var f = 0; f < Block; f++)
            {
                for (var c = 0; c < _channels; c++)
                {
                    var v = _block[f * _channels + c];
                    var s = _speakerOf[c];
                    if (s >= 0)
                        _speakers[s][f] = v;
                    else if (c == _lfeChannel)
                        _lfe[f] = v;
                }
            }
            if (_lfeChannel >= 0)
            {
                var center = _speakers[(int)VirtualSpeaker.Center];
                for (var f = 0; f < Block; f++)
                    center[f] += _lfeFilter.Process(_lfe[f]);
            }

            _convolver.Process(_speakers, _left, _right);
            for (var f = 0; f < Block; f++)
            {
                _stereo[2 * f] = _left[f];
                _stereo[2 * f + 1] = _right[f];
            }
            _output.Write(_stereo);
        }

        public unsafe void Render(IntPtr buffer, int frames, AudioFormat format)
        {
            var queued = _output.Count / 2;
            // Clock drift between VB-CABLE and the headset slowly grows the queue; trim it back. Audio
            // arrives in bursts, so a queue up to one burst above the target is normal, not drift.
            if (queued > _target + _largestBurst + _renderFrames)
            {
                _output.Discard((queued - _target) * 2);
                queued = _target;
            }
            if (!_primed && queued >= _target)
                _primed = true;
            _level += (queued - _level) * 0.05;

            Span<float> stereo = stackalloc float[frames * 2];
            if (_primed)
            {
                var available = Math.Min(frames, queued);
                _output.Read(stereo[..(available * 2)]);
                if (available < frames)
                {
                    // Underrun: the cushion was too thin for the capture timing. Grow it and rebuild.
                    _primed = false;
                    _underruns++;
                    _target = Math.Min(_target + Block, _maxTarget);
                }
            }
            Write(buffer, stereo, frames, format);
        }

        static unsafe void Write(IntPtr buffer, ReadOnlySpan<float> stereo, int frames, AudioFormat format)
        {
            var p = (byte*)buffer;
            var channels = format.Channels;
            var bytes = format.BytesPerSample;
            for (var f = 0; f < frames; f++)
            {
                for (var c = 0; c < channels; c++)
                {
                    var v = c < 2 ? Math.Clamp(stereo[2 * f + c], -1f, 1f) : 0f;
                    var at = p + (f * channels + c) * bytes;
                    switch (bytes)
                    {
                        case 4 when format.IsFloat:
                            *(float*)at = v;
                            break;
                        case 4:
                            *(int*)at = (int)(v * int.MaxValue);
                            break;
                        case 3:
                            var i24 = (int)(v * 8388607);
                            at[0] = (byte)i24;
                            at[1] = (byte)(i24 >> 8);
                            at[2] = (byte)(i24 >> 16);
                            break;
                        default:
                            *(short*)at = (short)(v * short.MaxValue);
                            break;
                    }
                }
            }
        }

        static (int[] SpeakerOf, int LfeChannel) MapChannels(AudioFormat format)
        {
            var mask = format.ChannelMask;
            if (mask == 0)
                mask = format.Channels switch { 2 => 0x3u, 6 => 0x3Fu, 8 => 0x63Fu, _ => 0u };

            var speakerOf = Enumerable.Repeat(-1, format.Channels).ToArray();
            var lfe = -1;
            var channel = 0;
            for (var bit = 0; bit < 32 && channel < format.Channels; bit++)
            {
                if ((mask & (1u << bit)) == 0)
                    continue;
                var speaker = (1u << bit) switch
                {
                    0x1 => VirtualSpeaker.FrontLeft,
                    0x2 => VirtualSpeaker.FrontRight,
                    0x4 => VirtualSpeaker.Center,
                    0x10 => VirtualSpeaker.BackLeft,
                    0x20 => VirtualSpeaker.BackRight,
                    0x200 => VirtualSpeaker.SideLeft,
                    0x400 => VirtualSpeaker.SideRight,
                    _ => (VirtualSpeaker?)null,
                };
                if (speaker is { } s)
                    speakerOf[channel] = (int)s;
                else if (bit == 3)
                    lfe = channel;
                channel++;
            }
            return (speakerOf, lfe);
        }
    }
}

/// <summary>Fixed-size ring of floats for one producer and one consumer on the same thread.</summary>
internal sealed class SampleFifo(int capacity)
{
    readonly float[] _data = new float[capacity];
    int _read;

    public int Count { get; private set; }

    public void Write(ReadOnlySpan<float> samples)
    {
        // A full ring drops the oldest audio rather than the newest.
        if (Count + samples.Length > _data.Length)
            Discard(Count + samples.Length - _data.Length);
        var write = (_read + Count) % _data.Length;
        var first = Math.Min(samples.Length, _data.Length - write);
        samples[..first].CopyTo(_data.AsSpan(write));
        samples[first..].CopyTo(_data);
        Count += samples.Length;
    }

    public void WriteZeros(int count)
    {
        Span<float> zeros = stackalloc float[Math.Min(count, 4096)];
        zeros.Clear();
        for (var left = count; left > 0; left -= zeros.Length)
            Write(zeros[..Math.Min(left, zeros.Length)]);
    }

    public void Read(Span<float> destination)
    {
        var first = Math.Min(destination.Length, _data.Length - _read);
        _data.AsSpan(_read, first).CopyTo(destination);
        _data.AsSpan(0, destination.Length - first).CopyTo(destination[first..]);
        Discard(destination.Length);
    }

    public void Discard(int count)
    {
        count = Math.Min(count, Count);
        _read = (_read + count) % _data.Length;
        Count -= count;
    }
}

/// <summary>Second-order Butterworth section (RBJ cookbook).</summary>
internal sealed class Biquad
{
    readonly float _b0, _b1, _b2, _a1, _a2;
    float _x1, _x2, _y1, _y2;

    Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = (float)(b0 / a0);
        _b1 = (float)(b1 / a0);
        _b2 = (float)(b2 / a0);
        _a1 = (float)(a1 / a0);
        _a2 = (float)(a2 / a0);
    }

    public static Biquad LowPass(double cutoffHz, int sampleRate)
    {
        var w = 2 * Math.PI * cutoffHz / sampleRate;
        var alpha = Math.Sin(w) / (2 * Math.Sqrt(0.5));
        var cos = Math.Cos(w);
        return new Biquad((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public float Process(float x)
    {
        var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        _x2 = _x1;
        _x1 = x;
        _y2 = _y1;
        _y1 = y;
        return y;
    }
}
