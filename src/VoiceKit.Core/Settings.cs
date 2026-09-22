namespace VoiceKit.Core;

public enum CarrierKind { Synth, File }
public enum TriggerMode { OneShot, Hold, Toggle }
public enum RetriggerMode { Ignore, Restart, Overlap }
public enum VoiceCommand { ToggleMicrophone, ToggleEffects, ToggleVoiceMonitor, StopSounds, Panic, ToggleEngine, PlaySound, ApplyPreset }

public sealed record EffectSettings
{
    public bool Enabled { get; init; } = true;
    public bool PitchEnabled { get; init; }
    public double PitchSemitones { get; init; }
    public bool RobotEnabled { get; init; }
    public double RobotHz { get; init; } = 70;
    public double RobotMix { get; init; } = .8;
    public bool VocoderEnabled { get; init; }
    public CarrierKind Carrier { get; init; }
    public double CarrierHz { get; init; } = 110;
    public bool CarrierChord { get; init; }
    public double CarrierGain { get; init; } = 1;
    public bool CarrierLoop { get; init; } = true;
    public bool CarrierRestart { get; init; } = true;
    public bool EchoEnabled { get; init; }
    public double EchoMs { get; init; } = 280;
    public double EchoFeedback { get; init; } = .3;
    public double EchoMix { get; init; } = .25;
    public bool ReverbEnabled { get; init; }
    public double ReverbSize { get; init; } = .55;
    public double ReverbMix { get; init; } = .25;

    public EffectSettings Validated() => this with
    {
        PitchSemitones = Safe(PitchSemitones, -12, 12, 0),
        RobotHz = Safe(RobotHz, 10, 300, 70), RobotMix = Safe(RobotMix, 0, 1, .8),
        CarrierHz = Safe(CarrierHz, 45, 440, 110), CarrierGain = Safe(CarrierGain, 0, 4, 1),
        EchoMs = Safe(EchoMs, 20, 1500, 280), EchoFeedback = Safe(EchoFeedback, 0, .85, .3),
        EchoMix = Safe(EchoMix, 0, 1, .25), ReverbSize = Safe(ReverbSize, 0, .9, .55),
        ReverbMix = Safe(ReverbMix, 0, 1, .25),
        Carrier = Enum.IsDefined(Carrier) ? Carrier : CarrierKind.Synth
    };
    internal static double Safe(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

// Immutable snapshots are published from the UI and read once per audio block.
public sealed record AudioSettings
{
    public EffectSettings Effects { get; init; } = new();
    public double MicGain { get; init; } = 1;
    public double SoundGain { get; init; } = .7;
    public double OutputGain { get; init; } = .85;
    public double MonitorGain { get; init; } = .6;
    public bool MicMuted { get; init; }
    public bool MonitorVoice { get; init; }
    public bool MonitorSounds { get; init; } = true;
    public bool MonitorDry { get; init; }
    public AudioSettings Validated() => this with
    {
        Effects = (Effects ?? new()).Validated(),
        MicGain = EffectSettings.Safe(MicGain, 0, 4, 1),
        SoundGain = EffectSettings.Safe(SoundGain, 0, 2, .7),
        OutputGain = EffectSettings.Safe(OutputGain, 0, 2, .85),
        MonitorGain = EffectSettings.Safe(MonitorGain, 0, 2, .6)
    };
}

public sealed record SoundBehavior(TriggerMode Trigger = TriggerMode.OneShot,
    bool Loop = false, RetriggerMode Retrigger = RetriggerMode.Restart, float Gain = 1);

public sealed record SoundEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Sound";
    public string StoredFile { get; init; } = "";
    public string Hotkey { get; init; } = "";
    public SoundBehavior Behavior { get; init; } = new();
    public override string ToString() => Name;
}
public sealed record EffectPreset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Preset";
    public string Hotkey { get; init; } = "";
    public EffectSettings Effects { get; init; } = new();
    public string? CarrierFile { get; init; }
    public override string ToString() => Name;
}
public sealed record HotkeyBinding(VoiceCommand Command, string Gesture, Guid? Target = null);
public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string? CaptureId { get; init; }
    public string? OutputId { get; init; }
    public string? MonitorId { get; init; }
    public int BufferMs { get; init; } = 30;
    public int RemotePort { get; init; } = 8765;
    public AudioSettings Audio { get; init; } = new();
    public string? CarrierFile { get; init; }
    public List<SoundEntry> Library { get; init; } = [];
    public List<EffectPreset> Presets { get; init; } = [];
    public List<HotkeyBinding> Hotkeys { get; init; } =
    [
        new(VoiceCommand.ToggleMicrophone, "Ctrl+Alt+F1"),
        new(VoiceCommand.ToggleEffects, "Ctrl+Alt+F2"),
        new(VoiceCommand.ToggleVoiceMonitor, "Ctrl+Alt+F3"),
        new(VoiceCommand.StopSounds, "Ctrl+Alt+F4"),
        new(VoiceCommand.ToggleEngine, "Ctrl+Alt+F5"),
        new(VoiceCommand.Panic, "Ctrl+Alt+F12")
    ];
}
