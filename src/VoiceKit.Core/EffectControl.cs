namespace VoiceKit.Core;

/// <summary>Small, explicit remote-control surface. Never replaces a whole preset or touches the vocoder.</summary>
public sealed record EffectPatch(bool? Enabled = null, Dictionary<string, double>? Parameters = null)
{
    public EffectSettings Apply(string effect, EffectSettings current)
    {
        if (Enabled is null && (Parameters is null || Parameters.Count == 0))
            throw new ArgumentException("Пустое изменение эффекта.");
        var next = effect switch
        {
            "pitch" => current with { PitchEnabled = Enabled ?? current.PitchEnabled },
            "wobble" => current with { WobbleEnabled = Enabled ?? current.WobbleEnabled },
            "robot" => current with { RobotEnabled = Enabled ?? current.RobotEnabled },
            "echo" => current with { EchoEnabled = Enabled ?? current.EchoEnabled },
            "reverb" => current with { ReverbEnabled = Enabled ?? current.ReverbEnabled },
            _ => throw new ArgumentException("Этот эффект недоступен с веб-пульта.")
        };
        if (Parameters is null) return next;
        foreach (var (name, value) in Parameters)
        {
            static double Check(double value, double min, double max)
            {
                if (!double.IsFinite(value) || value < min || value > max)
                    throw new ArgumentException($"Значение должно быть от {min} до {max}.");
                return value;
            }
            next = (effect, name) switch
            {
                ("pitch", "semitones") => next with { PitchSemitones = Check(value, -12, 12) },
                ("wobble", "minSemitones") => next with { WobbleMinSemitones = Check(value, -12, 12) },
                ("wobble", "maxSemitones") => next with { WobbleMaxSemitones = Check(value, -12, 12) },
                ("wobble", "rateHz") => next with { WobbleRateHz = Check(value, .1, 12) },
                ("robot", "hz") => next with { RobotHz = Check(value, 10, 300) },
                ("robot", "mix") => next with { RobotMix = Check(value, 0, 1) },
                ("echo", "delayMs") => next with { EchoMs = Check(value, 20, 1500) },
                ("echo", "feedback") => next with { EchoFeedback = Check(value, 0, .85) },
                ("echo", "mix") => next with { EchoMix = Check(value, 0, 1) },
                ("reverb", "size") => next with { ReverbSize = Check(value, 0, .9) },
                ("reverb", "mix") => next with { ReverbMix = Check(value, 0, 1) },
                _ => throw new ArgumentException($"Неизвестный параметр: {name}.")
            };
        }
        if (effect == "wobble" && next.WobbleMinSemitones > next.WobbleMaxSemitones)
            throw new ArgumentException("Нижняя граница воббла не может быть выше верхней.");
        return next;
    }
}

public sealed record RemoteEngineState(bool Running, bool Panic, bool ChainEnabled, bool MicrophoneMuted);
public sealed record PitchControlState(bool Enabled, double Semitones);
public sealed record RobotControlState(bool Enabled, double Hz, double Mix);
public sealed record EchoControlState(bool Enabled, double DelayMs, double Feedback, double Mix);
public sealed record ReverbControlState(bool Enabled, double Size, double Mix);
public sealed record WobbleControlState(bool Enabled, double MinSemitones, double MaxSemitones, double RateHz);
public sealed record RemoteEffectsState(PitchControlState Pitch, RobotControlState Robot,
    EchoControlState Echo, ReverbControlState Reverb, WobbleControlState Wobble);
public sealed record RemoteControlState(long Revision, RemoteEngineState Engine, RemoteEffectsState Effects)
{
    public static RemoteControlState From(AudioSettings settings, bool running, bool panic)
    {
        var e = settings.Effects;
        return new(0, new(running, panic, e.Enabled, settings.MicMuted), new(
            new(e.PitchEnabled, e.PitchSemitones), new(e.RobotEnabled, e.RobotHz, e.RobotMix),
            new(e.EchoEnabled, e.EchoMs, e.EchoFeedback, e.EchoMix),
            new(e.ReverbEnabled, e.ReverbSize, e.ReverbMix),
            new(e.WobbleEnabled, e.WobbleMinSemitones, e.WobbleMaxSemitones, e.WobbleRateHz)));
    }
}
