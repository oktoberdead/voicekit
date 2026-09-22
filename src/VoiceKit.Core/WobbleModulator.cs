namespace VoiceKit.Core;

/// <summary>Sample-clocked sine LFO in semitones, with smoothed limits, rate and enable envelope.</summary>
internal sealed class WobbleModulator
{
    private double _phase;
    private bool _initialized;
    private float _minimum = -2, _maximum = 2, _rate = 3, _amount;
    // Keep the shifted path active at each zero crossing: a dry/wet switch there causes comb filtering.
    public bool Active => _amount > 0 && (Math.Abs(_minimum) > .001f || Math.Abs(_maximum) > .001f);
    public double Next(EffectSettings settings)
    {
        bool enabled = settings.Enabled && settings.WobbleEnabled;
        if (!_initialized || (!enabled && _amount == 0))
        {
            _minimum = (float)settings.WobbleMinSemitones;
            _maximum = (float)settings.WobbleMaxSemitones;
            _rate = (float)settings.WobbleRateHz;
            _initialized = true;
        }
        if (!enabled && _amount == 0) return 0;
        DspMath.Smooth(ref _minimum, settings.WobbleMinSemitones);
        DspMath.Smooth(ref _maximum, settings.WobbleMaxSemitones);
        DspMath.Smooth(ref _rate, settings.WobbleRateHz);
        DspMath.Smooth(ref _amount, enabled ? 1 : 0);
        double value = (_minimum + _maximum) * .5 + (_maximum - _minimum) * .5 * Math.Sin(2 * Math.PI * _phase);
        _phase = (_phase + _rate / SoundMixer.SampleRate) % 1;
        return value * _amount;
    }
}
