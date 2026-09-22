using System.Text.Json;
using VoiceKit.Core;
using Xunit;

namespace VoiceKit.Core.Tests;

public class WobbleTests
{
    [Theory]
    [InlineData(.5)]
    [InlineData(3)]
    [InlineData(12)]
    public void LfoIsSampleClockedAndReachesItsBounds(double hz)
    {
        var lfo = new WobbleModulator();
        var settings = new EffectSettings { WobbleEnabled = true, WobbleMinSemitones = -3, WobbleMaxSemitones = 5, WobbleRateHz = hz };
        double last = 0;
        for (int i = 0; i < 48000; i++) last = lfo.Next(settings);
        double min = double.MaxValue, max = double.MinValue;
        int crossings = 0;
        for (int i = 0; i < 96000; i++)
        {
            double value = lfo.Next(settings);
            Assert.InRange(value, -3, 5);
            if (last <= 1 && value > 1) crossings++; // range midpoint
            min = Math.Min(min, value); max = Math.Max(max, value); last = value;
        }
        Assert.InRange(crossings, (int)(2 * hz) - 1, (int)(2 * hz) + 1);
        Assert.InRange(min, -3, -2.99); Assert.InRange(max, 4.99, 5);
    }
    [Fact]
    public void LfoRemainsActiveAtUnisonAndFadesOutOnDisable()
    {
        var lfo = new WobbleModulator();
        var settings = new EffectSettings { WobbleEnabled = true };
        Assert.Equal(0, lfo.Next(settings)); // sine starts at midpoint
        Assert.True(lfo.Active); // do not switch to dry at every zero crossing
        for (int i = 0; i < 24000; i++) lfo.Next(settings);
        double last = 1;
        for (int i = 0; i < 24000; i++) last = lfo.Next(settings with { WobbleEnabled = false });
        Assert.False(lfo.Active); Assert.Equal(0, last);
    }
    [Fact]
    public void ZeroBoundsAreExactBypassAndDisabledWobbleDoesNotAffectExistingPitch()
    {
        var dry = new VoiceProcessor(); var zero = new VoiceProcessor();
        var settings = new EffectSettings { WobbleEnabled = true, WobbleMinSemitones = 0, WobbleMaxSemitones = 0 };
        for (int i = 0; i < 10000; i++)
        {
            float input = .2f * MathF.Sin(i * .03f);
            Assert.Equal(dry.Process(input, new()), zero.Process(input, settings));
        }
        var a = new VoiceProcessor(); var b = new VoiceProcessor();
        var plain = new EffectSettings { PitchEnabled = true, PitchSemitones = 4 };
        for (int i = 0; i < 10000; i++)
        {
            float input = .2f * MathF.Sin(i * .03f);
            Assert.Equal(a.Process(input, plain), b.Process(input, plain with { WobbleMinSemitones = -12, WobbleMaxSemitones = 12 }));
        }
    }
    [Theory]
    [InlineData(false, 7, 440)] // disabled static pitch must not leak into the wobble shift
    [InlineData(true, -12, 220)] // independent offsets add, not multiply in semitone space
    public void FixedWobbleBoundsReallyShiftAudio(bool pitchEnabled, double staticPitch, int expectedHz)
    {
        var dsp = new VoiceProcessor();
        var settings = new EffectSettings
        {
            PitchEnabled = pitchEnabled, PitchSemitones = staticPitch,
            WobbleEnabled = true, WobbleMinSemitones = 12, WobbleMaxSemitones = 12
        };
        float last = 0; int crossings = 0;
        for (int i = 0; i < 96000; i++)
        {
            float sample = dsp.Process(.2f * MathF.Sin(2 * MathF.PI * 220 * i / 48000), settings);
            if (i >= 48000 && last <= 0 && sample > 0) crossings++;
            last = sample;
        }
        Assert.InRange(crossings, expectedHz - 10, expectedHz + 10);
    }
    [Fact]
    public void ExtremeWobbleRemainsFiniteBoundedAndAllocationFreeInGraph()
    {
        var graph = new VoiceGraph();
        graph.Publish(new() { MonitorVoice = true, Effects = new()
        {
            PitchEnabled = true, PitchSemitones = 12, WobbleEnabled = true,
            WobbleMinSemitones = -12, WobbleMaxSemitones = 12, WobbleRateHz = 12
        } });
        graph.BeginBlock();
        for (int i = 0; i < 48000; i++)
        {
            graph.Process(MathF.Sin(i * .043f), out float output, out float monitor);
            Assert.True(float.IsFinite(output) && float.IsFinite(monitor));
            Assert.InRange(output, -.991f, .991f);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4800; i++) graph.Process(.2f, out _, out _);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
    [Fact]
    public void OldSettingsDefaultOffAndNewPresetsRoundTrip()
    {
        var old = JsonSerializer.Deserialize<EffectSettings>("{\"PitchEnabled\":true,\"PitchSemitones\":4}")!;
        Assert.False(old.WobbleEnabled); Assert.Equal(-2, old.WobbleMinSemitones);
        Assert.Equal(2, old.WobbleMaxSemitones); Assert.Equal(3, old.WobbleRateHz);
        var preset = new EffectPreset { Effects = old with { WobbleEnabled = true, WobbleMinSemitones = -5, WobbleRateHz = .1 } };
        Assert.Equal(preset, JsonSerializer.Deserialize<EffectPreset>(JsonSerializer.Serialize(preset)));
    }
    [Fact]
    public void PersistedInvalidLimitsAreNormalizedButRemoteInvertedPatchIsRejectedAtomically()
    {
        var settings = new EffectSettings { WobbleMinSemitones = 40, WobbleMaxSemitones = -20, WobbleRateHz = double.NaN }.Validated();
        Assert.Equal(-12, settings.WobbleMinSemitones); Assert.Equal(12, settings.WobbleMaxSemitones); Assert.Equal(3, settings.WobbleRateHz);
        var original = new EffectSettings { VocoderEnabled = true, PitchSemitones = 5 };
        var bad = new EffectPatch(true, new() { ["minSemitones"] = 8, ["maxSemitones"] = -8 });
        Assert.Throws<ArgumentException>(() => bad.Apply("wobble", original));
        Assert.False(original.WobbleEnabled);
        var good = new EffectPatch(true, new() { ["minSemitones"] = 4, ["maxSemitones"] = 4, ["rateHz"] = .1 });
        Assert.Equal(original with { WobbleEnabled = true, WobbleMinSemitones = 4, WobbleMaxSemitones = 4, WobbleRateHz = .1 }, good.Apply("wobble", original));
        Assert.True(RemoteControlState.From(new() { Effects = good.Apply("wobble", original) }, true, false).Effects.Wobble.Enabled);
    }
}
