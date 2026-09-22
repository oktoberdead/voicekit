using VoiceKit.Core;
using Xunit;

namespace VoiceKit.Core.Tests;

public class DspTests
{
    [Fact]
    public void DisabledEffectsDoNotDuplicateDryMicrophone()
    {
        var dsp = new VoiceProcessor();
        var s = new EffectSettings();
        for (int i = 0; i < 10000; i++)
        {
            float input = .1f * MathF.Sin(i * .031f);
            Assert.Equal(input, dsp.Process(input, s), 6);
        }
    }
    [Fact]
    public void DryDebugMonitorCannotAffectTransmission()
    {
        var a = new VoiceGraph(); var b = new VoiceGraph();
        a.Publish(new()); b.Publish(new() { MonitorDry = true, MonitorVoice = true, MonitorSounds = true });
        a.BeginBlock(); b.BeginBlock();
        for (int i = 0; i < 2000; i++)
        {
            a.Process(.1f, out float x, out _); b.Process(.1f, out float y, out _);
            Assert.Equal(x, y);
        }
    }
    [Fact]
    public void PanicIsImmediateAndStaysLatchedAcrossSettingChanges()
    {
        var graph = new VoiceGraph();
        graph.Publish(new() { MonitorVoice = true, Effects = new() { EchoEnabled = true } }); graph.BeginBlock();
        for (int i = 0; i < 20000; i++) graph.Process(.8f, out _, out _);
        graph.SetPanic(true);
        graph.Publish(new() { MonitorVoice = true }); graph.BeginBlock();
        for (int i = 0; i < 2000; i++)
        {
            graph.Process(1, out float output, out float monitor);
            Assert.Equal(0, output); Assert.Equal(0, monitor);
        }
        Assert.True(graph.IsPanicked);
    }
    [Fact]
    public void MicMuteIncludesEffectTailsButNotSoundpad()
    {
        var graph = new VoiceGraph();
        var s = new AudioSettings { Effects = new() { EchoEnabled = true, ReverbEnabled = true } };
        graph.Publish(s); graph.BeginBlock();
        for (int i = 0; i < 24000; i++) graph.Process(.2f, out _, out _);
        graph.Publish(s with { MicMuted = true }); graph.BeginBlock();
        float output = 1;
        for (int i = 0; i < 48000; i++) graph.Process(.2f, out output, out _);
        Assert.InRange(Math.Abs(output), 0, .0001);
        var clip = new AudioClip(Guid.NewGuid(), Enumerable.Repeat(.2f, 4800).ToArray());
        graph.Sounds.Enqueue(new(clip, clip.Id, new(), true)); graph.BeginBlock();
        for (int i = 0; i < 1000; i++) graph.Process(.2f, out output, out _);
        Assert.True(output > .05f);
    }
    [Fact]
    public void ExtremeSettingsStillProduceFiniteBoundedOutputs()
    {
        var graph = new VoiceGraph();
        graph.Publish(new()
        {
            MicGain = 4, OutputGain = 2, MonitorVoice = true, MonitorGain = 2,
            Effects = new()
            {
                PitchEnabled = true, PitchSemitones = 12, RobotEnabled = true, VocoderEnabled = true,
                EchoEnabled = true, EchoFeedback = .85, EchoMix = 1, ReverbEnabled = true, ReverbSize = .9, ReverbMix = 1
            }
        });
        graph.BeginBlock();
        for (int i = 0; i < 96000; i++)
        {
            graph.Process(i % 997 == 0 ? float.NaN : 4 * MathF.Sin(i * .023f), out float x, out float y);
            Assert.True(float.IsFinite(x) && float.IsFinite(y));
            Assert.InRange(x, -.991f, .991f); Assert.InRange(y, -.991f, .991f);
        }
    }
    [Fact]
    public void VocoderNeedsModulatorEvenWithFileCarrier()
    {
        var dsp = new VoiceProcessor();
        var s = new EffectSettings { VocoderEnabled = true, Carrier = CarrierKind.File };
        var file = new AudioClip(Guid.NewGuid(), Enumerable.Repeat(.5f, 4800).ToArray());
        for (int i = 0; i < 10000; i++) Assert.Equal(0, dsp.Process(0, s, file));
    }
    [Fact]
    public void FileCarrierActuallyModulatesSpeech()
    {
        var dsp = new VoiceProcessor();
        var s = new EffectSettings { VocoderEnabled = true, Carrier = CarrierKind.File, CarrierLoop = true };
        var random = new Random(42);
        var samples = Enumerable.Range(0, 4800).Select(_ => (float)(random.NextDouble() - .5)).ToArray();
        var file = new AudioClip(Guid.NewGuid(), samples);
        double energy = 0;
        for (int i = 0; i < 48000; i++)
        {
            float result = dsp.Process(.2f * MathF.Sin(i * .043f), s, file);
            if (i > 24000) energy += result * result;
        }
        Assert.True(energy > .001);
    }
    [Fact]
    public void PitchMovesSteadySineUpAnOctave()
    {
        var dsp = new VoiceProcessor();
        var s = new EffectSettings { PitchEnabled = true, PitchSemitones = 12 };
        int crossings = 0; float previous = 0;
        for (int i = 0; i < 96000; i++)
        {
            float output = dsp.Process(.2f * MathF.Sin(2 * MathF.PI * 220 * i / 48000), s);
            if (i >= 48000 && previous <= 0 && output > 0) crossings++;
            previous = output;
        }
        Assert.InRange(crossings, 430, 450);
    }
    [Fact]
    public void InvalidNumericSettingsAreClampedBeforePublication()
    {
        var a = new AudioSettings { MicGain = double.NaN, Effects = new() { EchoFeedback = 10, PitchSemitones = double.PositiveInfinity } }.Validated();
        Assert.Equal(1, a.MicGain); Assert.Equal(.85, a.Effects.EchoFeedback); Assert.Equal(0, a.Effects.PitchSemitones);
    }
    [Fact]
    public void SteadyStateDspDoesNotAllocateManagedMemory()
    {
        var graph = new VoiceGraph();
        graph.Publish(new() { Effects = new() { PitchEnabled = true, VocoderEnabled = true, ReverbEnabled = true, EchoEnabled = true } });
        graph.BeginBlock();
        for (int i = 0; i < 20000; i++) graph.Process(.1f, out _, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int block = 0; block < 10; block++)
        {
            graph.BeginBlock();
            for (int i = 0; i < 480; i++) graph.Process(.1f, out _, out _);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
