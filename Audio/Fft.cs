namespace BarracudaTray.Audio;

/// <summary>In-place radix-2 complex FFT for one fixed power-of-two size.</summary>
internal sealed class Fft
{
    readonly int _size;
    readonly int[] _reversed;
    readonly float[] _cos;
    readonly float[] _sin;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
            throw new ArgumentException("FFT size must be a power of two", nameof(size));
        _size = size;
        var bits = System.Numerics.BitOperations.Log2((uint)size);
        _reversed = new int[size];
        for (var i = 0; i < size; i++)
        {
            var r = 0;
            for (var b = 0; b < bits; b++)
                r |= ((i >> b) & 1) << (bits - 1 - b);
            _reversed[i] = r;
        }
        _cos = new float[size / 2];
        _sin = new float[size / 2];
        for (var i = 0; i < size / 2; i++)
        {
            _cos[i] = (float)Math.Cos(2 * Math.PI * i / size);
            _sin[i] = (float)-Math.Sin(2 * Math.PI * i / size);
        }
    }

    public void Forward(Span<float> re, Span<float> im) => Transform(re, im, inverse: false);

    /// <summary>Inverse transform, scaled by 1/N.</summary>
    public void Inverse(Span<float> re, Span<float> im)
    {
        Transform(re, im, inverse: true);
        var scale = 1f / _size;
        for (var i = 0; i < _size; i++)
        {
            re[i] *= scale;
            im[i] *= scale;
        }
    }

    void Transform(Span<float> re, Span<float> im, bool inverse)
    {
        for (var i = 0; i < _size; i++)
        {
            var j = _reversed[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var length = 2; length <= _size; length <<= 1)
        {
            var half = length >> 1;
            var step = _size / length;
            for (var start = 0; start < _size; start += length)
            {
                for (var k = 0; k < half; k++)
                {
                    var wr = _cos[k * step];
                    var wi = inverse ? -_sin[k * step] : _sin[k * step];
                    var a = start + k;
                    var b = a + half;
                    var tr = re[b] * wr - im[b] * wi;
                    var ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }
}
