using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace LiveCaptioner.Services;

public sealed class AzureSpeechAsrService : IAsrService, IStreamingAudioConsumer
{
    private readonly string _subscriptionKey;
    private readonly string _region;
    private readonly string _language;
    private PushAudioInputStream? _pushStream;
    private SpeechRecognizer? _recognizer;
    private bool _disposed;

    public AzureSpeechAsrService(string subscriptionKey, string region, string language)
    {
        _subscriptionKey = subscriptionKey.Trim();
        _region = region.Trim();
        _language = string.IsNullOrWhiteSpace(language) ? "en-US" : language.Trim();
    }

    public event EventHandler<string>? TranscriptReady;
    public event EventHandler<string>? StatusChanged;

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_recognizer is not null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_subscriptionKey) || string.IsNullOrWhiteSpace(_region))
        {
            StatusChanged?.Invoke(this, "未配置 Azure Speech Key 或 Region，无法启动微软在线识别");
            return;
        }

        var speechConfig = SpeechConfig.FromSubscription(_subscriptionKey, _region);
        speechConfig.SpeechRecognitionLanguage = _language;
        speechConfig.SetProperty(PropertyId.SpeechServiceResponse_PostProcessingOption, "TrueText");

        var format = AudioStreamFormat.GetWaveFormatPCM(RollingAudioBuffer.SampleRate, 16, 1);
        _pushStream = AudioInputStream.CreatePushStream(format);
        var audioConfig = AudioConfig.FromStreamInput(_pushStream);
        _recognizer = new SpeechRecognizer(speechConfig, audioConfig);
        _recognizer.Recognized += (_, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrWhiteSpace(e.Result.Text))
            {
                TranscriptReady?.Invoke(this, e.Result.Text.Trim());
            }
        };
        _recognizer.Canceled += (_, e) =>
            StatusChanged?.Invoke(this, $"Azure Speech 已取消：{e.Reason} {e.ErrorDetails}");
        _recognizer.SessionStarted += (_, _) => StatusChanged?.Invoke(this, $"Azure Speech 在线识别已连接：{_language}");
        _recognizer.SessionStopped += (_, _) => StatusChanged?.Invoke(this, "Azure Speech 会话已结束");

        await _recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        if (_recognizer is not null)
        {
            await _recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
            _recognizer.Dispose();
            _recognizer = null;
        }

        _pushStream?.Close();
        _pushStream?.Dispose();
        _pushStream = null;
    }

    public void AddAudio(ReadOnlySpan<byte> pcm16Mono16k)
    {
        if (_pushStream is null || pcm16Mono16k.IsEmpty)
        {
            return;
        }

        _pushStream.Write(pcm16Mono16k.ToArray());
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
