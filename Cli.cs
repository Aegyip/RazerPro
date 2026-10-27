using BarracudaTray.Audio;
using BarracudaTray.Headset;
using BarracudaTray.Hid;
using BarracudaTray.Lighting;

namespace BarracudaTray;

/// <summary>Command-line helpers for checking the headset without the tray UI.</summary>
internal static class Cli
{
    public static int Run(string[] args)
    {
        HidDevice.Trace = report => Console.WriteLine($"-> {Convert.ToHexString(report)}");
        try
        {
            switch (args[0])
            {
                case "--diag":
                    return Diagnose();
                case "--color" when args.Length > 1 && LampColor.TryParse(args[1], out var color):
                    return WithDevice(d =>
                    {
                        d.SetAutonomous(false);
                        d.SetColors(Enumerable.Repeat(color, d.LampCount).ToArray());
                    });
                case "--spread":
                    // Each lamp a different hue, to check that lamp ids and colors line up.
                    return WithDevice(d =>
                    {
                        d.SetAutonomous(false);
                        d.SetColors(Enumerable.Range(0, d.LampCount).Select(i => LampColor.FromHue(i * 360.0 / d.LampCount)).ToArray());
                    });
                case "--listen":
                    return Listen(args.Length > 1 && int.TryParse(args[1], out var seconds) ? seconds : 15);
                case "--poll":
                    return Poll();
                case "--read" when args.Length > 1 && byte.TryParse(args[1], System.Globalization.NumberStyles.HexNumber, null, out var reportId):
                    return WithDevice(d => Console.WriteLine($"<- {Convert.ToHexString(d.ReadFeature(reportId))}"));
                case "--get" when args.Length > 1 && byte.TryParse(args[1], System.Globalization.NumberStyles.HexNumber, null, out var property):
                    return Get((HeadsetProperty)property);
                case "--set" when args.Length > 2 && byte.TryParse(args[1], System.Globalization.NumberStyles.HexNumber, null, out var setProperty):
                    return Get((HeadsetProperty)setProperty, Convert.FromHexString(args[2]));
                case "--devices":
                    return Devices();
                case "--status":
                    return Status();
                case "--configure-cable":
                    return ConfigureCable();
                case "--meters":
                    return Meters(args.Length > 1 && int.TryParse(args[1], out var meterSeconds) ? meterSeconds : 5);
                case "--selftest":
                    return SelfTest(args.Length > 1 ? args[1] : "razer");
                case "--surround":
                    return Surround(args.Length > 1 && int.TryParse(args[1], out var runSeconds) ? runSeconds : 10,
                        args.Length > 2 ? args[2] : "razer", exclusive: !args.Contains("shared"));
                case "--off":
                    return WithDevice(d =>
                    {
                        d.SetAutonomous(false);
                        d.SetColors(new LampColor[d.LampCount]);
                    });
                case "--auto":
                    return WithDevice(d => d.SetAutonomous(true));
                default:
                    Console.WriteLine("Usage: BarracudaTray [--diag | --color RRGGBB | --off | --auto]");
                    return 2;
            }
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    static int Diagnose()
    {
        var paths = HidDevice.FindPaths(LightingEngine.VendorId, LightingEngine.ProductId);
        Console.WriteLine($"{paths.Count} HID collection(s) for {LightingEngine.VendorId:X4}:{LightingEngine.ProductId:X4}");
        foreach (var path in paths)
        {
            Console.WriteLine();
            Console.WriteLine(path);
            using var hid = HidDevice.TryOpen(path);
            if (hid is null)
            {
                Console.WriteLine("  (cannot be opened, reserved by Windows)");
                continue;
            }
            Console.WriteLine($"  page=0x{hid.UsagePage:X4} usage=0x{hid.Usage:X4} featureLength={hid.FeatureReportLength}");
            foreach (var field in hid.FeatureFields)
                Console.WriteLine(field.IsArray || field.IsButton ? $"  {field}" : $"  {field} offset={hid.ProbeOffset(field)}");
        }

        Console.WriteLine();
        using var device = LampArrayDevice.Find(LightingEngine.VendorId, LightingEngine.ProductId);
        Console.WriteLine(device is null
            ? "LampArray: not found"
            : $"LampArray: {device.LampCount} lamps, min update interval {device.MinUpdateInterval.TotalMilliseconds} ms");
        return device is null ? 1 : 0;
    }

    /// <summary>Prints every input report the headset sends, for reading its status events.</summary>
    static int Listen(int seconds)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var gate = new object();
        foreach (var path in HidDevice.FindPaths(LightingEngine.VendorId, LightingEngine.ProductId))
        {
            var hid = HidDevice.TryOpen(path);
            if (hid is null || hid.InputReportLength == 0)
            {
                hid?.Dispose();
                continue;
            }
            var name = $"{hid.UsagePage:X4}:{hid.Usage:X4}";
            new Thread(() =>
            {
                while (true)
                {
                    var report = hid.ReadInput();
                    lock (gate)
                        Console.WriteLine($"{clock.Elapsed.TotalSeconds,7:F2}s {name} {Convert.ToHexString(report)}");
                }
            }) { IsBackground = true }.Start();
            Console.WriteLine($"Listening on {name} ({hid.InputReportLength} bytes)");
        }
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        return 0;
    }

    /// <summary>Requests every input report id once; read-only.</summary>
    static int Poll()
    {
        foreach (var path in HidDevice.FindPaths(LightingEngine.VendorId, LightingEngine.ProductId))
        {
            using var hid = HidDevice.TryOpen(path);
            if (hid is null || hid.InputReportLength == 0)
                continue;
            var name = $"{hid.UsagePage:X4}:{hid.Usage:X4}";
            var any = false;
            for (var id = 0; id < 256; id++)
            {
                if (hid.TryGetInputReport((byte)id) is not { } report)
                    continue;
                any = true;
                Console.WriteLine($"{name} id=0x{id:X2} {Convert.ToHexString(report)}");
            }
            if (!any)
                Console.WriteLine($"{name}: no input report answered");
        }
        return 0;
    }

    /// <summary>
    /// Requests (or, with <paramref name="value"/>, writes) one vendor property. Only properties
    /// Synapse itself reads and writes are safe; an unknown request once wedged the dongle.
    /// </summary>
    static int Get(HeadsetProperty property, byte[]? value = null)
    {
        using var channel = RazerVendorChannel.Open(LightingEngine.VendorId, LightingEngine.ProductId);
        if (channel is null)
        {
            Console.WriteLine("Vendor interface not found");
            return 1;
        }
        using var answered = new ManualResetEventSlim();
        channel.PropertyReceived += (p, data) =>
        {
            Console.WriteLine($"<- {p} (0x{(byte)p:X2}) = {Convert.ToHexString(data)} ({string.Join(" ", data.Select(b => (sbyte)b))})");
            if (p == property)
                answered.Set();
        };
        channel.Start();
        var sent = value is null
            ? channel.Request(property, TimeSpan.FromSeconds(1))
            : channel.Set(property, value, TimeSpan.FromSeconds(1));
        if (!sent)
        {
            Console.WriteLine("The dongle did not accept the request");
            return 1;
        }
        if (answered.Wait(TimeSpan.FromSeconds(2)))
            return 0;
        Console.WriteLine("No reply");
        return 1;
    }

    static int Devices()
    {
        var enumerator = CoreAudio.CreateEnumerator();
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        {
            Console.WriteLine(flow);
            foreach (var (id, name) in CoreAudio.Endpoints(enumerator, flow))
            {
                string format;
                try
                {
                    format = CoreAudio.MixFormat(id).ToString();
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    format = $"(format unavailable: 0x{ex.HResult:X8})";
                }
                Console.WriteLine($"  {name}  [{format}]");
                Console.WriteLine($"    exclusive 48 kHz: {ExclusiveSupport(enumerator, id)}");
            }
        }
        return 0;
    }

    /// <summary>Switches VB-CABLE's input to 7.1; used by the installer.</summary>
    static int ConfigureCable()
    {
        if (CoreAudio.FindEndpoint(DataFlow.Render, SurroundController.CableRenderName) is not { } cable)
        {
            Console.WriteLine("VB-CABLE not installed");
            return 1;
        }
        var ok = SurroundController.EnsureSevenOne(cable);
        Console.WriteLine(ok ? $"CABLE Input: {CoreAudio.MixFormat(cable)}" : "Could not switch CABLE Input to 7.1");
        return ok ? 0 : 1;
    }

    /// <summary>Runs the headset monitor for a few seconds and prints what it learned.</summary>
    static int Status()
    {
        using var monitor = new HeadsetMonitor();
        Thread.Sleep(TimeSpan.FromSeconds(5));
        var s = monitor.Status;
        Console.WriteLine($"dongle={s.DonglePresent} linked={s.Linked} battery={s.Battery} charging={s.Charging} mic muted={s.MicMuted}");
        Console.WriteLine($"eq={s.Eq} curve=[{string.Join(" ", s.EqCurve ?? [])}] sidetone on={s.SidetoneOn} level={s.SidetoneLevel} power saving={s.PowerSavingMinutes} min");
        return s.Linked == true ? 0 : 1;
    }

    /// <summary>Prints the default playback device and every playback device's peak level, to see where sound goes.</summary>
    static int Meters(int seconds)
    {
        var enumerator = CoreAudio.CreateEnumerator();
        Console.WriteLine($"Default (multimedia): {CoreAudio.FriendlyName(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))}");
        var meters = CoreAudio.Endpoints(enumerator, DataFlow.Render)
            .Select(d =>
            {
                var iid = new Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064");
                return (d.Name, Meter: (IAudioMeterInformation)enumerator.GetDevice(d.Id).Activate(ref iid, CoreAudio.ClsctxAll, IntPtr.Zero));
            })
            .ToList();
        for (var i = 0; i < seconds * 2; i++)
        {
            Thread.Sleep(500);
            Console.WriteLine(string.Join("  ", meters.Select(m => $"{m.Name[..Math.Min(22, m.Name.Length)]}={m.Meter.GetPeakValue():F2}")));
        }
        return 0;
    }

    /// <summary>Which channel counts and sample types the driver itself accepts (read-only check).</summary>
    static string ExclusiveSupport(IMMDeviceEnumerator enumerator, string id)
    {
        var client = CoreAudio.Activate(enumerator.GetDevice(id));
        var supported = new List<string>();
        foreach (var (channels, mask) in new[] { (2, 0x3u), (6, 0x60Fu), (8, 0x63Fu) })
        {
            foreach (var (bits, isFloat) in new[] { (32, true), (24, false), (16, false) })
            {
                var native = new AudioFormat(channels, 48000, bits, isFloat, mask).Allocate();
                try
                {
                    if (client.IsFormatSupported(ShareMode.Exclusive, native, IntPtr.Zero) == 0)
                        supported.Add($"{channels}ch/{bits}{(isFloat ? "f" : "")}");
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(native);
                }
            }
        }
        System.Runtime.InteropServices.Marshal.ReleaseComObject(client);
        return supported.Count > 0 ? string.Join(" ", supported) : "none";
    }

    /// <summary>
    /// Feeds an impulse to each virtual speaker and checks both ears get exactly that speaker's
    /// impulse responses back. Exercises the FFT, partitioning and HeSuVi channel mapping.
    /// </summary>
    static int SelfTest(string profile)
    {
        var hrir = HrirSet.Load(HrirLibrary.PathOf(profile));
        const int block = BinauralConvolver.BlockSize;
        var blocks = (hrir.Length + block - 1) / block + 2;
        var worst = 0f;
        foreach (var speaker in Enum.GetValues<VirtualSpeaker>())
        {
            var convolver = new BinauralConvolver(hrir);
            var left = new float[blocks * block];
            var right = new float[blocks * block];
            var input = Enumerable.Range(0, 7).Select(_ => new float[block]).ToArray();
            for (var b = 0; b < blocks; b++)
            {
                foreach (var channel in input)
                    Array.Clear(channel);
                if (b == 0)
                    input[(int)speaker][0] = 1;
                convolver.Process(input, left.AsSpan(b * block, block), right.AsSpan(b * block, block));
            }
            var expectedLeft = hrir.Left(speaker);
            var expectedRight = hrir.Right(speaker);
            var error = 0f;
            for (var i = 0; i < left.Length; i++)
            {
                var l = i < hrir.Length ? expectedLeft[i] : 0;
                var r = i < hrir.Length ? expectedRight[i] : 0;
                error = Math.Max(error, Math.Max(Math.Abs(left[i] - l), Math.Abs(right[i] - r)));
            }
            worst = Math.Max(worst, error);
            Console.WriteLine($"{speaker,-10} max error {error:E2}");
        }
        var ok = worst < 1e-4f;
        Console.WriteLine(ok ? $"OK ({profile}, {hrir.Length} taps)" : "FAILED");
        return ok ? 0 : 1;
    }

    static int Surround(int seconds, string profile, bool exclusive)
    {
        var source = CoreAudio.FindEndpoint(DataFlow.Capture, SurroundController.CableCaptureName);
        var target = CoreAudio.FindEndpoint(DataFlow.Render, SurroundController.HeadsetName);
        if (source is null || target is null)
        {
            Console.WriteLine(source is null ? "VB-CABLE not installed" : "Headset audio device not found");
            return 1;
        }
        using var engine = new SurroundEngine(source, target, HrirSet.Load(HrirLibrary.PathOf(profile)), exclusive);
        var failed = false;
        engine.Failed += message =>
        {
            failed = true;
            Console.WriteLine($"Failed: {message}");
        };
        engine.Start();
        for (var i = 0; i < seconds && !failed; i++)
        {
            Thread.Sleep(1000);
            Console.WriteLine($"{i + 1,3}s  ~{engine.LatencyMs:F1} ms  {engine.Mode}  ({engine.Breakdown})");
        }
        return failed ? 1 : 0;
    }

    static int WithDevice(Action<LampArrayDevice> action)
    {
        using var device = LampArrayDevice.Find(LightingEngine.VendorId, LightingEngine.ProductId);
        if (device is null)
        {
            Console.WriteLine("Headset not found");
            return 1;
        }
        action(device);
        Console.WriteLine("OK");
        return 0;
    }
}
