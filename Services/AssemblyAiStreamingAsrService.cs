using System.Net.WebSockets;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using LiveCaptioner.Localization;

namespace LiveCaptioner.Services;

public sealed class AssemblyAiStreamingAsrService : IAsrService, IStreamingAudioConsumer
{
    private const int SampleRate = RollingAudioBuffer.SampleRate;
    private const int BytesPerSample = RollingAudioBuffer.BytesPerSample;
    private const int ChunkBytes = SampleRate * BytesPerSample / 20; // 50ms
    private readonly object _chunkGate = new();
    private readonly string _apiKey;
    private readonly string _speechModel;
    private readonly byte[] _chunkBuffer = new byte[ChunkBytes];
    private int _chunkBufferLength;
    private CancellationTokenSource? _cts;
    private ClientWebSocket? _webSocket;
    private Channel<byte[]>? _audioChannel;
    private Task? _sendTask;
    private Task? _receiveTask;
    private string _lastEmitted = string.Empty;
    private bool _disposed;

    public AssemblyAiStreamingAsrService(string apiKey, string speechModel)
    {
        _apiKey = apiKey.Trim();
        _speechModel = string.IsNullOrWhiteSpace(speechModel)
            ? "universal-streaming-multilingual"
            : speechModel.Trim();
    }

    public event EventHandler<string>? TranscriptReady;
    public event EventHandler<string>? StatusChanged;

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_webSocket is not null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            StatusChanged?.Invoke(this, LocalizationManager.T("AssemblyAiMissingKey"));
            return;
        }

        _cts = new CancellationTokenSource();
        _audioChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(240)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        _webSocket = new ClientWebSocket();
        _webSocket.Options.SetRequestHeader("Authorization", _apiKey);

        var uri = BuildWebSocketUri();
        await _webSocket.ConnectAsync(uri, _cts.Token).ConfigureAwait(false);
        _sendTask = Task.Run(() => SendLoopAsync(_cts.Token));
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        StatusChanged?.Invoke(this, LocalizationManager.Format("AssemblyAiConnected", _speechModel));
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var webSocket = _webSocket;
        if (cts is null || webSocket is null)
        {
            return;
        }

        try
        {
            _audioChannel?.Writer.TryComplete();

            if (webSocket.State == WebSocketState.Open)
            {
                var terminate = Encoding.UTF8.GetBytes("""{"type":"Terminate"}""");
                await webSocket.SendAsync(terminate, WebSocketMessageType.Text, true, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await cts.CancelAsync().ConfigureAwait(false);
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
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, LocalizationManager.Format("AssemblyAiStopFailed", ex.Message));
        }
        finally
        {
            webSocket.Dispose();
            cts.Dispose();
            _webSocket = null;
            _cts = null;
            _audioChannel = null;
            _sendTask = null;
            _receiveTask = null;
            _chunkBufferLength = 0;
        }
    }

    public void AddAudio(ReadOnlySpan<byte> pcm16Mono16k)
    {
        if (_audioChannel is null || _cts?.IsCancellationRequested == true || pcm16Mono16k.IsEmpty)
        {
            return;
        }

        lock (_chunkGate)
        {
            while (!pcm16Mono16k.IsEmpty)
            {
                var take = Math.Min(ChunkBytes - _chunkBufferLength, pcm16Mono16k.Length);
                pcm16Mono16k[..take].CopyTo(_chunkBuffer.AsSpan(_chunkBufferLength));
                _chunkBufferLength += take;
                pcm16Mono16k = pcm16Mono16k[take..];

                if (_chunkBufferLength == ChunkBytes)
                {
                    _audioChannel.Writer.TryWrite(_chunkBuffer.ToArray());
                    _chunkBufferLength = 0;
                }
            }
        }
    }

    private Uri BuildWebSocketUri()
    {
        var parameters = new Dictionary<string, string>
        {
            ["speech_model"] = _speechModel,
            ["sample_rate"] = SampleRate.ToString(),
            ["encoding"] = "pcm_s16le",
            ["format_turns"] = "true",
            ["min_turn_silence"] = "300",
            ["max_turn_silence"] = "900",
            ["language_detection"] = _speechModel.Contains("multilingual", StringComparison.OrdinalIgnoreCase)
                ? "true"
                : "false"
        };

        var query = string.Join("&", parameters.Select(item =>
            $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));

        return new Uri($"wss://streaming.assemblyai.com/v3/ws?{query}");
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        if (_audioChannel is null || _webSocket is null)
        {
            return;
        }

        await foreach (var chunk in _audioChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_webSocket.State != WebSocketState.Open)
            {
                break;
            }

            await _webSocket.SendAsync(chunk, WebSocketMessageType.Binary, true, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_webSocket is null)
        {
            return;
        }

        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (!cancellationToken.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await _webSocket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    StatusChanged?.Invoke(this, LocalizationManager.Format("AssemblyAiClosed", _webSocket.CloseStatusDescription));
                    return;
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Text)
            {
                HandleMessage(Encoding.UTF8.GetString(message.ToArray()));
            }
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            var message = JsonSerializer.Deserialize<AssemblyAiMessage>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (message is null)
            {
                return;
            }

            if (string.Equals(message.Type, "Begin", StringComparison.OrdinalIgnoreCase))
            {
                StatusChanged?.Invoke(this, LocalizationManager.Format("AssemblyAiSessionStarted", message.Id));
                return;
            }

            if (string.Equals(message.Type, "Termination", StringComparison.OrdinalIgnoreCase))
            {
                StatusChanged?.Invoke(this, LocalizationManager.T("AssemblyAiSessionEnded"));
                return;
            }

            var transcript = message.Transcript?.Trim();
            if (string.IsNullOrWhiteSpace(transcript))
            {
                return;
            }

            if (message.EndOfTurn == true || message.TurnIsFormatted == true)
            {
                EmitTranscript(transcript);
            }
        }
        catch (JsonException ex)
        {
            StatusChanged?.Invoke(this, LocalizationManager.Format("AssemblyAiParseFailed", ex.Message));
        }
    }

    private void EmitTranscript(string text)
    {
        var normalized = NormalizeForDedup(text);
        if (normalized.Length < 2 || normalized == _lastEmitted)
        {
            return;
        }

        _lastEmitted = normalized;
        TranscriptReady?.Invoke(this, text);
    }

    private static string NormalizeForDedup(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch) && !char.IsPunctuation(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
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

    private sealed class AssemblyAiMessage
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("transcript")]
        public string? Transcript { get; set; }

        [JsonPropertyName("end_of_turn")]
        public bool? EndOfTurn { get; set; }

        [JsonPropertyName("turn_is_formatted")]
        public bool? TurnIsFormatted { get; set; }
    }
}
