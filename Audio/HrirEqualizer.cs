namespace BarracudaTray.Audio;

/// <summary>
/// Tone correction for a HeSuVi profile. Most music and game audio is close to the same on both
/// front channels, and for such sound the front speakers' responses add up unevenly: with the
/// "razer" profile +3 dB at 120–250 Hz but −6.5 dB at 1–2 kHz and −6 dB at 8–12 kHz, which is
/// boomy, dull and quiet. One correction filter, flattening that front response, is applied to
/// every response, so the differences between directions (the surround cues) stay as they were.
/// The filter is minimum phase, so it adds no noticeable delay.
/// </summary>
internal static class HrirEqualizer
{
    const int FftSize = 8192;              // 5.9 Hz bins, fine enough for the bass region
    const int FilterLength = 1024;         // 21 ms, long enough for a 40 Hz correction
    const double MaxBoostDb = 9;
    const double MaxCutDb = 12;
    const double LowestHz = 40;            // below and above these the edge correction is held,
    const double HighestHz = 15000;        // so the filter never chases the response's roll-offs

    public static HrirSet Flatten(HrirSet set)
    {
        var filter = CorrectionFilter(set);
        return set.Map(ir => Convolve(ir, filter));
    }

    static float[] CorrectionFilter(HrirSet set)
    {
        var fft = new Fft(FftSize);
        const int half = FftSize / 2;
        const double binHz = (double)HrirSet.SampleRate / FftSize;

        // What the left ear gets when both front speakers play the same sound.
        var re = new float[FftSize];
        var im = new float[FftSize];
        var frontLeft = set.Left(VirtualSpeaker.FrontLeft);
        var frontRight = set.Left(VirtualSpeaker.FrontRight);
        for (var i = 0; i < set.Length && i < FftSize; i++)
            re[i] = frontLeft[i] + frontRight[i];
        fft.Forward(re, im);
        var power = new double[half + 1];
        for (var k = 0; k <= half; k++)
            power[k] = re[k] * (double)re[k] + im[k] * (double)im[k];

        // Third-octave smoothing, then the inverse, limited both ways.
        var gainDb = new double[half + 1];
        for (var k = 0; k <= half; k++)
        {
            var hz = Math.Clamp(k * binHz, LowestHz, HighestHz);
            var lo = (int)Math.Floor(hz / Math.Pow(2, 1 / 6.0) / binHz);
            var hi = (int)Math.Ceiling(hz * Math.Pow(2, 1 / 6.0) / binHz);
            double sum = 0;
            for (var j = Math.Max(1, lo); j <= Math.Min(half, hi); j++)
                sum += power[j];
            var mean = sum / Math.Max(1, Math.Min(half, hi) - Math.Max(1, lo) + 1);
            var responseDb = 10 * Math.Log10(Math.Max(mean, 1e-12));
            gainDb[k] = Math.Clamp(-responseDb, -MaxCutDb, MaxBoostDb);
        }

        // Minimum-phase filter with that magnitude, through the real cepstrum.
        Array.Clear(re);
        Array.Clear(im);
        for (var k = 0; k <= half; k++)
        {
            re[k] = (float)(gainDb[k] / 20 * Math.Log(10)); // natural log of the linear gain
            if (k > 0 && k < half)
                re[FftSize - k] = re[k];
        }
        fft.Inverse(re, im);                                // real cepstrum
        for (var n = 1; n < half; n++)
        {
            re[n] *= 2;
            re[FftSize - n] = 0;
        }
        Array.Clear(im);
        fft.Forward(re, im);                                // log spectrum, minimum phase
        for (var k = 0; k < FftSize; k++)
        {
            var magnitude = Math.Exp(re[k]);
            var phase = im[k];
            re[k] = (float)(magnitude * Math.Cos(phase));
            im[k] = (float)(magnitude * Math.Sin(phase));
        }
        fft.Inverse(re, im);

        var filter = new float[FilterLength];
        var fade = FilterLength / 4;
        for (var n = 0; n < FilterLength; n++)
        {
            var window = n < FilterLength - fade ? 1.0 : 0.5 * (1 + Math.Cos(Math.PI * (n - (FilterLength - fade)) / fade));
            filter[n] = (float)(re[n] * window);
        }
        return filter;
    }

    static float[] Convolve(float[] signal, float[] filter)
    {
        var output = new float[signal.Length + filter.Length - 1];
        for (var i = 0; i < signal.Length; i++)
        {
            var s = signal[i];
            if (s == 0)
                continue;
            for (var j = 0; j < filter.Length; j++)
                output[i + j] += s * filter[j];
        }
        return output;
    }
}
