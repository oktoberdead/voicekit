using VoiceKit.Core;
using Xunit;

namespace VoiceKit.Core.Tests;

public class AudioBufferTests
{
    [Fact]
    public void FullRingDropsNewSamplesWithoutOverwritingUnreadData()
    {
        var ring = new SpscAudioBuffer(4);
        for (int i = 0; i < 4; i++) Assert.True(ring.Write(i));
        Assert.False(ring.Write(99));
        Assert.Equal(4, ring.Count);
        Assert.Equal(1, ring.DroppedSamples);
        for (int i = 0; i < 4; i++) Assert.Equal(i, ring.Peek(i));
        ring.Advance(2);
        Assert.True(ring.Write(4)); Assert.True(ring.Write(5));
        for (int i = 0; i < 4; i++) Assert.Equal(i + 2, ring.Peek(i));
    }
    [Fact]
    public void NonFiniteInputIsSanitized()
    {
        var ring = new SpscAudioBuffer(8);
        ring.Write(float.NaN); ring.Write(float.PositiveInfinity);
        Assert.Equal(0, ring.Peek(0)); Assert.Equal(0, ring.Peek(1));
    }
    [Fact]
    public void ExcessLatencyIsDiscardedByConsumer()
    {
        var ring = new SpscAudioBuffer(48000);
        var reader = new AdaptiveReader(ring, 48000, 48000, 480);
        for (int i = 0; i < 24000; i++) ring.Write(.25f);
        Assert.True(float.IsFinite(reader.Read()));
        Assert.InRange(ring.Count, 470, 480);
        Assert.Equal(1, reader.Resyncs);
    }
    [Fact]
    public void OverflowDiscardsStaleAudioInsteadOfPlayingOldSamples()
    {
        var ring = new SpscAudioBuffer(4800);
        var reader = new AdaptiveReader(ring, 48000, 48000, 480);
        for (int i = 0; i < 6000; i++) ring.Write(.8f);
        Assert.Equal(0, reader.Read());
        Assert.Equal(0, ring.Count);
        Assert.Equal(1, reader.Resyncs);
        for (int i = 0; i < 960; i++) ring.Write(.1f);
        float sample = 0;
        for (int i = 0; i < 400; i++) sample = reader.Read();
        Assert.InRange(sample, .099f, .101f);
    }
    [Fact]
    public void StarvationFadesToSilenceAndCanRecover()
    {
        var ring = new SpscAudioBuffer(48000);
        var reader = new AdaptiveReader(ring, 48000, 48000, 480);
        for (int i = 0; i < 960; i++) ring.Write(.5f);
        float last = 0;
        for (int i = 0; i < 4000; i++) last = reader.Read();
        Assert.True(reader.Underruns > 0);
        Assert.InRange(Math.Abs(last), 0, .00001);
        for (int i = 0; i < 960; i++) ring.Write(.5f);
        for (int i = 0; i < 400; i++) last = reader.Read();
        Assert.InRange(last, .49f, .51f);
    }
    [Theory]
    [InlineData(.9995)]
    [InlineData(1.0005)]
    public void ClockDriftDoesNotAccumulateLatencyOverSimulatedTwoMinutes(double speed)
    {
        var ring = new SpscAudioBuffer(48000);
        var reader = new AdaptiveReader(ring, 48000, 48000, 1440);
        for (int i = 0; i < 1440; i++) ring.Write(.1f);
        double fractional = 0;
        int min = int.MaxValue, max = 0;
        for (int block = 0; block < 12000; block++)
        {
            fractional += 480 * speed;
            int input = (int)fractional;
            fractional -= input;
            for (int i = 0; i < input; i++) ring.Write(.1f);
            for (int i = 0; i < 480; i++) reader.Read();
            if (block > 6000) { min = Math.Min(min, ring.Count); max = Math.Max(max, ring.Count); }
        }
        Assert.Equal(0, ring.DroppedSamples);
        Assert.Equal(0, reader.Resyncs);
        Assert.Equal(0, reader.Underruns);
        Assert.InRange(max - min, 0, 200);
        Assert.InRange(ring.Count, 400, 3000);
    }
    [Fact]
    public void CommandQueueIsBoundedAndMaintainsOrder()
    {
        var queue = new SpscQueue<int>(2);
        Assert.True(queue.TryEnqueue(1)); Assert.True(queue.TryEnqueue(2));
        Assert.False(queue.TryEnqueue(3));
        Assert.True(queue.TryDequeue(out int a)); Assert.Equal(1, a);
        Assert.True(queue.TryEnqueue(3));
        Assert.True(queue.TryDequeue(out int b)); Assert.Equal(2, b);
        Assert.True(queue.TryDequeue(out int c)); Assert.Equal(3, c);
        Assert.False(queue.TryDequeue(out _));
    }
    [Fact]
    public async Task RingSurvivesConcurrentProducerAndConsumer()
    {
        var ring = new SpscAudioBuffer(1024);
        const int samples = 100000;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var producer = Task.Run(() =>
        {
            for (int i = 0; i < samples; i++)
                while (!ring.Write(i)) { timeout.Token.ThrowIfCancellationRequested(); Thread.Yield(); }
        });
        var consumer = Task.Run(() =>
        {
            for (int i = 0; i < samples; i++)
            {
                while (ring.Count == 0) { timeout.Token.ThrowIfCancellationRequested(); Thread.Yield(); }
                Assert.Equal((float)i, ring.Peek(0));
                ring.Advance(1);
            }
        });
        await Task.WhenAll(producer, consumer);
        Assert.Equal(0, ring.Count);
    }
}
