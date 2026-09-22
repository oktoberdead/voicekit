namespace VoiceKit.Core.Calibration;

public sealed record ComparisonAudio(float[] Dry, float[] A, float[] B, double GainDry, double GainA, double GainB, float[][]? Candidates = null)
{
    public int Frames => Dry.Length;
}

/// <summary>Offline processing only. Each take gets its own fresh DSP state and the same source timeline.</summary>
public static class CalibrationRenderer
{
    public static ComparisonAudio Render(float[] raw, EffectSettings a, EffectSettings b, AudioClip? carrier,
        CalibrationSection excerpt, bool matchLevels, CancellationToken ct = default)
    {
        var (start, end) = Range(raw, excerpt);
        var dry = new float[end - start]; var resultA = new float[dry.Length]; var resultB = new float[dry.Length];
        a = a.Validated(); b = b.Validated();
        if (carrier is null && (NeedsCarrier(a) || NeedsCarrier(b)))
            throw new InvalidOperationException("В пресете включён файловый вокодер. Нужна его загруженная несущая, либо выбери нейтральный A и «В B только высота + тембр».");
        var dspA = new VoiceProcessor(); var dspB = new VoiceProcessor();
        // Warm up from the beginning of the same recording (including silent reference), not a fresh DSP at every A/B switch.
        for (int i = 0; i < end; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            float input = float.IsFinite(raw[i]) ? Math.Clamp(raw[i], -8, 8) : 0;
            float outputA = DspMath.Protect(dspA.Process(input, a, carrier));
            float outputB = DspMath.Protect(dspB.Process(input, b, carrier));
            if (i < start) continue;
            dry[i - start] = input; resultA[i - start] = outputA; resultB[i - start] = outputB;
        }
        double gd = Normalize(dry, matchLevels), ga = Normalize(resultA, matchLevels), gb = Normalize(resultB, matchLevels);
        return new(dry, resultA, resultB, gd, ga, gb);
    }
    private static (int Start, int End) Range(float[] raw, CalibrationSection excerpt)
    {
        if (raw.Length > CalibrationScript.MaxSamples || raw.Length == 0) throw new ArgumentException("Некорректная длина образца.");
        if (excerpt.StartSeconds < CalibrationScript.NoiseSeconds || excerpt.DurationSeconds <= 0)
            throw new ArgumentException("Прослушивать можно только речевую часть образца.");
        int start = checked(excerpt.StartSeconds * CalibrationScript.SampleRate);
        int end = Math.Min(raw.Length, checked(checked(excerpt.StartSeconds + excerpt.DurationSeconds) * CalibrationScript.SampleRate));
        if (end - start < CalibrationScript.SampleRate / 2) throw new ArgumentException("Этот фрагмент не записан или слишком короткий. Выбери «Вся речь».");
        return (start, end);
    }
    internal static (float[] Audio, double Gain) RenderCandidate(float[] raw, EffectSettings settings, AudioClip? carrier,
        CalibrationSection excerpt, bool matchLevels, CancellationToken ct)
    {
        var (start, end) = Range(raw, excerpt);
        settings = settings.Validated();
        if (carrier is null && NeedsCarrier(settings)) throw new InvalidOperationException("Для кандидата нужна файловая несущая.");
        var result = new float[end - start]; var dsp = new VoiceProcessor();
        for (int i = 0; i < end; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            float input = float.IsFinite(raw[i]) ? Math.Clamp(raw[i], -8, 8) : 0;
            float output = DspMath.Protect(dsp.Process(input, settings, carrier));
            if (i >= start) result[i - start] = output;
        }
        return (result, Normalize(result, matchLevels));
    }
    private static bool NeedsCarrier(EffectSettings s) => s.Enabled && s.VocoderEnabled && s.Carrier == CarrierKind.File;
    private static double Normalize(float[] audio, bool match)
    {
        double energy = 0, peak = 0;
        foreach (float sample in audio) { energy += sample * (double)sample; peak = Math.Max(peak, Math.Abs(sample)); }
        double rms = Math.Sqrt(energy / audio.Length);
        // RMS matching is an approximation, not perceptual loudness/LUFS. Never amplify near-silence.
        double gain = match && rms > .001 ? Math.Min(4, .1 / rms) : 1;
        if (peak > 0) gain = Math.Min(gain, .9 / peak);
        for (int i = 0; i < audio.Length; i++) audio[i] *= (float)gain;
        return gain;
    }
}

/// <summary>Single reader, atomic control setters. One shared cursor with 10 ms-ish crossfades, no simultaneous players.</summary>
public sealed class ComparisonPlayback
{
    private readonly ComparisonAudio _audio;
    private readonly float[] _weights;
    private readonly float[][] _sources;
    private int _position, _variant, _muted;
    private float _targetVolume = .35f, _volume;
    public ComparisonPlayback(ComparisonAudio audio, int variant)
    {
        if (audio.Frames == 0 || audio.A.Length != audio.Frames || audio.B.Length != audio.Frames)
            throw new ArgumentException("A/B-фрагменты должны быть одной длины.");
        if (audio.Candidates is { Length: < 1 or > 9 }) throw new ArgumentException("Допустимо от 1 до 9 кандидатов.");
        _sources = audio.Candidates is null ? [audio.Dry, audio.A, audio.B] : [audio.Dry, audio.A, .. audio.Candidates];
        if (_sources.Any(x => x is null || x.Length != audio.Frames)) throw new ArgumentException("Кандидаты должны быть одной длины.");
        _weights = new float[_sources.Length];
        _audio = audio; Select(variant); _weights[variant] = 1;
    }
    public int Position => Volatile.Read(ref _position);
    public int Frames => _audio.Frames;
    public void Select(int variant)
    {
        if (variant < 0 || variant >= _sources.Length) throw new ArgumentOutOfRangeException(nameof(variant));
        Volatile.Write(ref _variant, variant);
    }
    public void SetVolume(float value) => Volatile.Write(ref _targetVolume, float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0);
    public void Mute() => Volatile.Write(ref _muted, 1);
    public int ReadStereo(Span<float> buffer)
    {
        if (buffer.Length % 2 != 0) throw new ArgumentException("Нужен целый стереокадр.");
        int position = _position, frames = Math.Min(buffer.Length / 2, _audio.Frames - position);
        int variant = Volatile.Read(ref _variant);
        float volume = Volatile.Read(ref _targetVolume);
        for (int i = 0; i < frames; i++)
        {
            for (int v = 0; v < _weights.Length; v++) _weights[v] += ((v == variant ? 1 : 0) - _weights[v]) * .004f;
            _volume += (volume - _volume) * .002f;
            int cursor = position + i;
            float sample = 0;
            for (int v = 0; v < _weights.Length; v++) sample += _sources[v][cursor] * _weights[v];
            float edge = Math.Min(1, Math.Min((cursor + 1) / 240f, (_audio.Frames - cursor) / 240f));
            sample = Volatile.Read(ref _muted) != 0 ? 0 : Math.Clamp(sample * _volume * edge, -.98f, .98f);
            buffer[2 * i] = buffer[2 * i + 1] = sample;
        }
        buffer[(frames * 2)..].Clear();
        Volatile.Write(ref _position, position + frames);
        return frames * 2;
    }
}
