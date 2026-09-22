namespace VoiceKit.Core;

internal static class DspMath
{
    public static float Smooth(ref float state, double target, float speed = .001f)
    {
        state += ((float)target - state) * speed;
        if (Math.Abs(state - target) < 1e-6) state = (float)target;
        return state;
    }
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;
    // Unity below -6 dB, continuous slope into a bounded, zero-lookahead soft knee.
    public static float Protect(float x)
    {
        if (!float.IsFinite(x)) return 0;
        float a = Math.Abs(x);
        return a <= .5f ? x : MathF.CopySign(.5f + .49f * MathF.Tanh((a - .5f) / .49f), x);
    }
}

internal sealed class BandPass
{
    private readonly double _b0, _b2, _a1, _a2;
    private double _z1, _z2;
    public BandPass(double frequency, double q)
    {
        double w = 2 * Math.PI * frequency / SoundMixer.SampleRate;
        double alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
        _b0 = alpha / a0; _b2 = -alpha / a0;
        _a1 = -2 * Math.Cos(w) / a0; _a2 = (1 - alpha) / a0;
    }
    public float Process(float x)
    {
        double y = _b0 * x + _z1;
        _z1 = -_a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        if (Math.Abs(_z1) < 1e-25) _z1 = 0;
        if (Math.Abs(_z2) < 1e-25) _z2 = 0;
        return (float)y;
    }
}

internal sealed class Vocoder
{
    private const int Bands = 20;
    private readonly BandPass[] _modulator = new BandPass[Bands], _carrier = new BandPass[Bands];
    private readonly float[] _envelopes = new float[Bands];
    private readonly double[] _phases = new double[3];
    private static readonly double[] Intervals = [1, 1.2599210499, 1.4983070769];
    private AudioClip? _file;
    private int _position, _attack;
    private bool _wasEnabled;
    private float _hz = 110, _gain = 1, _wet;
    public Vocoder()
    {
        for (int i = 0; i < Bands; i++)
        {
            double hz = 100 * Math.Pow(7500.0 / 100, (double)i / (Bands - 1));
            _modulator[i] = new(hz, 4);
            _carrier[i] = new(hz, 4);
        }
    }
    public float Process(float input, EffectSettings s, AudioClip? file)
    {
        bool enabled = s.Enabled && s.VocoderEnabled;
        if (!ReferenceEquals(file, _file)) { _file = file; _position = _attack = 0; }
        if (enabled && !_wasEnabled && s.CarrierRestart) _position = _attack = 0;
        _wasEnabled = enabled;
        DspMath.Smooth(ref _hz, s.CarrierHz);
        DspMath.Smooth(ref _gain, s.CarrierGain);
        float carrier = 0;
        if (s.Carrier == CarrierKind.File)
        {
            if (enabled && _file is { Samples.Length: > 0 } && _position < _file.Samples.Length)
            {
                carrier = _file.Samples[_position++] * Math.Min(1, ++_attack / 240f);
                carrier *= Math.Min(1, (_file.Samples.Length - _position + 1) / 240f);
                if (_position == _file.Samples.Length && s.CarrierLoop) _position = _attack = 0;
            }
        }
        else
        {
            int voices = s.CarrierChord ? 3 : 1;
            for (int i = 0; i < voices; i++)
            {
                double dt = _hz * Intervals[i] / SoundMixer.SampleRate;
                _phases[i] = (_phases[i] + dt) % 1;
                carrier += (float)(2 * _phases[i] - 1 - PolyBlep(_phases[i], dt)) / voices;
            }
        }
        float result = 0;
        for (int i = 0; i < Bands; i++)
        {
            float energy = Math.Abs(_modulator[i].Process(input));
            _envelopes[i] += (energy - _envelopes[i]) * (energy > _envelopes[i] ? .01f : .0007f);
            if (_envelopes[i] < 1e-20f) _envelopes[i] = 0;
            result += _carrier[i].Process(carrier * _gain) * _envelopes[i];
        }
        return DspMath.Lerp(input, result * 12, DspMath.Smooth(ref _wet, enabled ? 1 : 0));
    }
    private static double PolyBlep(double t, double dt)
    {
        if (t < dt) { t /= dt; return t + t - t * t - 1; }
        if (t > 1 - dt) { t = (t - 1) / dt; return t * t + t + t + 1; }
        return 0;
    }
}

internal sealed class Echo
{
    private readonly float[] _buffer = new float[SoundMixer.SampleRate * 2];
    private int _position;
    private float _time = 280, _feedback = .3f, _send, _mix = .25f;
    public float Process(float input, EffectSettings s)
    {
        DspMath.Smooth(ref _time, s.EchoMs, .0001f);
        DspMath.Smooth(ref _feedback, s.EchoFeedback);
        DspMath.Smooth(ref _mix, s.EchoMix);
        DspMath.Smooth(ref _send, s.Enabled && s.EchoEnabled ? 1 : 0);
        double pos = (_position - _time * 48 + _buffer.Length) % _buffer.Length;
        int index = (int)pos;
        float delayed = DspMath.Lerp(_buffer[index], _buffer[(index + 1) % _buffer.Length], (float)(pos - index));
        float feedbackSample = Math.Clamp(input * _send + delayed * _feedback, -8, 8);
        _buffer[_position] = Math.Abs(feedbackSample) < 1e-20f ? 0 : feedbackSample;
        _position = (_position + 1) % _buffer.Length;
        return input + delayed * _mix; // disable stops the send; existing tail decays naturally
    }
}

