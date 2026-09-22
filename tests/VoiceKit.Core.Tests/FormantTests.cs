using System.Numerics;
using System.Text.Json;
using VoiceKit.Core;
using VoiceKit.Core.Calibration;
using VoiceKit.Core.Tests.Fixtures;
using Xunit;

namespace VoiceKit.Core.Tests;

public class FormantTests
{
    private static float[] Vowel()
    {
        var data = new float[96000];
        for (int h = 1; h < 90; h++)
        {
            double f = h * 128;
            double amplitude = (.01 + Math.Exp(-.5 * Math.Pow((f - 550) / 110, 2))
                + .8 * Math.Exp(-.5 * Math.Pow((f - 1500) / 140, 2))
                + .45 * Math.Exp(-.5 * Math.Pow((f - 2600) / 190, 2))) / (1 + Math.Pow(f / 4500, 4));
            for (int i = 0; i < data.Length; i++) data[i] += (float)(amplitude * Math.Sin(2 * Math.PI * f * i / 48000));
        }
        float gain = .2f / data.Max(x => Math.Abs(x));
        for (int i = 0; i < data.Length; i++) data[i] *= gain;
        return data;
    }
    private static float[] Process(float[] raw, EffectSettings settings)
    {
        var dsp = new VoiceProcessor(); var result = new float[raw.Length];
        for (int i = 0; i < raw.Length; i++) result[i] = dsp.Process(raw[i], settings);
        return result;
    }
    private static double Pitch(float[] processed)
    {
        var take = new float[4 * 48000];
        Array.Copy(processed, processed.Length - 48000, take, 3 * 48000, 48000);
        return VoiceAnalyzer.Analyze(take).MedianF0Hz ?? throw new InvalidOperationException("No F0 detected");
    }
    // FFT metric of a synthetic vowel's second resonance region, NOT clinical F2 estimation.
    private static double BandCentroid(float[] samples)
    {
        const int n = 65536;
        var data = new Complex[n];
        for (int i = 0; i < n; i++) data[i] = samples[samples.Length - n + i] * (.5 - .5 * Math.Cos(2 * Math.PI * i / n));
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }
        for (int length = 2; length <= n; length *= 2)
        {
            var step = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (int begin = 0; begin < n; begin += length)
            {
                var w = Complex.One;
                for (int j = 0; j < length / 2; j++, w *= step)
                {
                    var even = data[begin + j]; var odd = data[begin + j + length / 2] * w;
                    data[begin + j] = even + odd; data[begin + j + length / 2] = even - odd;
                }
            }
        }
        double sum = 0, weighted = 0;
        for (int k = 0; k <= n / 2; k++)
        {
            double hz = k * 48000.0 / n;
            if (hz is < 1100 or > 2150) continue;
            double power = data[k].Magnitude * data[k].Magnitude;
            sum += power; weighted += hz * power;
        }
        return weighted / sum;
    }
    [Fact]
    public void DisabledFormantsAreBitIdenticalToThePreviousPitchEngine()
    {
        var old = new LegacyPitchShifter(); var current = new PitchShifter();
        for (int i = 0; i < 144000; i++)
        {
            float input = .17f * MathF.Sin(i * .031f) + .04f * MathF.Sin(i * .113f);
            double pitch = i < 48000 ? 4 : i < 96000 ? -7 : Math.Sin(i * .001) * 8;
            bool enabled = i > 4096 && i < 140000, modulated = i >= 96000;
            Assert.Equal(old.Process(input, pitch, enabled, modulated),
                current.Process(input, pitch, enabled, modulated, false, 5));
        }
    }
    [Fact]
    public void FormantOnlyMovesEnvelopeInBothDirectionsWithoutActivatingStoredPitch()
    {
        var raw = Vowel();
        var basic = new EffectSettings { FormantEnabled = true, PitchEnabled = false, PitchSemitones = 9 };
        var neutral = Process(raw, basic);
        var higher = Process(raw, basic with { FormantSemitones = 4 });
        var lower = Process(raw, basic with { FormantSemitones = -4 });
        Assert.InRange(Pitch(higher), 126, 130); Assert.InRange(Pitch(lower), 126, 130);
        double baseline = BandCentroid(neutral);
        Assert.True(BandCentroid(higher) > baseline + 150);
        Assert.True(BandCentroid(lower) < baseline - 100);
    }
    [Fact]
    public void PreservationReducesEnvelopeDriftWhilePitchStillChanges()
    {
        var raw = Vowel();
        var basic = new EffectSettings { PitchEnabled = true, PitchSemitones = 4 };
        var legacy = Process(raw, basic); var preserve = Process(raw, basic with { FormantEnabled = true });
        Assert.InRange(Pitch(preserve), 159, 164);
        double target = BandCentroid(raw);
        Assert.True(Math.Abs(BandCentroid(preserve) - target) < .6 * Math.Abs(BandCentroid(legacy) - target));
    }
    [Fact]
    public void FormantsRemainFiniteAndAllocationFreeWithWobbleAndExtremeInputs()
    {
        var dsp = new VoiceProcessor();
        var s = new EffectSettings { FormantEnabled = true, FormantSemitones = 6, PitchEnabled = true,
            PitchSemitones = 12, WobbleEnabled = true, WobbleMinSemitones = -12, WobbleMaxSemitones = 12 };
        for (int i = 0; i < 96000; i++)
        {
            float value = dsp.Process(i % 500 == 0 ? float.NaN : .1f * MathF.Sin(i * .02f), s);
            Assert.True(float.IsFinite(value));
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4800; i++) dsp.Process(.05f, s);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        for (int i = 0; i < 48000; i++) Assert.True(float.IsFinite(dsp.Process(0, s with { FormantEnabled = false })));
    }
    [Fact]
    public void SettingsDefaultOffClampAndPersistAndRemoteEditsPreserveThem()
    {
        var old = JsonSerializer.Deserialize<EffectSettings>("{\"PitchEnabled\":true}")!;
        Assert.False(old.FormantEnabled);
        var preset = new EffectPreset { Effects = old with { FormantEnabled = true, FormantSemitones = 2.5 } };
        Assert.Equal(preset, JsonSerializer.Deserialize<EffectPreset>(JsonSerializer.Serialize(preset)));
        Assert.Equal(6, (old with { FormantSemitones = 30 }).Validated().FormantSemitones);
        Assert.Equal(0, (old with { FormantSemitones = double.NaN }).Validated().FormantSemitones);
        var patch = new EffectPatch(true, new() { ["semitones"] = 4 });
        var updated = patch.Apply("pitch", preset.Effects);
        Assert.True(updated.FormantEnabled); Assert.Equal(2.5, updated.FormantSemitones);
    }
    [Fact]
    public void GridIsBoundedDistinctAndKeepsUnrelatedEffects()
    {
        var grid = CalibrationExplorer.Around(new() { PitchSemitones = 4.4, FormantSemitones = 1.5, EchoEnabled = true });
        Assert.Equal(9, grid.Length);
        Assert.Equal(4.4, grid[0].PitchSemitones); Assert.Equal(1.5, grid[0].FormantSemitones);
        Assert.All(grid, x => Assert.True(x.PitchEnabled && x.FormantEnabled && x.EchoEnabled));
        var edge = CalibrationExplorer.Around(new() { PitchSemitones = 12, FormantSemitones = 6 });
        Assert.Equal(4, edge.Length); Assert.Equal(edge.Length, edge.Distinct().Count());
        Assert.All(edge, x => { Assert.InRange(x.PitchSemitones, -12, 12); Assert.InRange(x.FormantSemitones, -6, 6); });
    }
    [Fact]
    public void GridUsesSameSourceAndCursorAndCanBeCancelled()
    {
        var raw = new float[4 * 48000];
        for (int i = 3 * 48000; i < raw.Length; i++) raw[i] = .1f * MathF.Sin(i * .02f);
        var section = new CalibrationSection("short", 3, 1, "");
        var center = new EffectSettings { PitchSemitones = 3, FormantSemitones = 1.5 };
        var grid = CalibrationExplorer.Render(raw, new(), center, null, section, true);
        var selected = CalibrationRenderer.Render(raw, new(), grid.Choices[4].Effects, null, section, true);
        Assert.Equal(selected.B, grid.Audio.Candidates![4]);
        Assert.All(grid.Audio.Candidates!, x => Assert.Equal(grid.Audio.Frames, x.Length));
        var player = new ComparisonPlayback(grid.Audio, 2); var buffer = new float[960];
        player.ReadStereo(buffer); player.Select(6); player.ReadStereo(buffer);
        Assert.Equal(960, player.Position);
        player.Mute(); player.ReadStereo(buffer); Assert.All(buffer, x => Assert.Equal(0, x));
        Assert.Throws<ArgumentOutOfRangeException>(() => player.Select(11));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => CalibrationExplorer.Render(raw, new(), center, null, section, true, stop.Token));
    }
}
