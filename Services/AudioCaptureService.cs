using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LiveCaptioner.Services;

public sealed class AudioCaptureService : IDisposable
{
    private readonly RollingAudioBuffer _audioBuffer;
    private WasapiLoopbackCapture? _capture;
    private bool _disposed;

    public AudioCaptureService(RollingAudioBuffer audioBuffer)
    {
        _audioBuffer = audioBuffer;
    }

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<byte[]>? PcmAudioAvailable;

    public bool IsRunning => _capture is not null;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_capture is not null)
        {
            return;
        }

        _audioBuffer.Clear();
        _capture = new WasapiLoopbackCapture();
        _capture.DataAvailable += CaptureOnDataAvailable;
        _capture.RecordingStopped += CaptureOnRecordingStopped;
        _capture.StartRecording();

        StatusChanged?.Invoke(this, $"系统音频捕获已启动：{_capture.WaveFormat}");
    }

    public void Stop()
    {
        var capture = _capture;
        if (capture is null)
        {
            return;
        }

        capture.StopRecording();
        DetachAndDispose(capture);
        _capture = null;
        StatusChanged?.Invoke(this, "系统音频捕获已停止");
    }

    private void CaptureOnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || sender is not WasapiLoopbackCapture capture)
        {
            return;
        }

        try
        {
            var pcm = ConvertToWhisperPcm(e.Buffer.AsSpan(0, e.BytesRecorded), capture.WaveFormat);
            _audioBuffer.Add(pcm);
            PcmAudioAvailable?.Invoke(this, pcm);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"音频重采样失败：{ex.Message}");
        }
    }

    private void CaptureOnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            StatusChanged?.Invoke(this, $"音频捕获中断：{e.Exception.Message}");
        }
    }

    private static byte[] ConvertToWhisperPcm(ReadOnlySpan<byte> input, WaveFormat inputFormat)
    {
        using var inputStream = new MemoryStream(input.ToArray(), writable: false);
        using var sourceStream = new RawSourceWaveStream(inputStream, inputFormat);

        ISampleProvider sampleProvider = sourceStream.ToSampleProvider();
        if (sampleProvider.WaveFormat.Channels != 1)
        {
            sampleProvider = new MonoDownmixSampleProvider(sampleProvider);
        }

        if (sampleProvider.WaveFormat.SampleRate != RollingAudioBuffer.SampleRate)
        {
            sampleProvider = new WdlResamplingSampleProvider(sampleProvider, RollingAudioBuffer.SampleRate);
        }

        IWaveProvider waveProvider = new SampleToWaveProvider16(sampleProvider);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = waveProvider.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private void DetachAndDispose(WasapiLoopbackCapture capture)
    {
        capture.DataAvailable -= CaptureOnDataAvailable;
        capture.RecordingStopped -= CaptureOnRecordingStopped;
        capture.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }
}
