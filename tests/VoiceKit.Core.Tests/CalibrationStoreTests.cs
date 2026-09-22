using System.Text.Json;
using VoiceKit.Core.Calibration;
using VoiceKit.Windows.Calibration;
using Xunit;

namespace VoiceKit.Core.Tests;

public class CalibrationStoreTests
{
    [Fact]
    public void ExplicitSaveAndLoadRoundTripRawAudioAndEffectsWithoutOverwriting()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoiceKit-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CalibrationStore(root);
            Assert.False(Directory.Exists(store.Root)); // merely creating the service must not persist a voice
            var samples = new float[4 * 48000];
            for (int i = 3 * 48000; i < samples.Length; i++) samples[i] = .1f * MathF.Sin(i * .03f);
            var profile = new CalibrationProfile { Name = "Тест", Candidate = new() { PitchEnabled = true, PitchSemitones = 3.5 } };
            string first = store.Save(profile, samples, CancellationToken.None);
            string second = store.Save(profile, samples, CancellationToken.None);
            Assert.NotEqual(first, second);
            var loaded = store.Load(first, CancellationToken.None);
            Assert.Equal(samples, loaded.Raw); Assert.Equal(profile.Name, loaded.Profile.Name);
            Assert.Equal(profile.Candidate, loaded.Profile.Candidate);
            Assert.Equal(2, Directory.GetDirectories(store.Root).Length);
            Assert.DoesNotContain(Directory.GetDirectories(store.Root), x => Path.GetFileName(x).StartsWith(".pending-"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void InvalidAudioIsNotSavedOrAcceptedOnLoad()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoiceKit-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CalibrationStore(root); var raw = new float[4 * 48000];
            raw[123] = float.NaN;
            Assert.Throws<ArgumentException>(() => store.Save(new(), raw, CancellationToken.None));
            Assert.False(Directory.Exists(store.Root));
            raw[123] = 0;
            string path = store.Save(new(), raw, CancellationToken.None);
            raw[123] = float.NaN;
            using (var writer = new NAudio.Wave.WaveFileWriter(Path.Combine(Path.GetDirectoryName(path)!, "sample.wav"),
                NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 1))) writer.WriteSamples(raw, 0, raw.Length);
            Assert.Throws<InvalidDataException>(() => store.Load(path, CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void CancelledSaveDoesNotLeaveAProfileAndLoadChecksSchemaAndMissingSample()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoiceKit-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CalibrationStore(root); var samples = new float[4 * 48000];
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => store.Save(new(), samples, cancelled.Token));
            Assert.Empty(Directory.GetDirectories(store.Root));
            string path = store.Save(new(), samples, CancellationToken.None);
            File.WriteAllText(path, JsonSerializer.Serialize(new CalibrationProfile { SchemaVersion = 999 }));
            Assert.Throws<InvalidDataException>(() => store.Load(path, CancellationToken.None));
            File.WriteAllText(path, JsonSerializer.Serialize(new CalibrationProfile()));
            File.Delete(Path.Combine(Path.GetDirectoryName(path)!, "sample.wav"));
            Assert.Throws<FileNotFoundException>(() => store.Load(path, CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
