using VoiceKit.Core;
using Xunit;

namespace VoiceKit.Core.Tests;

public class EffectControlTests
{
    [Fact]
    public void RemotePatchNeverChangesVocoderOrUnrelatedEffects()
    {
        var original = new EffectSettings { VocoderEnabled = true, Carrier = CarrierKind.File, CarrierGain = 2, EchoEnabled = true };
        var patch = new EffectPatch(true, new() { ["semitones"] = -3.5 });
        Assert.Equal(original with { PitchEnabled = true, PitchSemitones = -3.5 }, patch.Apply("pitch", original));
    }
    [Fact]
    public void PatchPreservesMasterBypassAndOtherParameters()
    {
        var original = new EffectSettings { Enabled = false, EchoMix = .6, EchoMs = 700 };
        var next = new EffectPatch(Parameters: new() { ["feedback"] = .4 }).Apply("echo", original);
        Assert.Equal(original with { EchoFeedback = .4 }, next);
    }
    [Theory]
    [InlineData("pitch", "semitones", 12.1)]
    [InlineData("robot", "hz", 0)]
    [InlineData("robot", "mix", -1)]
    [InlineData("echo", "feedback", .86)]
    [InlineData("echo", "delayMs", 2000)]
    [InlineData("echo", "mix", double.NaN)]
    [InlineData("reverb", "size", double.PositiveInfinity)]
    [InlineData("reverb", "carrierHz", 100)]
    public void InvalidParametersAreRejectedNotSilentlyClamped(string effect, string parameter, double value)
    {
        Assert.Throws<ArgumentException>(() => new EffectPatch(Parameters: new() { [parameter] = value }).Apply(effect, new()));
    }
    [Fact]
    public void UnsupportedEffectsAndEmptyPatchesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new EffectPatch(true).Apply("vocoder", new()));
        Assert.Throws<ArgumentException>(() => new EffectPatch().Apply("echo", new()));
    }
    [Fact]
    public void SnapshotContainsOnlyRemoteControlsAndSafetyState()
    {
        var settings = new AudioSettings { MicMuted = true, Effects = new() { Enabled = false, EchoEnabled = true } };
        var snapshot = RemoteControlState.From(settings, true, true);
        Assert.True(snapshot.Engine.Panic); Assert.True(snapshot.Engine.MicrophoneMuted);
        Assert.False(snapshot.Engine.ChainEnabled); Assert.True(snapshot.Effects.Echo.Enabled);
        Assert.Equal(0, snapshot.Revision);
    }
}
