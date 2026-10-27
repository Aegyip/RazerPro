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

    /// <summary>Requests one vendor property. Only 0x21 and 0x2A are known to be safe to ask for.</summary>
    static int Get(HeadsetProperty property)
    {
        using var channel = RazerVendorChannel.Open(LightingEngine.VendorId, LightingEngine.ProductId);
        if (channel is null)
        {
            Console.WriteLine("Vendor interface not found");
            return 1;
        }
        using var answered = new ManualResetEventSlim();
        channel.PropertyReceived += (p, v) =>
        {
            Console.WriteLine($"<- {p} (0x{(byte)p:X2}) = 0x{v:X2} ({v})");
            if (p == property)
                answered.Set();
        };
        channel.Start();
        if (!channel.Request(property, TimeSpan.FromSeconds(1)))
        {
            Console.WriteLine("The dongle did not accept the request");
            return 1;
        }
        if (answered.Wait(TimeSpan.FromSeconds(2)))
            return 0;
        Console.WriteLine("No reply");
        return 1;
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
