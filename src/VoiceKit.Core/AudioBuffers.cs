namespace VoiceKit.Core;

/// <summary>One producer, one consumer. Full buffers drop NEW data; only the consumer advances read.</summary>
public sealed class SpscAudioBuffer
{
    private readonly float[] _data;
    private long _read, _write, _dropped;
    public SpscAudioBuffer(int capacity)
    {
        if (capacity < 4) throw new ArgumentOutOfRangeException(nameof(capacity));
        _data = new float[capacity];
    }
    public int Capacity => _data.Length;
    public int Count => (int)Math.Clamp(Volatile.Read(ref _write) - Volatile.Read(ref _read), 0, _data.Length);
    public long DroppedSamples => Interlocked.Read(ref _dropped);
    public bool Write(float sample)
    {
        long w = _write;
        if (w - Volatile.Read(ref _read) >= _data.Length)
        {
            Interlocked.Increment(ref _dropped);
            return false;
        }
        _data[(int)(w % _data.Length)] = float.IsFinite(sample) ? sample : 0;
        Volatile.Write(ref _write, w + 1);
        return true;
    }
    // Consumer-only methods; Peek requires offset < Count.
    public float Peek(int offset) => _data[(int)((_read + offset) % _data.Length)];
    public void Advance(int samples)
    {
        if (samples < 0 || samples > Count) throw new ArgumentOutOfRangeException(nameof(samples));
        Volatile.Write(ref _read, _read + samples);
    }
}

/// <summary>Bounded, fractional reader with slow occupancy feedback for independent device clocks.</summary>
public sealed class AdaptiveReader
{
    private readonly SpscAudioBuffer _buffer;
    private readonly double _baseRatio;
    private readonly int _target, _maxLag;
    private double _fraction, _fill, _correction;
    private bool _ready;
    private float _last;
    private int _fade;
    private long _underruns, _resyncs, _seenDrops;
    public AdaptiveReader(SpscAudioBuffer buffer, int inputRate, int outputRate, int targetSamples)
    {
        if (inputRate <= 0 || outputRate <= 0 || targetSamples < 2 || targetSamples * 4 >= buffer.Capacity)
            throw new ArgumentOutOfRangeException(nameof(targetSamples));
        _buffer = buffer;
        _baseRatio = (double)inputRate / outputRate;
        _target = targetSamples;
        _maxLag = targetSamples * 4;
        _fill = targetSamples;
    }
    public long Underruns => Interlocked.Read(ref _underruns);
    public long Resyncs => Interlocked.Read(ref _resyncs);
    public double CorrectionPpm => Volatile.Read(ref _correction) * 1_000_000;
    public float Read()
    {
        int count = _buffer.Count;
        long dropped = _buffer.DroppedSamples;
        if (dropped != _seenDrops)
        {
            // After a stalled consumer, a full ring contains old audio, not the latest packet.
            // Discard it entirely and prefill with fresh capture instead of replaying history.
            _seenDrops = dropped;
            _buffer.Advance(count);
            _ready = false;
            _fraction = 0;
            _fill = _target;
            Interlocked.Increment(ref _resyncs);
            return _last *= .98f;
        }
        if (count > _maxLag)
        {
            _buffer.Advance(count - _target);
            count = _target;
            _fraction = 0;
            _fill = _target;
            _fade = 0;
            Interlocked.Increment(ref _resyncs);
        }
        if (!_ready)
        {
            if (count < _target) return _last *= .98f;
            _ready = true;
            _fade = 0;
            _fraction = 0;
        }
        // Low pass the sawtooth created by capture packets; adjust within +/- 0.5%.
        _fill += .00005 * (count - _fill);
        double correction = Math.Clamp((_fill - _target) / _target * .002, -.005, .005);
        Volatile.Write(ref _correction, correction);
        double step = _baseRatio * (1 + correction);
        int advance = (int)(_fraction + step);
        if (count < Math.Max(2, advance + 1))
        {
            _ready = false;
            Interlocked.Increment(ref _underruns);
            return _last *= .98f;
        }
        float value = _buffer.Peek(0) + (_buffer.Peek(1) - _buffer.Peek(0)) * (float)_fraction;
        _fraction += step;
        _buffer.Advance((int)_fraction);
        _fraction -= (int)_fraction;
        float ramp = Math.Min(1, ++_fade / 240f);
        _last += ramp * (value - _last);
        return _last;
    }
}

public sealed class SpscQueue<T> where T : struct
{
    private readonly T[] _items;
    private long _read, _write;
    public SpscQueue(int capacity) => _items = new T[capacity];
    public bool TryEnqueue(T item)
    {
        long w = _write;
        if (w - Volatile.Read(ref _read) >= _items.Length) return false;
        _items[(int)(w % _items.Length)] = item;
        Volatile.Write(ref _write, w + 1);
        return true;
    }
    public bool TryDequeue(out T item)
    {
        long r = _read;
        if (r == Volatile.Read(ref _write)) { item = default; return false; }
        int i = (int)(r % _items.Length);
        item = _items[i];
        _items[i] = default;
        Volatile.Write(ref _read, r + 1);
        return true;
    }
}
