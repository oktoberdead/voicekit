using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoiceKit.Core;

namespace VoiceKit.Windows.Audio;

public sealed record AudioDevice(string Id, string Name)
{
    public override string ToString() => Name;
}
public sealed record AudioDiagnostics(double CaptureQueuedMs, double MonitorQueuedMs, long CaptureDrops,
    long MonitorDrops, long Underruns, long Resyncs, double CapturePpm, double MonitorPpm,
    double ProcessingMs, double BlockMs, float InputPeak, float OutputPeak, long RejectedSounds);

/// <summary>The cable's render callback owns the DSP graph. Capture/monitor never run DSP twice.</summary>
public sealed class AudioEngine : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _captureDevice, _outputDevice, _monitorDevice;
    private WasapiCapture? _capture;
    private WasapiOut? _output, _monitor;
    private SpscAudioBuffer? _captureBuffer, _monitorBuffer;
    private AdaptiveReader? _captureReader, _monitorReader;
    private int _captureRate;
    private bool _floating;
    private int _bits, _channels, _stride;
    private int _disposing, _faulted;
    private double _processingMs, _blockMs;
    private float _inputPeak, _outputPeak;
    public VoiceGraph Graph { get; } = new();
    public event Action<string>? Faulted;

    public static IReadOnlyList<AudioDevice> Devices(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<AudioDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device) result.Add(new(device.ID, device.FriendlyName));
        }
        return result;
    }
    public void Start(AppSettings settings)
    {
        if (_capture is not null) throw new InvalidOperationException("Движок уже запущен.");
        if (string.IsNullOrWhiteSpace(settings.CaptureId) || string.IsNullOrWhiteSpace(settings.OutputId))
            throw new InvalidOperationException("Выбери микрофон и выход CABLE Input.");
        if (settings.OutputId == settings.MonitorId)
            throw new InvalidOperationException("Выход передачи и прослушивания должны быть разными устройствами.");
        try
        {
            int bufferMs = Math.Clamp(settings.BufferMs, 10, 100);
            Graph.Publish(settings.Audio);
            _captureDevice = _enumerator.GetDevice(settings.CaptureId);
            _outputDevice = _enumerator.GetDevice(settings.OutputId);
            if (_captureDevice.DataFlow != DataFlow.Capture || _outputDevice.DataFlow != DataFlow.Render)
                throw new InvalidOperationException("Неправильное направление аудиоустройства.");
            if (_captureDevice.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Вход VB-CABLE запрещён как микрофон: возможна обратная связь. Выбери физический микрофон.");
            _capture = new WasapiCapture(_captureDevice, true, bufferMs);
            var format = _capture.WaveFormat;
            _captureRate = format.SampleRate;
            _bits = format.BitsPerSample;
            _channels = format.Channels;
            _stride = format.BlockAlign;
            var encoding = format.Encoding;
            if (format is WaveFormatExtensible extensible)
            {
                if (extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")) encoding = WaveFormatEncoding.IeeeFloat;
                else if (extensible.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71")) encoding = WaveFormatEncoding.Pcm;
            }
            _floating = encoding == WaveFormatEncoding.IeeeFloat;
            if ((_floating && _bits != 32) || (!_floating && (encoding != WaveFormatEncoding.Pcm || _bits is not (16 or 24 or 32))))
                throw new NotSupportedException($"Неподдерживаемый формат микрофона: {format}");
            _captureBuffer = new(_captureRate);
            _captureReader = new(_captureBuffer, _captureRate, SoundMixer.SampleRate, _captureRate * bufferMs / 1000);
            if (!string.IsNullOrEmpty(settings.MonitorId))
            {
                _monitorDevice = _enumerator.GetDevice(settings.MonitorId);
                _monitorBuffer = new(SoundMixer.SampleRate);
                _monitorReader = new(_monitorBuffer, SoundMixer.SampleRate, SoundMixer.SampleRate, SoundMixer.SampleRate * bufferMs / 1000);
                _monitor = new WasapiOut(_monitorDevice, AudioClientShareMode.Shared, true, bufferMs);
                _monitor.Init(new MonitorProvider(this).ToWaveProvider());
                _monitor.PlaybackStopped += OnPlaybackStopped;
            }
            _output = new WasapiOut(_outputDevice, AudioClientShareMode.Shared, true, bufferMs);
            _output.Init(new MasterProvider(this).ToWaveProvider());
            _output.PlaybackStopped += OnPlaybackStopped;
            _capture.DataAvailable += OnCapture;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();
            _monitor?.Play();
            _output.Play();
        }
        catch { Dispose(); throw; }
    }
    private void OnCapture(object? sender, WaveInEventArgs e)
    {
        if (Volatile.Read(ref _disposing) != 0 || _captureBuffer is null) return;
        int bytes = _bits / 8;
        for (int offset = 0; offset + _stride <= e.BytesRecorded; offset += _stride)
        {
            float mono = 0;
            for (int ch = 0; ch < _channels; ch++)
            {
                int p = offset + ch * bytes;
                float sample;
                if (_floating) sample = BitConverter.ToSingle(e.Buffer, p);
                else if (_bits == 16) sample = BitConverter.ToInt16(e.Buffer, p) / 32768f;
                else if (_bits == 24)
                {
                    int n = e.Buffer[p] | (e.Buffer[p + 1] << 8) | (e.Buffer[p + 2] << 16);
                    sample = ((n << 8) >> 8) / 8388608f;
                }
                else sample = BitConverter.ToInt32(e.Buffer, p) / 2147483648f;
                mono += float.IsFinite(sample) ? Math.Clamp(sample, -8, 8) / _channels : 0;
            }
            _captureBuffer.Write(mono);
        }
    }
    private sealed class MasterProvider(AudioEngine owner) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SoundMixer.SampleRate, 2);
        public int Read(float[] buffer, int offset, int count)
        {
            long start = Stopwatch.GetTimestamp();
            owner.Graph.BeginBlock();
            float inputPeak = 0, outputPeak = 0;
            int end = offset + count;
            for (int i = offset; i + 1 < end; i += 2)
            {
                float input = owner._captureReader!.Read();
                owner.Graph.Process(input, out float output, out float monitor);
                inputPeak = Math.Max(inputPeak, Math.Abs(input));
                outputPeak = Math.Max(outputPeak, Math.Abs(output));
                buffer[i] = buffer[i + 1] = output;
                owner._monitorBuffer?.Write(monitor);
            }
            if ((count & 1) != 0) buffer[end - 1] = 0;
            Volatile.Write(ref owner._inputPeak, inputPeak);
            Volatile.Write(ref owner._outputPeak, outputPeak);
            Volatile.Write(ref owner._processingMs, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            Volatile.Write(ref owner._blockMs, count * 1000.0 / 2 / SoundMixer.SampleRate);
            return count;
        }
    }
    private sealed class MonitorProvider(AudioEngine owner) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SoundMixer.SampleRate, 2);
        public int Read(float[] buffer, int offset, int count)
        {
            int end = offset + count;
            for (int i = offset; i + 1 < end; i += 2)
            {
                float value = owner._monitorReader!.Read();
                buffer[i] = buffer[i + 1] = owner.Graph.IsPanicked ? 0 : value;
            }
            if ((count & 1) != 0) buffer[end - 1] = 0;
            return count;
        }
    }
    private void OnPlaybackStopped(object? sender, StoppedEventArgs e) => ReportFault(e.Exception?.Message ?? "Выходное устройство остановилось.");
    private void OnRecordingStopped(object? sender, StoppedEventArgs e) => ReportFault(e.Exception?.Message ?? "Микрофон остановился.");
    private void ReportFault(string message)
    {
        if (Volatile.Read(ref _disposing) == 0 && Interlocked.Exchange(ref _faulted, 1) == 0)
        {
            Graph.SetPanic(true);
            Faulted?.Invoke(message); // subscriber must marshal to UI; never dispose here
        }
    }
    public AudioDiagnostics Diagnostics => new(
        (_captureBuffer?.Count ?? 0) * 1000.0 / Math.Max(1, _captureRate),
        (_monitorBuffer?.Count ?? 0) * 1000.0 / SoundMixer.SampleRate,
        _captureBuffer?.DroppedSamples ?? 0, _monitorBuffer?.DroppedSamples ?? 0,
        (_captureReader?.Underruns ?? 0) + (_monitorReader?.Underruns ?? 0),
        (_captureReader?.Resyncs ?? 0) + (_monitorReader?.Resyncs ?? 0),
        _captureReader?.CorrectionPpm ?? 0, _monitorReader?.CorrectionPpm ?? 0,
        Volatile.Read(ref _processingMs), Volatile.Read(ref _blockMs),
        Volatile.Read(ref _inputPeak), Volatile.Read(ref _outputPeak), Graph.Sounds.RejectedCommands);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposing, 1) != 0) return;
        Graph.SetPanic(true);
        // Each resource is attempted independently, including after device removal.
        static void Release(Action dispose) { try { dispose(); } catch (Exception ex) { Debug.WriteLine(ex); } }
        if (_output is not null) { Release(_output.Stop); Release(_output.Dispose); }
        if (_monitor is not null) { Release(_monitor.Stop); Release(_monitor.Dispose); }
        if (_capture is not null) { Release(_capture.StopRecording); Release(_capture.Dispose); }
        if (_captureDevice is not null) Release(_captureDevice.Dispose);
        if (_outputDevice is not null) Release(_outputDevice.Dispose);
        if (_monitorDevice is not null) Release(_monitorDevice.Dispose);
        Release(_enumerator.Dispose);
    }
}
