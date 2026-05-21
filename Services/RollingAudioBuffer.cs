namespace LiveCaptioner.Services;

public sealed class RollingAudioBuffer
{
    public const int SampleRate = 16_000;
    public const int BytesPerSample = 2;
    public const int Channels = 1;

    private readonly object _gate = new();
    private readonly List<byte> _buffer = [];
    private readonly int _maxBytes;
    private int _bytesSinceLastWindow;

    public RollingAudioBuffer(TimeSpan maxDuration)
    {
        _maxBytes = BytesFor(maxDuration);
    }

    public void Add(ReadOnlySpan<byte> pcm16Mono16k)
    {
        if (pcm16Mono16k.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            _buffer.AddRange(pcm16Mono16k.ToArray());
            _bytesSinceLastWindow += pcm16Mono16k.Length;

            if (_buffer.Count > _maxBytes)
            {
                _buffer.RemoveRange(0, _buffer.Count - _maxBytes);
            }
        }
    }

    public bool TryReadWindow(TimeSpan windowDuration, TimeSpan strideDuration, out byte[] pcm16Mono16k)
    {
        var windowBytes = BytesFor(windowDuration);
        var strideBytes = BytesFor(strideDuration);

        lock (_gate)
        {
            if (_buffer.Count < windowBytes || _bytesSinceLastWindow < strideBytes)
            {
                pcm16Mono16k = [];
                return false;
            }

            pcm16Mono16k = _buffer
                .Skip(_buffer.Count - windowBytes)
                .Take(windowBytes)
                .ToArray();

            _bytesSinceLastWindow = 0;
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _buffer.Clear();
            _bytesSinceLastWindow = 0;
        }
    }

    private static int BytesFor(TimeSpan duration)
    {
        var bytes = (int)Math.Round(duration.TotalSeconds * SampleRate * Channels * BytesPerSample);
        return bytes - bytes % 2;
    }
}
