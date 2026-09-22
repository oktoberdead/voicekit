using VoiceKit.Core;
using VoiceKit.Core.Calibration;
using Xunit;

namespace VoiceKit.Core.Tests;

public class CalibrationTests
{
    private static float[] Tone(double hz, int seconds = 7, double amplitude = .15)
    {
        var samples = new float[seconds * 48000];
        for (int i = 3 * 48000; i < samples.Length; i++) samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / 48000));
        return samples;
    }
    [Fact]
    public void ScriptHasContiguousStagesAndNoNoiseInPreviewExcerpts()
    {
        int end = 0;
        foreach (var stage in CalibrationScript.Stages) { Assert.Equal(end, stage.StartSeconds); end = stage.EndSeconds; }
        Assert.Equal(38, end);
        Assert.Equal("Тишина", CalibrationScript.StageAt(2.99).Name);
        Assert.Equal("Гласная А", CalibrationScript.StageAt(3).Name);
        Assert.All(CalibrationScript.Excerpts, x => Assert.True(x.StartSeconds >= 3 && x.EndSeconds <= 38));
    }
    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(220)]
    [InlineData(440)]
    public void DetectsKnownF0WithoutChangingOriginalRecording(double hz)
    {
        var samples = Tone(hz); var copy = samples.ToArray();
        var report = VoiceAnalyzer.Analyze(samples);
        Assert.True(report.CanSuggestPitch);
        Assert.InRange(report.MedianF0Hz!.Value, hz * .985, hz * 1.015);
        Assert.InRange(report.VoicedFraction, .95, 1);
        Assert.True(report.Periodicity > .9);
        Assert.Equal(0, report.ClippedSamples);
        Assert.Equal(copy, samples);
    }
    [Fact]
    public void HarmonicsDoNotAutomaticallyBecomeTheFundamental()
    {
        var samples = Tone(110, 7, .04);
        for (int i = 3 * 48000; i < samples.Length; i++)
            samples[i] += (float)(.08 * Math.Sin(2 * Math.PI * 220 * i / 48000) + .04 * Math.Sin(2 * Math.PI * 330 * i / 48000));
        var report = VoiceAnalyzer.Analyze(samples);
        Assert.InRange(report.MedianF0Hz!.Value, 108, 112);
    }
    [Fact]
    public void SilenceNoiseAndTooQuietSpeechDoNotProduceARecommendation()
    {
        var silent = VoiceAnalyzer.Analyze(new float[7 * 48000]);
        Assert.Null(silent.MedianF0Hz); Assert.False(silent.CanSuggestPitch); Assert.Equal(-120, silent.PeakDbfs);
        var random = new Random(42);
        var noise = new float[7 * 48000];
        for (int i = 3 * 48000; i < noise.Length; i++) noise[i] = (float)((random.NextDouble() * 2 - 1) * .1);
        var noisy = VoiceAnalyzer.Analyze(noise);
        Assert.False(noisy.CanSuggestPitch);
        Assert.False(VoiceAnalyzer.Analyze(Tone(150, 7, .0001)).CanSuggestPitch);
        Assert.Throws<InvalidOperationException>(() => VoiceAnalyzer.Suggest(silent, 180));
    }
    [Fact]
    public void MeasuredBackgroundGatesWeakSpeechAndClippingDisablesSuggestion()
    {
        var noisy = Tone(160, 7, .02);
        var random = new Random(43);
        for (int i = 0; i < 3 * 48000; i++) noisy[i] = (float)((random.NextDouble() * 2 - 1) * .1);
        Assert.False(VoiceAnalyzer.Analyze(noisy).CanSuggestPitch);
        var clipped = Tone(160);
        for (int i = 3 * 48000; i < clipped.Length; i++) clipped[i] = MathF.CopySign(1, clipped[i]);
        var report = VoiceAnalyzer.Analyze(clipped);
        Assert.True(report.ClippedSamples > 1000); Assert.False(report.CanSuggestPitch);
    }
    [Fact]
    public void SuggestionIsAnExplicitBoundedRelativeShiftNotPitchFlattening()
    {
        var report = VoiceAnalyzer.Analyze(Tone(120));
        Assert.InRange(VoiceAnalyzer.Suggest(report, 180).Semitones, 6.9, 7.1);
        var high = VoiceAnalyzer.Suggest(report, 350);
        Assert.True(high.Limited); Assert.Equal(12, high.Semitones);
        Assert.Throws<ArgumentOutOfRangeException>(() => VoiceAnalyzer.Suggest(report, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => VoiceAnalyzer.Suggest(report, 800));
    }
    [Fact]
    public void AnalysisChecksSizeAndCancellation()
    {
        Assert.Throws<ArgumentException>(() => VoiceAnalyzer.Analyze(new float[48000]));
        Assert.Throws<ArgumentException>(() => VoiceAnalyzer.Analyze(new float[CalibrationScript.MaxSamples + 1]));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => VoiceAnalyzer.Analyze(Tone(150), stop.Token));
    }
    [Fact]
    public void BadSamplesInvalidateRecommendationButDoNotProduceNaNMetrics()
    {
        var raw = Tone(150); raw[48000 * 4] = float.NaN;
        var result = VoiceAnalyzer.Analyze(raw);
        Assert.False(result.CanSuggestPitch); Assert.True(double.IsFinite(result.SpeechRmsDbfs));
        Assert.Contains(result.Warnings, x => x.Contains("Некорректные"));
    }
    [Fact]
    public void ComparisonIsRepeatableUsesSameExcerptAndLeavesSourceIntact()
    {
        var raw = Tone(180, 5); var copy = raw.ToArray();
        var excerpt = new CalibrationSection("test", 3, 2, "");
        var result = CalibrationRenderer.Render(raw, new(), new(), null, excerpt, true);
        Assert.Equal(96000, result.Frames); Assert.Equal(result.A, result.B); Assert.Equal(result.Dry, result.A);
        Assert.Equal(copy, raw);
        double rms = Math.Sqrt(result.A.Sum(x => x * (double)x) / result.Frames);
        Assert.InRange(rms, .099, .101);
        Assert.True(result.A.All(x => float.IsFinite(x) && Math.Abs(x) <= .901));
    }
    [Fact]
    public void EffectStateIsWarmedFromRecordingStartAndFreshForEachRender()
    {
        var raw = Tone(180, 5);
        var effect = new EffectSettings { EchoEnabled = true, EchoMs = 180, EchoFeedback = .2, EchoMix = .4 };
        var section = new CalibrationSection("tail", 4, 1, "");
        var first = CalibrationRenderer.Render(raw, effect, effect, null, section, false);
        var again = CalibrationRenderer.Render(raw, effect, effect, null, section, false);
        Assert.Equal(first.A, first.B); Assert.Equal(first.A, again.A);
        var expected = new VoiceProcessor();
        for (int i = 0; i < 4 * 48000 + 480; i++)
        {
            float output = expected.Process(raw[i], effect);
            if (i >= 4 * 48000) Assert.Equal(output, first.A[i - 4 * 48000]);
        }
    }
    [Fact]
    public void CandidatePitchActuallyChangesFrequencyOnTheSameRecording()
    {
        var result = CalibrationRenderer.Render(Tone(220, 5), new(), new() { PitchEnabled = true, PitchSemitones = 12 }, null,
            new("test", 3, 2, ""), false);
        static int Crossings(float[] data) => Enumerable.Range(1, data.Length - 1).Count(i => data[i - 1] <= 0 && data[i] > 0);
        Assert.InRange(Crossings(result.A), 430, 450);
        Assert.InRange(Crossings(result.B), 860, 900);
    }
    [Fact]
    public void SilenceIsNotBoostedAndMissingCarrierIsNotSilentlySubstituted()
    {
        var silence = new float[4 * 48000]; var excerpt = new CalibrationSection("test", 3, 1, "");
        var result = CalibrationRenderer.Render(silence, new(), new(), null, excerpt, true);
        Assert.Equal(1, result.GainA); Assert.All(result.A, x => Assert.Equal(0, x));
        Assert.Throws<InvalidOperationException>(() => CalibrationRenderer.Render(silence,
            new() { VocoderEnabled = true, Carrier = CarrierKind.File }, new(), null, excerpt, true));
        Assert.Throws<ArgumentException>(() => CalibrationRenderer.Render(silence, new(), new(), null,
            CalibrationScript.Excerpts[0], true));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => CalibrationRenderer.Render(silence, new(), new(), null, excerpt, true, stop.Token));
    }
    [Fact]
    public void PlaybackCrossfadesAtSharedCursorAndMuteIsImmediate()
    {
        var audio = new ComparisonAudio(new float[6000], Enumerable.Repeat(.3f, 6000).ToArray(), Enumerable.Repeat(-.3f, 6000).ToArray(), 1, 1, 1);
        var player = new ComparisonPlayback(audio, 1); player.SetVolume(1);
        var buffer = new float[4000];
        Assert.Equal(4000, player.ReadStereo(buffer)); Assert.Equal(2000, player.Position);
        float previous = buffer[^1];
        player.Select(2); player.ReadStereo(buffer);
        Assert.Equal(4000, player.Position);
        Assert.InRange(Math.Abs(buffer[0] - previous), 0, .01);
        Assert.True(buffer[^1] < -.28f);
        player.Mute(); player.ReadStereo(buffer);
        Assert.All(buffer, x => Assert.Equal(0, x));
        Assert.Equal(0, player.ReadStereo(buffer));
        Assert.Throws<ArgumentOutOfRangeException>(() => player.Select(3));
    }
    [Fact]
    public void PlaybackDoesNotAllocateOnItsAudioThread()
    {
        var array = new float[48000];
        var playback = new ComparisonPlayback(new(array, array, array, 1, 1, 1), 0);
        var output = new float[960]; playback.ReadStereo(output);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++) playback.ReadStereo(output);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