internal sealed class Reverb
{
    private sealed class Comb(int length)
    {
        private readonly float[] _buffer = new float[length];
        private int _index;
        private float _low;
        public float Process(float input, float feedback)
        {
            float y = _buffer[_index];
            _low += .4f * (y - _low);
            if (Math.Abs(_low) < 1e-20f) _low = 0;
            _buffer[_index] = input + _low * feedback;
            _index = (_index + 1) % _buffer.Length;
            return y;
        }
    }
    private sealed class AllPass(int length)
    {
        private readonly float[] _buffer = new float[length];
        private int _index;
        public float Process(float input)
        {
            float b = _buffer[_index], y = b - .5f * input;
            float next = input + .5f * y;
            _buffer[_index] = Math.Abs(next) < 1e-20f ? 0 : next;
            _index = (_index + 1) % _buffer.Length;
            return y;
        }
    }
    private readonly Comb[] _combs = [new(1557), new(1617), new(1491), new(1423)];
    private readonly AllPass[] _diffusers = [new(225), new(556)];
    private float _send, _feedback = .74f, _mix = .25f;
    public float Process(float input, EffectSettings s)
    {
        DspMath.Smooth(ref _send, s.Enabled && s.ReverbEnabled ? 1 : 0);
        DspMath.Smooth(ref _feedback, .55 + s.ReverbSize * .35);
        DspMath.Smooth(ref _mix, s.ReverbMix);
        float wet = 0;
        foreach (var comb in _combs) wet += comb.Process(input * _send * .2f, _feedback);
        foreach (var diffuser in _diffusers) wet = diffuser.Process(wet);
        return input + wet * _mix;
    }
}

public sealed class VoiceProcessor
{
    private readonly PitchShifter _pitch = new();
    private readonly WobbleModulator _wobble = new();
    private readonly Vocoder _vocoder = new();
    private readonly Echo _echo = new();
    private readonly Reverb _reverb = new();
    private double _robotPhase;
    private float _robotHz = 70, _robotWet, _enabled = 1;
    public float Process(float input, EffectSettings settings, AudioClip? carrier = null)
    {
        input = float.IsFinite(input) ? Math.Clamp(input, -8, 8) : 0;
        double wobble = _wobble.Next(settings);
        bool modulated = _wobble.Active;
        // One STFT stage: no second FFT, buffering layer or dry copy for wobble.
        double pitch = modulated ? (settings.PitchEnabled ? settings.PitchSemitones : 0) + wobble : settings.PitchSemitones;
        bool formants = settings.Enabled && settings.FormantEnabled;
        // Formant-only processing must not activate a stored but disabled pitch value.
        if (formants && !settings.PitchEnabled && !modulated) pitch = 0;
        float x = _pitch.Process(input, pitch, settings.Enabled && (settings.PitchEnabled || modulated || formants),
            modulated, formants, settings.FormantSemitones);
        DspMath.Smooth(ref _robotHz, settings.RobotHz);
        _robotPhase = (_robotPhase + _robotHz / SoundMixer.SampleRate) % 1;
        float robot = x * (float)Math.Cos(2 * Math.PI * _robotPhase);
        x = DspMath.Lerp(x, robot, DspMath.Smooth(ref _robotWet,
            settings.Enabled && settings.RobotEnabled ? settings.RobotMix : 0));
        x = _vocoder.Process(x, settings, carrier);
        x = _echo.Process(x, settings);
        x = _reverb.Process(x, settings);
        return DspMath.Lerp(input, x, DspMath.Smooth(ref _enabled, settings.Enabled ? 1 : 0));
    }
}

public sealed class VoiceGraph
{
    private readonly VoiceProcessor _processor = new();
    public SoundMixer Sounds { get; } = new();
    private AudioSettings _settings = new();
    private AudioClip? _carrier;
    private AudioSettings _block = new();
    private AudioClip? _blockCarrier;
    private int _panic;
    private float _micGain = 1, _soundGain = .7f, _outputGain = .85f, _monitorGain = .6f;
    private float _micGate = 1, _monitorVoice, _monitorSound, _monitorDry;
    public bool IsPanicked => Volatile.Read(ref _panic) != 0;
    public void Publish(AudioSettings settings) => Volatile.Write(ref _settings, settings.Validated());
    public void SetCarrier(AudioClip? clip) => Volatile.Write(ref _carrier, clip);
    public void SetPanic(bool panic)
    {
        Volatile.Write(ref _panic, panic ? 1 : 0);
        if (panic) Sounds.StopAll();
    }
    public void BeginBlock()
    {
        _block = Volatile.Read(ref _settings);
        _blockCarrier = Volatile.Read(ref _carrier);
        Sounds.BeginBlock();
    }
    public void Process(float input, out float transmission, out float monitor)
    {
        var s = _block;
        // Mute both before DSP and after it, so delay/reverb tails cannot leak through mic mute.
        float gate = DspMath.Smooth(ref _micGate, s.MicMuted || IsPanicked ? 0 : 1);
        float dry = input * DspMath.Smooth(ref _micGain, s.MicGain) * gate;
        float voice = _processor.Process(dry, s.Effects, _blockCarrier) * gate;
        float sounds = Sounds.Next() * DspMath.Smooth(ref _soundGain, s.SoundGain);
        transmission = DspMath.Protect((voice + sounds) * DspMath.Smooth(ref _outputGain, s.OutputGain));
        monitor = DspMath.Protect((voice * DspMath.Smooth(ref _monitorVoice, s.MonitorVoice ? 1 : 0)
            + sounds * DspMath.Smooth(ref _monitorSound, s.MonitorSounds ? 1 : 0)
            + dry * DspMath.Smooth(ref _monitorDry, s.MonitorDry ? 1 : 0))
            * DspMath.Smooth(ref _monitorGain, s.MonitorGain));
        if (IsPanicked) transmission = monitor = 0;
    }
}
