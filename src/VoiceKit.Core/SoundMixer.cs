namespace VoiceKit.Core;

// Samples must not be mutated after publication. All clips use mono 48 kHz.
public sealed record AudioClip(Guid Id, float[] Samples);
public readonly record struct SoundAction(AudioClip? Clip, Guid Id, SoundBehavior Behavior, bool Pressed);

public sealed class SoundMixer
{
    public const int SampleRate = 48000;
    public const int MaxVoices = 32;
    private sealed class Voice
    {
        public AudioClip? Clip;
        public SoundBehavior Behavior = new();
        public int Position, Fade;
        public bool Releasing;
    }
    private readonly Voice[] _voices = Enumerable.Range(0, MaxVoices).Select(_ => new Voice()).ToArray();
    private readonly SpscQueue<SoundAction> _commands = new(128);
    private int _stop;
    private long _rejected;
    public long RejectedCommands => Interlocked.Read(ref _rejected);
    public bool Enqueue(SoundAction command)
    {
        if (_commands.TryEnqueue(command)) return true;
        Interlocked.Increment(ref _rejected);
        // Never leave a held sound running because its key-up was dropped.
        if (!command.Pressed) StopAll();
        return false;
    }
    public void StopAll() => Interlocked.Exchange(ref _stop, 1);
    public void BeginBlock()
    {
        if (Interlocked.Exchange(ref _stop, 0) != 0)
        {
            while (_commands.TryDequeue(out _)) { }
            foreach (var v in _voices) v.Releasing = true;
        }
        while (_commands.TryDequeue(out var command)) Apply(command);
    }
    private void Apply(SoundAction c)
    {
        bool active = false;
        foreach (var v in _voices)
            if (v.Clip?.Id == c.Id && !v.Releasing) active = true;
        if (!c.Pressed)
        {
            foreach (var v in _voices)
                if (v.Clip?.Id == c.Id && v.Behavior.Trigger == TriggerMode.Hold) v.Releasing = true;
            return;
        }
        if (active && c.Behavior.Trigger == TriggerMode.Toggle)
        {
            foreach (var v in _voices) if (v.Clip?.Id == c.Id) v.Releasing = true;
            return;
        }
        if (active && c.Behavior.Retrigger == RetriggerMode.Ignore) return;
        if (active && c.Behavior.Retrigger == RetriggerMode.Restart)
            foreach (var v in _voices) if (v.Clip?.Id == c.Id) v.Releasing = true;
        if (c.Clip is null || c.Clip.Samples.Length == 0) return;
        foreach (var v in _voices)
        {
            if (v.Clip is not null) continue;
            v.Clip = c.Clip;
            v.Behavior = c.Behavior;
            v.Position = v.Fade = 0;
            v.Releasing = false;
            return;
        }
        Interlocked.Increment(ref _rejected);
    }
    public float Next()
    {
        float sum = 0;
        foreach (var v in _voices)
        {
            var clip = v.Clip;
            if (clip is null) continue;
            if (v.Releasing)
            {
                if (--v.Fade <= 0) { v.Clip = null; continue; }
            }
            else v.Fade = Math.Min(240, v.Fade + 1);
            float gain = float.IsFinite(v.Behavior.Gain) ? Math.Clamp(v.Behavior.Gain, 0, 2) : 1;
            // Also taper file boundaries to avoid clicks at loop/restart/end.
            float edge = Math.Min(1, Math.Min((v.Position + 1) / 240f, (clip.Samples.Length - v.Position) / 240f));
            sum += clip.Samples[v.Position++] * gain * (v.Fade / 240f) * edge;
            if (v.Position >= clip.Samples.Length)
            {
                if (v.Behavior.Loop) v.Position = 0;
                else v.Clip = null;
            }
        }
        return sum;
    }
}
