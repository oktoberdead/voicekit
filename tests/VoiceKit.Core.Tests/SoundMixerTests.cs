using VoiceKit.Core;
using Xunit;

namespace VoiceKit.Core.Tests;

public class SoundMixerTests
{
    private static AudioClip Clip(int length = 4800) => new(Guid.NewGuid(), Enumerable.Repeat(.25f, length).ToArray());
    private static float Advance(SoundMixer mixer, int count)
    {
        float last = 0;
        for (int i = 0; i < count; i++) last = mixer.Next();
        return last;
    }
    [Fact]
    public void OneShotEndsAndLoopKeepsPlaying()
    {
        var c = Clip(); var mixer = new SoundMixer();
        mixer.Enqueue(new(c, c.Id, new(), true)); mixer.BeginBlock();
        Assert.Equal(0, Advance(mixer, 6000));
        mixer.Enqueue(new(c, c.Id, new(Loop: true), true)); mixer.BeginBlock();
        Assert.True(Advance(mixer, 10000) > 0);
    }
    [Fact]
    public void HeldLoopStopsOnRelease()
    {
        var c = Clip(); var mixer = new SoundMixer();
        var behavior = new SoundBehavior(TriggerMode.Hold, true);
        mixer.Enqueue(new(c, c.Id, behavior, true)); mixer.BeginBlock();
        Assert.True(Advance(mixer, 1000) > 0);
        mixer.Enqueue(new(c, c.Id, behavior, false)); mixer.BeginBlock();
        Assert.Equal(0, Advance(mixer, 300));
    }
    [Fact]
    public void ToggleSecondPressStopsInsteadOfOverlapping()
    {
        var c = Clip(); var mixer = new SoundMixer();
        var behavior = new SoundBehavior(TriggerMode.Toggle, true, RetriggerMode.Overlap);
        mixer.Enqueue(new(c, c.Id, behavior, true)); mixer.BeginBlock(); Advance(mixer, 1000);
        mixer.Enqueue(new(c, c.Id, behavior, true)); mixer.BeginBlock();
        Assert.Equal(0, Advance(mixer, 300));
    }
    [Theory]
    [InlineData(RetriggerMode.Ignore, .25f)]
    [InlineData(RetriggerMode.Restart, .25f)]
    [InlineData(RetriggerMode.Overlap, .5f)]
    public void RetriggerPolicyControlsSimultaneousVoices(RetriggerMode mode, float expected)
    {
        var c = Clip(); var mixer = new SoundMixer();
        var behavior = new SoundBehavior(Retrigger: mode);
        mixer.Enqueue(new(c, c.Id, behavior, true)); mixer.BeginBlock(); Advance(mixer, 500);
        mixer.Enqueue(new(c, c.Id, behavior, true)); mixer.BeginBlock();
        Assert.Equal(expected, Advance(mixer, 500), 4);
    }
    [Fact]
    public void StopAllDiscardsQueuedStartsAndFadesActiveVoices()
    {
        var c = Clip(); var mixer = new SoundMixer();
        mixer.Enqueue(new(c, c.Id, new(Loop: true), true)); mixer.BeginBlock(); Advance(mixer, 500);
        mixer.Enqueue(new(c, c.Id, new(), true)); mixer.StopAll(); mixer.BeginBlock();
        Assert.Equal(0, Advance(mixer, 300));
    }
    [Fact]
    public void LostKeyUpDueToFullQueueTriggersFailSafeStop()
    {
        var c = Clip(); var mixer = new SoundMixer();
        var behavior = new SoundBehavior(TriggerMode.Hold, true);
        mixer.Enqueue(new(c, c.Id, behavior, true)); mixer.BeginBlock(); Advance(mixer, 500);
        for (int i = 0; i < 128; i++) Assert.True(mixer.Enqueue(new(c, c.Id, behavior, true)));
        Assert.False(mixer.Enqueue(new(c, c.Id, behavior, false)));
        mixer.BeginBlock();
        Assert.Equal(0, Advance(mixer, 500));
    }
    [Fact]
    public void VoiceLimitIsBounded()
    {
        var c = Clip(); var mixer = new SoundMixer();
        for (int i = 0; i < 40; i++) mixer.Enqueue(new(c, c.Id, new(Retrigger: RetriggerMode.Overlap), true));
        mixer.BeginBlock(); Assert.Equal(8, mixer.RejectedCommands);
    }
}
