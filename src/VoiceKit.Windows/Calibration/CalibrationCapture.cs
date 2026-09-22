using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoiceKit.Core.Calibration;

namespace VoiceKit.Windows.Calibration;

/// <summary>Separate opt-in capture session. No render endpoint, DSP, file IO or allocation in DataAvailable.</summary>
public sealed class CalibrationCapture : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;
    private WasapiCapture? _capture;
    private float[] _samples = [];
    private int _count, _rate, _channels, _bits, _stride, _disposed, _aborted;
    private bool _floating;
    private float _peak;
    private string? _error;
    public double Seconds => (double)Volatile.Read(ref _count) / Math.Max(1, _rate);
    public float Peak => Volatile.Read(ref _peak);
    public bool Complete => _samples.Length > 0 && Volatile.Read(ref _count) == _samples.Length;
    public string? Error => Volatile.Read(ref _error);
    public void Start(string captureId)
    {
        if (_capture is not null || Volatile.Read(ref _disposed) != 0) throw new InvalidOperationException("Запись уже использована.");
        try
        {
            _device = _enumerator.GetDevice(captureId);
            if (_device.DataFlow != DataFlow.Capture || _device.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Для образца выбери физический микрофон, не VB-CABLE.");
            _capture = new WasapiCapture(_device, true, 30);
            var format = _capture.WaveFormat.AsStandardWaveFormat();
            _rate = format.SampleRate; _channels = format.Channels; _bits = format.BitsPerSample; _stride = format.BlockAlign;
            _floating = format.Encoding == WaveFormatEncoding.IeeeFloat && _bits == 32;
            if ((!_floating && (format.Encoding != WaveFormatEncoding.Pcm || _bits is not (16 or 24 or 32))) ||
                _rate is < 8000 or > 192000 || _channels is < 1 or > 8 || _stride < _channels * (_bits / 8))
                throw new NotSupportedException($"Формат записи не поддержан лабораторией: {format}");
            _samples = new float[_rate * CalibrationScript.DurationSeconds];
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += (_, e) =>
            {
                if (Volatile.Read(ref _disposed) == 0)
                    Volatile.Write(ref _error, e.Exception?.Message ?? "Устройство записи остановилось.");
            };
            _capture.StartRecording();
        }
        catch { Dispose(); throw; }
    }
    // Safe from the system power-event thread; device disposal stays on the UI thread.
    public void Abort() => Volatile.Write(ref _aborted, 1);
    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _aborted) != 0) return;
        int count = _count, bytes = _bits / 8;
        float peak = 0;
        for (int offset = 0; offset + _stride <= e.BytesRecorded && count < _samples.Length; offset += _stride)
        {
            float sample = 0;
            for (int ch = 0; ch < _channels; ch++)
            {
                int p = offset + ch * bytes;
                float x;
                if (_floating) x = BitConverter.ToSingle(e.Buffer, p);
                else if (_bits == 16) x = BitConverter.ToInt16(e.Buffer, p) / 32768f;
                else if (_bits == 24)
                {
                    int n = e.Buffer[p] | (e.Buffer[p + 1] << 8) | (e.Buffer[p + 2] << 16);
                    x = ((n << 8) >> 8) / 8388608f;
                }
                else x = BitConverter.ToInt32(e.Buffer, p) / 2147483648f;
                if (!float.IsFinite(x)) Volatile.Write(ref _error, "Устройство прислало некорректные сэмплы. Запись прервана.");
                sample += (float.IsFinite(x) ? Math.Clamp(x, -8, 8) : 0) / _channels;
            }
            _samples[count++] = sample;
            peak = Math.Max(peak, Math.Abs(sample));
        }
        Volatile.Write(ref _peak, peak);
        Volatile.Write(ref _count, count);
    }
    // Call after Dispose has joined capture, then convert/analyze on a worker thread.
    public float[] To48Khz(CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) == 0) throw new InvalidOperationException("Сначала останови запись.");
        int count = _count;
        if (count < _rate * 4) throw new InvalidOperationException("Слишком короткая запись. Нужны 3 секунды фона и хотя бы 1 секунда речи.");
        if (_rate == CalibrationScript.SampleRate) return _samples.AsSpan(0, count).ToArray();
        var source = new CapturedSamples(_samples, count, _rate);
        var resampler = new WdlResamplingSampleProvider(source, CalibrationScript.SampleRate);
        var result = new float[Math.Min(CalibrationScript.MaxSamples, (int)((long)count * CalibrationScript.SampleRate / _rate))];
        int total = 0;
        while (total < result.Length)
        {
            ct.ThrowIfCancellationRequested();
            int read = resampler.Read(result, total, Math.Min(8192, result.Length - total));
            if (read == 0) break;
            total += read;
        }
        return total == result.Length ? result : result.AsSpan(0, total).ToArray();
    }
    private sealed class CapturedSamples(float[] data, int length, int rate) : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);
        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, length - _position);
            Array.Copy(data, _position, buffer, offset, n); _position += n; return n;
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _capture?.StopRecording(); }
        finally
        {
            try { _capture?.Dispose(); }
            finally { _device?.Dispose(); _enumerator.Dispose(); }
        }
    }
}
