using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoiceKit.Core.Calibration;

namespace VoiceKit.Windows.Calibration;

/// <summary>A single headphones-only stream. Cannot select capture or the main transmission endpoint.</summary>
public sealed class CalibrationPlayer : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;
    private WasapiOut? _output;
    private int _disposed;
    public ComparisonPlayback Playback { get; }
    public event Action<string?>? Stopped;
    public CalibrationPlayer(ComparisonAudio audio, int variant) => Playback = new(audio, variant);
    public void Start(string monitorId, string? forbiddenOutputId, float volume)
    {
        if (_output is not null || Volatile.Read(ref _disposed) != 0) throw new InvalidOperationException("Плеер уже запущен.");
        try
        {
            if (string.IsNullOrWhiteSpace(monitorId) || monitorId == forbiddenOutputId)
                throw new InvalidOperationException("Выбери отдельный выход на наушники, не выход передачи.");
            _device = _enumerator.GetDevice(monitorId);
            if (_device.DataFlow != DataFlow.Render || _device.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Прослушивание лаборатории в VB-CABLE запрещено. Выбери наушники.");
            _output = new WasapiOut(_device, AudioClientShareMode.Shared, true, 30);
            Playback.SetVolume(volume);
            _output.Init(new Provider(Playback).ToWaveProvider());
            _output.PlaybackStopped += (_, e) =>
            {
                if (Volatile.Read(ref _disposed) == 0) Stopped?.Invoke(e.Exception?.Message);
            };
            _output.Play();
        }
        catch { Dispose(); throw; }
    }
    private sealed class Provider(ComparisonPlayback playback) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(CalibrationScript.SampleRate, 2);
        public int Read(float[] buffer, int offset, int count) => playback.ReadStereo(buffer.AsSpan(offset, count));
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Playback.Mute();
        try { _output?.Stop(); }
        finally
        {
            try { _output?.Dispose(); }
            finally { _device?.Dispose(); _enumerator.Dispose(); }
        }
    }
}
