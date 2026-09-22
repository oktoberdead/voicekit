using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoiceKit.Core;

namespace VoiceKit.Windows.Audio;

public static class ClipLoader
{
    public const int MaxSeconds = 300;
    public const long MaxCacheBytes = 384L * 1024 * 1024;
    // This is called only on a worker task. No IO or decoding in audio callbacks.
    public static AudioClip Load(string path, Guid id)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider source = reader;
        if (source.WaveFormat.Channels == 2) source = new StereoToMonoSampleProvider(source);
        if (source.WaveFormat.Channels != 1) throw new NotSupportedException("Поддержаны моно и стерео файлы.");
        if (source.WaveFormat.SampleRate != SoundMixer.SampleRate)
            source = new WdlResamplingSampleProvider(source, SoundMixer.SampleRate);
        var samples = new List<float>();
        var buffer = new float[8192];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (samples.Count + read > MaxSeconds * SoundMixer.SampleRate)
                throw new InvalidOperationException($"Файл длиннее {MaxSeconds / 60} минут. Обрежь его перед импортом.");
            for (int i = 0; i < read; i++) samples.Add(float.IsFinite(buffer[i]) ? Math.Clamp(buffer[i], -8, 8) : 0);
        }
        if (samples.Count == 0) throw new InvalidOperationException("Звуковой файл пуст.");
        return new(id, samples.ToArray());
    }
}
