using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LiveCaptioner.Services;

public sealed class MonoDownmixSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly float[] _sourceBuffer;

    public MonoDownmixSampleProvider(ISampleProvider source)
    {
        if (source.WaveFormat.Channels < 1)
        {
            throw new ArgumentException("Source must contain at least one channel.", nameof(source));
        }

        _source = source;
        _sourceBuffer = new float[4096 * source.WaveFormat.Channels];
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var channels = _source.WaveFormat.Channels;
        var framesRequested = count;
        var sourceSamplesNeeded = Math.Min(_sourceBuffer.Length, framesRequested * channels);
        var sourceSamplesRead = _source.Read(_sourceBuffer, 0, sourceSamplesNeeded);
        var framesRead = sourceSamplesRead / channels;

        for (var frame = 0; frame < framesRead; frame++)
        {
            var sum = 0f;
            var sourceIndex = frame * channels;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += _sourceBuffer[sourceIndex + channel];
            }

            buffer[offset + frame] = sum / channels;
        }

        return framesRead;
    }
}
