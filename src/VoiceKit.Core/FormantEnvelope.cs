using System.Numerics;

namespace VoiceKit.Core;

/// <summary>
/// Non-neural, low-quefrency cepstral envelope. Used inside the existing pitch STFT,
/// not as another audio queue. This warps a smooth envelope, not measured F1/F2 poles.
/// All storage is reserved at construction; no allocations on the audio thread.
/// </summary>
internal sealed class FormantEnvelope
{
    private const int Size = 1024, Half = Size / 2;
    private const double MaxLogGain = 2.0794415416798357; // ln(8), ±18 dB
    private readonly Complex[] _cepstrum = new Complex[Size], _twiddles = new Complex[Half];
    private readonly double[] _logEnvelope = new double[Half + 1], _lifter = new double[Size];
    private readonly int[] _reverse = new int[Size];
    private double _voicing;
    public FormantEnvelope()
    {
        for (int i = 0; i < Size; i++)
        {
            int v = i, r = 0;
            for (int bit = 0; bit < 10; bit++) { r = (r << 1) | (v & 1); v >>= 1; }
            _reverse[i] = r;
            int q = Math.Min(i, Size - i);
            _lifter[i] = q <= 48 ? 1 : q < 96 ? .5 + .5 * Math.Cos(Math.PI * (q - 48) / 48) : 0;
        }
        for (int i = 0; i < Half; i++) _twiddles[i] = Complex.FromPolarCoordinates(1, -2 * Math.PI * i / Size);
    }
    public void Analyze(Complex[] spectrum)
    {
        double energy = 0, logEnergy = 0;
        for (int k = 0; k <= Half; k++)
        {
            double power = spectrum[k].Real * spectrum[k].Real + spectrum[k].Imaginary * spectrum[k].Imaginary;
            double log = .5 * Math.Log(Math.Max(1e-18, power));
            _cepstrum[k] = new(log, 0);
            if (k > 0 && k < Half) _cepstrum[Size - k] = _cepstrum[k];
            // Broad-band spectral flatness: attenuate envelope warping on noise/fricatives.
            if (k is >= 4 and <= 85) { energy += power; logEnergy += 2 * log; }
        }
        double flatness = Math.Exp(logEnergy / 82) / Math.Max(1e-18, energy / 82);
        double target = energy < 1e-8 ? 0 : Math.Clamp((.4 - flatness) / .3, 0, 1);
        _voicing += .25 * (target - _voicing); // frame-rate smoothing, heuristic, NOT a voiced classifier
        Transform(true);
        for (int i = 0; i < Size; i++) _cepstrum[i] *= _lifter[i];
        Transform(false);
        for (int k = 0; k <= Half; k++) _logEnvelope[k] = _cepstrum[k].Real;
    }
    public double Gain(int sourceBin, int destinationBin, double formantRatio, double mix)
    {
        // Desired envelope at output f is original E(f / formantRatio), independent of pitch.
        double index = Math.Clamp(destinationBin / formantRatio, 0, Half);
        int lo = (int)index;
        double desired = _logEnvelope[lo] + (_logEnvelope[Math.Min(Half, lo + 1)] - _logEnvelope[lo]) * (index - lo);
        double logGain = Math.Clamp(desired - _logEnvelope[sourceBin], -MaxLogGain, MaxLogGain);
        double hz = destinationBin * (48000.0 / Size);
        // Avoid enhancing sibilance/noisy upper spectrum; ±18 dB hard bound on correction.
        double band = Math.Clamp((8000 - hz) / 3000, 0, 1) * Math.Clamp(hz / 150, 0, 1);
        return Math.Exp(logGain * band * _voicing * mix);
    }
    private void Transform(bool inverse)
    {
        for (int i = 0; i < Size; i++)
        {
            int j = _reverse[i];
            if (j > i) (_cepstrum[i], _cepstrum[j]) = (_cepstrum[j], _cepstrum[i]);
        }
        for (int length = 2; length <= Size; length *= 2)
        {
            int half = length / 2, stride = Size / length;
            for (int begin = 0; begin < Size; begin += length)
                for (int j = 0; j < half; j++)
                {
                    var w = inverse ? Complex.Conjugate(_twiddles[j * stride]) : _twiddles[j * stride];
                    var even = _cepstrum[begin + j]; var odd = _cepstrum[begin + j + half] * w;
                    _cepstrum[begin + j] = even + odd; _cepstrum[begin + j + half] = even - odd;
                }
        }
        if (inverse) for (int i = 0; i < Size; i++) _cepstrum[i] /= Size;
    }
}
