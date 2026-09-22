using System.Numerics;

namespace VoiceKit.Core;

/// <summary>Streaming STFT pitch shifter, 1024-point FFT, 4x overlap.
/// Spectral bins are remapped using instantaneous frequencies, not resampled audio.
/// No formant preservation, transient locking or per-frame allocation.</summary>
internal sealed class PitchShifter
{
    private const int Size = 1024, Hop = Size / 4;
    private readonly float[] _input = new float[Size], _output = new float[Size * 2];
    private readonly double[] _window = new double[Size], _previousPhase = new double[Size / 2 + 1],
        _synthesisPhase = new double[Size / 2 + 1], _magnitudes = new double[Size / 2 + 1],
        _frequencySums = new double[Size / 2 + 1];
    private readonly Complex[] _spectrum = new Complex[Size], _twiddles = new Complex[Size / 2];
    private readonly int[] _reverse = new int[Size];
    private int _inputPosition, _outputPosition, _untilFrame = Hop;
    private float _semitones, _wet;
    public PitchShifter()
    {
        for (int i = 0; i < Size; i++)
        {
            _window[i] = .5 - .5 * Math.Cos(2 * Math.PI * i / Size);
            int value = i, reversed = 0;
            for (int bit = 0; bit < 10; bit++) { reversed = (reversed << 1) | (value & 1); value >>= 1; }
            _reverse[i] = reversed;
        }
        for (int i = 0; i < _twiddles.Length; i++)
            _twiddles[i] = Complex.FromPolarCoordinates(1, -2 * Math.PI * i / Size);
    }
    public float Process(float input, double semitones, bool enabled, bool modulated = false)
    {
        _input[_inputPosition] = input;
        _inputPosition = (_inputPosition + 1) % Size;
        // The LFO already smooths its controls; do not low-pass away its fast oscillation.
        DspMath.Smooth(ref _semitones, semitones, modulated ? .02f : .0005f);
        if (--_untilFrame == 0)
        {
            TransformFrame(Math.Pow(2, _semitones / 12.0));
            _untilFrame = Hop;
        }
        float shifted = _output[_outputPosition];
        _output[_outputPosition] = 0;
        _outputPosition = (_outputPosition + 1) % _output.Length;
        float wet = DspMath.Smooth(ref _wet, enabled && (modulated || Math.Abs(semitones) > .001) ? 1 : 0);
        return DspMath.Lerp(input, shifted, wet);
    }
    private void TransformFrame(double ratio)
    {
        for (int i = 0; i < Size; i++) _spectrum[i] = new Complex(_input[(_inputPosition + i) % Size] * _window[i], 0);
        Fft(false);
        Array.Clear(_magnitudes);
        Array.Clear(_frequencySums);
        const double phaseStep = 2 * Math.PI * Hop / Size;
        for (int k = 0; k <= Size / 2; k++)
        {
            var bin = _spectrum[k];
            double magnitude = bin.Magnitude, phase = Math.Atan2(bin.Imaginary, bin.Real);
            double residual = Math.IEEERemainder(phase - _previousPhase[k] - k * phaseStep, 2 * Math.PI);
            _previousPhase[k] = phase;
            double frequency = (k + residual / phaseStep) * ratio;
            int destination = (int)Math.Round(k * ratio);
            if (destination < 1 || destination >= Size / 2) continue;
            _magnitudes[destination] += magnitude;
            _frequencySums[destination] += magnitude * frequency;
        }
        Array.Clear(_spectrum);
        for (int k = 1; k < Size / 2; k++)
        {
            double frequency = _magnitudes[k] > 1e-15 ? _frequencySums[k] / _magnitudes[k] : k;
            _synthesisPhase[k] = Math.IEEERemainder(_synthesisPhase[k] + frequency * phaseStep, 2 * Math.PI);
            _spectrum[k] = Complex.FromPolarCoordinates(_magnitudes[k], _synthesisPhase[k]);
            _spectrum[Size - k] = Complex.Conjugate(_spectrum[k]);
        }
        Fft(true);
        for (int i = 0; i < Size; i++)
        {
            int destination = (_outputPosition + i) % _output.Length;
            // Periodic Hann squared sums to 1.5 at 4x overlap.
            _output[destination] += (float)(_spectrum[i].Real * _window[i] * (2.0 / 3));
        }
    }
    private void Fft(bool inverse)
    {
        for (int i = 0; i < Size; i++)
        {
            int j = _reverse[i];
            if (j > i) (_spectrum[i], _spectrum[j]) = (_spectrum[j], _spectrum[i]);
        }
        for (int length = 2; length <= Size; length *= 2)
        {
            int half = length / 2, stride = Size / length;
            for (int begin = 0; begin < Size; begin += length)
                for (int j = 0; j < half; j++)
                {
                    var w = _twiddles[j * stride];
                    if (inverse) w = Complex.Conjugate(w);
                    var even = _spectrum[begin + j];
                    var odd = _spectrum[begin + j + half] * w;
                    _spectrum[begin + j] = even + odd;
                    _spectrum[begin + j + half] = even - odd;
                }
        }
        if (inverse) for (int i = 0; i < Size; i++) _spectrum[i] /= Size;
    }
}
