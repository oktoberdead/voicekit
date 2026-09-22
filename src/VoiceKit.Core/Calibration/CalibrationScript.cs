namespace VoiceKit.Core.Calibration;

public sealed record CalibrationSection(string Name, int StartSeconds, int DurationSeconds, string Prompt)
{
    public int EndSeconds => StartSeconds + DurationSeconds;
    public override string ToString() => Name;
}

public static class CalibrationScript
{
    public const int Version = 1;
    public const int SampleRate = 48000;
    public const int DurationSeconds = 38;
    public const int NoiseSeconds = 3;
    public const int MaxSamples = SampleRate * DurationSeconds;
    public const string Phrase = "Сегодня утром я открыл окно. На улице тихо, а солнце уже светит. Сейчас я говорю своим обычным голосом.";
    public static IReadOnlyList<CalibrationSection> Stages { get; } = Array.AsReadOnly<CalibrationSection>([
        new("Тишина", 0, 3, "Помолчи. Измеряем фон, микрофон уже записывает."),
        new("Гласная А", 3, 3, "Тяни «а-а-а» обычным голосом. Не меняй высоту специально."),
        new("Гласная И", 6, 3, "Тяни «и-и-и» обычным голосом."),
        new("Гласная У", 9, 3, "Тяни «у-у-у» обычным голосом."),
        new("Фраза", 12, 14, Phrase + " Если осталось время, повтори."),
        new("Свободная речь", 26, 12, "Расскажи, как прошёл твой день. Говори так, как обычно говоришь в игре или разговоре.")
    ]);
    public static IReadOnlyList<CalibrationSection> Excerpts { get; } = Array.AsReadOnly<CalibrationSection>([
        new("Фраза", 12, 14, ""), new("Свободная речь", 26, 12, ""),
        new("Гласные", 3, 9, ""), new("Вся речь", 3, 35, "")
    ]);
    public static CalibrationSection StageAt(double seconds) => Stages.FirstOrDefault(x => seconds < x.EndSeconds) ?? Stages[^1];
}

/// <summary>Saved only by an explicit user action. This is a reference sample, not a trained voice model.</summary>
public sealed record CalibrationProfile
{
    public int SchemaVersion { get; init; } = 1;
    public int ScriptVersion { get; init; } = CalibrationScript.Version;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Мой голос";
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string CaptureDeviceName { get; init; } = "";
    public EffectSettings Reference { get; init; } = new();
    public EffectSettings Candidate { get; init; } = new();
    public double TargetMedianHz { get; init; } = 180;
    public bool MatchLevels { get; init; } = true;
    public string? CarrierFile { get; init; }
    public string Preferred { get; init; } = "B";
    public VoiceAnalysis? Analysis { get; init; }
}
