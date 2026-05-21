using System.Threading.Channels;
using Google.Cloud.Speech.V1;
using Google.Protobuf;

namespace LiveCaptioner.Services;

public sealed class GoogleSpeechAsrService : IAsrService, IStreamingAudioConsumer
{
    private readonly string _credentialsPath;
    private readonly string _language;
    private Channel<byte[]>? _audioChannel;
    private SpeechClient.StreamingRecognizeStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _sendTask;
    private Task? _receiveTask;
    private bool _disposed;

    public GoogleSpeechAsrService(string credentialsPath, string language)
    {
        _credentialsPath = credentialsPath.Trim();
        _language = string.IsNullOrWhiteSpace(language) ? "en-US" : language.Trim();
    }

    public event EventHandler<string>? TranscriptReady;
    public event EventHandler<string>? StatusChanged;

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_stream is not null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_credentialsPath))
        {
            Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", _credentialsPath);
        }

        _cts = new CancellationTokenSource();
        _audioChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(240)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        var client = await SpeechClient.CreateAsync().ConfigureAwait(false);
        _stream = client.StreamingRecognize();
        await _stream.WriteAsync(new StreamingRecognizeRequest
        {
            StreamingConfig = new StreamingRecognitionConfig
            {
                Config = new RecognitionConfig
                {
                    Encoding = RecognitionConfig.Types.AudioEncoding.Linear16,
                    SampleRateHertz = RollingAudioBuffer.SampleRate,
                    LanguageCode = _language,
                    EnableAutomaticPunctuation = true
                },
                InterimResults = false,
                SingleUtterance = false
            }
        }).ConfigureAwait(false);

        _sendTask = Task.Run(() => SendLoopAsync(_cts.Token));
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        StatusChanged?.Invoke(this, $"Google Speech 在线识别已连接：{_language}");
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        if (cts is null)
        {
            return;
        }

        try
        {
            _audioChannel?.Writer.TryComplete();
            await cts.CancelAsync().ConfigureAwait(false);
            if (_stream is not null)
            {
                await _stream.WriteCompleteAsync().ConfigureAwait(false);
            }

            await Task.WhenAll(_sendTask ?? Task.CompletedTask, _receiveTask ?? Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
        }
        finally
        {
            cts.Dispose();
            _cts = null;
            _stream = null;
            _audioChannel = null;
            _sendTask = null;
            _receiveTask = null;
        }
    }

    public void AddAudio(ReadOnlySpan<byte> pcm16Mono16k)
    {
        if (_audioChannel is null || pcm16Mono16k.IsEmpty)
        {
            return;
        }

        _audioChannel.Writer.TryWrite(pcm16Mono16k.ToArray());
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        if (_audioChannel is null || _stream is null)
        {
            return;
        }

        await foreach (var chunk in _audioChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await _stream.WriteAsync(new StreamingRecognizeRequest
            {
                AudioContent = ByteString.CopyFrom(chunk)
            }).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            return;
        }

        var responseStream = _stream.GetResponseStream();
        while (await responseStream.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var result in responseStream.Current.Results)
            {
                if (!result.IsFinal || result.Alternatives.Count == 0)
                {
                    continue;
                }

                var transcript = result.Alternatives[0].Transcript?.Trim();
                if (!string.IsNullOrWhiteSpace(transcript))
                {
                    TranscriptReady?.Invoke(this, transcript);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _disposed = true;
    }
}
