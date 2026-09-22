namespace VoiceKit.Core.Calibration;

public sealed record CalibrationChoice(EffectSettings Effects, double PreviewGain)
{
    public override string ToString() => $"Высота {Effects.PitchSemitones:+0.0;-0.0;0.0} st · тембр {Effects.FormantSemitones:+0.0;-0.0;0.0} st";
}
public sealed record CalibrationExploration(ComparisonAudio Audio, CalibrationChoice[] Choices);

/// <summary>A bounded listening grid, NOT an automatic beauty/gender ranking.</summary>
public static class CalibrationExplorer
{
    public static EffectSettings[] Around(EffectSettings center)
    {
        center = center.Validated();
        var choices = new List<EffectSettings>();
        // Centre first; all options preserve the same other effects and keep pitch / envelope independent.
        foreach (double p in new[] { 0.0, -1, 1 })
            foreach (double f in new[] { 0.0, -1.5, 1.5 })
            {
                var candidate = (center with
                {
                    Enabled = true, PitchEnabled = true, FormantEnabled = true,
                    PitchSemitones = center.PitchSemitones + p, FormantSemitones = center.FormantSemitones + f
                }).Validated();
                if (!choices.Contains(candidate)) choices.Add(candidate);
            }
        return choices.ToArray();
    }
    public static CalibrationExploration Render(float[] raw, EffectSettings reference, EffectSettings center,
        AudioClip? carrier, CalibrationSection excerpt, bool matchLevels, CancellationToken ct = default,
        IProgress<int>? progress = null)
    {
        var settings = Around(center);
        var candidates = new float[settings.Length][];
        var choices = new CalibrationChoice[settings.Length];
        ComparisonAudio? first = null;
        for (int i = 0; i < settings.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i == 0)
            {
                first = CalibrationRenderer.Render(raw, reference, settings[i], carrier, excerpt, matchLevels, ct);
                candidates[i] = first.B; choices[i] = new(settings[i], first.GainB);
            }
            else
            {
                var variant = CalibrationRenderer.RenderCandidate(raw, settings[i], carrier, excerpt, matchLevels, ct);
                candidates[i] = variant.Audio; choices[i] = new(settings[i], variant.Gain);
            }
            progress?.Report((i + 1) * 100 / settings.Length);
        }
        ct.ThrowIfCancellationRequested();
        // Do not retain nine copies of the original and A. At most 11 mono excerpt buffers (~74 MB).
        return new(first! with { Candidates = candidates }, choices);
    }
}
