namespace BarracudaTray.Audio;

/// <summary>
/// Renders seven virtual speakers to two ears with uniformly partitioned overlap-save convolution.
/// Latency is one block. Both ears share one inverse FFT: each speaker's two impulse responses are
/// packed as H = H_left + i·H_right, and because both ear signals are real, the real part of the
/// inverse transform is the left ear and the imaginary part the right ear.
/// </summary>
internal sealed class BinauralConvolver
{
    public const int BlockSize = 128;
    const int FftSize = BlockSize * 2;
    const int Speakers = 7;

    readonly Fft _fft = new(FftSize);
    readonly int _partitions;
    readonly float[][][] _filterRe; // [speaker][partition][bin]
    readonly float[][][] _filterIm;
    readonly float[][][] _inputRe;  // frequency-domain delay line, [speaker][slot][bin]
    readonly float[][][] _inputIm;
    readonly float[][] _history;    // last two input blocks per speaker
    readonly float[] _accRe = new float[FftSize];
    readonly float[] _accIm = new float[FftSize];
    int _newest;

    public BinauralConvolver(HrirSet hrir)
    {
        _partitions = Math.Max(1, (hrir.Length + BlockSize - 1) / BlockSize);
        _filterRe = new float[Speakers][][];
        _filterIm = new float[Speakers][][];
        _inputRe = new float[Speakers][][];
        _inputIm = new float[Speakers][][];
        _history = new float[Speakers][];

        var leftRe = new float[FftSize];
        var leftIm = new float[FftSize];
        var rightRe = new float[FftSize];
        var rightIm = new float[FftSize];
        for (var s = 0; s < Speakers; s++)
        {
            var left = hrir.Left((VirtualSpeaker)s);
            var right = hrir.Right((VirtualSpeaker)s);
            _filterRe[s] = new float[_partitions][];
            _filterIm[s] = new float[_partitions][];
            _inputRe[s] = new float[_partitions][];
            _inputIm[s] = new float[_partitions][];
            _history[s] = new float[FftSize];
            for (var p = 0; p < _partitions; p++)
            {
                Array.Clear(leftRe);
                Array.Clear(leftIm);
                Array.Clear(rightRe);
                Array.Clear(rightIm);
                var start = p * BlockSize;
                for (var i = 0; i < BlockSize && start + i < hrir.Length; i++)
                {
                    leftRe[i] = left[start + i];
                    rightRe[i] = right[start + i];
                }
                _fft.Forward(leftRe, leftIm);
                _fft.Forward(rightRe, rightIm);

                var re = _filterRe[s][p] = new float[FftSize];
                var im = _filterIm[s][p] = new float[FftSize];
                for (var k = 0; k < FftSize; k++)
                {
                    // H_left + i·H_right
                    re[k] = leftRe[k] - rightIm[k];
                    im[k] = leftIm[k] + rightRe[k];
                }
                _inputRe[s][p] = new float[FftSize];
                _inputIm[s][p] = new float[FftSize];
            }
        }
    }

    /// <summary>Consumes one block per virtual speaker and writes one block per ear.</summary>
    public void Process(float[][] speakers, Span<float> left, Span<float> right)
    {
        _newest = (_newest + 1) % _partitions;
        for (var s = 0; s < Speakers; s++)
        {
            var history = _history[s];
            Array.Copy(history, BlockSize, history, 0, BlockSize);
            Array.Copy(speakers[s], 0, history, BlockSize, BlockSize);
            var re = _inputRe[s][_newest];
            var im = _inputIm[s][_newest];
            history.CopyTo(re, 0);
            Array.Clear(im);
            _fft.Forward(re, im);
        }

        Array.Clear(_accRe);
        Array.Clear(_accIm);
        for (var s = 0; s < Speakers; s++)
        {
            for (var p = 0; p < _partitions; p++)
            {
                var slot = (_newest - p + _partitions) % _partitions;
                var xr = _inputRe[s][slot];
                var xi = _inputIm[s][slot];
                var hr = _filterRe[s][p];
                var hi = _filterIm[s][p];
                for (var k = 0; k < FftSize; k++)
                {
                    _accRe[k] += xr[k] * hr[k] - xi[k] * hi[k];
                    _accIm[k] += xr[k] * hi[k] + xi[k] * hr[k];
                }
            }
        }

        _fft.Inverse(_accRe, _accIm);
        // Overlap-save: only the second half is free of circular wrap-around.
        _accRe.AsSpan(BlockSize, BlockSize).CopyTo(left);
        _accIm.AsSpan(BlockSize, BlockSize).CopyTo(right);
    }
}
