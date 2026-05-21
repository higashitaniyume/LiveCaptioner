using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LiveCaptioner.Localization;

namespace LiveCaptioner.Services;

public sealed class CustomStreamingAsrService : IAsrService, IStreamingAudioConsumer
{
    private const int SampleRate = RollingAudioBuffer.SampleRate;
    private const int BytesPerSample = RollingAudioBuffer.BytesPerSample;
    private const int ChunkBytes = SampleRate * BytesPerSample / 20; // 50ms

    private static readonly string[] DefaultTranscriptFields =
        ["transcript", "text", "result", "recognize_result", "voice_text_str"];

    private readonly object _chunkGate = new();
    private readonly string _webSocketUrl;
    private readonly string _apiKey;
    private readonly string _authHeader;
    private readonly string _transcriptField;
    private readonly byte[] _chunkBuffer = new byte[ChunkBytes];
    private int _chunkBufferLength;
    private CancellationTokenSource? _cts;
    private ClientWebSocket? _webSocket;
    private Channel<byte[]>? _audioChannel;
    private Task? _sendTask;
    private Task? _receiveTask;
    private string _lastEmitted = string.Empty;
    private bool _disposed;

    public CustomStreamingAsrService(string webSocketUrl, string apiKey, string authHeader, string transcriptField)
    {
        _webSocketUrl = webSocketUrl.Trim();
        _apiKey = apiKey.Trim();
        _authHeader = string.IsNullOrWhiteSpace(authHeader) ? "Authorization" : authHeader.Trim();
        _transcriptField = transcriptField.Trim();
    }

    public event EventHandler<string>? TranscriptReady;
    public event EventHandler<string>? StatusChanged;

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_webSocket is not null) return;

        if (string.IsNullOrWhiteSpace(_webSocketUrl))
        {
            StatusChanged?.Invoke(this, LocalizationManager.T("CustomSttMissingUrl"));
            return;
        }

        if (!Uri.TryCreate(_webSocketUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "ws" && uri.Scheme != "wss"))
        {
            StatusChanged?.Invoke(this, LocalizationManager.T("CustomSttInvalidUrl"));
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
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            _webSocket.Options.SetRequestHeader(_authHeader, _apiKey);
        }

        await _webSocket.ConnectAsync(uri, _cts.Token).ConfigureAwait(false);
        _sendTask = Task.Run(() => SendLoopAsync(_cts.Token));
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        StatusChanged?.Invoke(this, LocalizationManager.Format("CustomSttConnected", uri.Host));
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var webSocket = _webSocket;
        if (cts is null || webSocket is null) return;

        try
        {
            _audioChannel?.Writer.TryComplete();

            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await cts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(_sendTask ?? Task.CompletedTask, _receiveTask ?? Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, LocalizationManager.Format("CustomSttStopFailed", ex.Message));
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
        if (_audioChannel is null || _cts?.IsCancellationRequested == true || pcm16Mono16k.IsEmpty) return;

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

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        if (_audioChannel is null || _webSocket is null) return;

        await foreach (var chunk in _audioChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_webSocket.State != WebSocketState.Open) break;
            await _webSocket.SendAsync(chunk, WebSocketMessageType.Binary, true, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_webSocket is null) return;

        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (!cancellationToken.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await _webSocket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
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
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? transcript = null;

            // If user specified a dot-separated path, follow it.
            if (!string.IsNullOrWhiteSpace(_transcriptField))
            {
                transcript = ResolveJsonPath(root, _transcriptField);
            }

            // Fall back to scanning common field names.
            if (string.IsNullOrWhiteSpace(transcript))
            {
                transcript = ScanForTranscript(root);
            }

            if (!string.IsNullOrWhiteSpace(transcript))
            {
                EmitTranscript(transcript);
            }
        }
        catch (JsonException)
        {
        }
    }

    private static string? ScanForTranscript(JsonElement element)
    {
        // Check top-level fields first.
        foreach (var field in DefaultTranscriptFields)
        {
            if (element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }

        // Check nested common patterns: payload.result, data.result.text, result.text
        if (element.TryGetProperty("payload", out var payload) &&
            payload.TryGetProperty("result", out var payloadResult) &&
            payloadResult.ValueKind == JsonValueKind.String)
        {
            return payloadResult.GetString();
        }

        if (element.TryGetProperty("data", out var data))
        {
            if (data.TryGetProperty("result", out var dataResult) &&
                dataResult.TryGetProperty("text", out var dataResultText) &&
                dataResultText.ValueKind == JsonValueKind.String)
            {
                return dataResultText.GetString();
            }
        }

        // Recurse into objects/arrays one level deep.
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                var nested = ScanForTranscript(property.Value);
                if (nested is not null) return nested;
            }
        }

        return null;
    }

    private static string? ResolveJsonPath(JsonElement root, string path)
    {
        var segments = path.Split('.');
        var current = root;

        foreach (var segment in segments)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private void EmitTranscript(string text)
    {
        var normalized = NormalizeForDedup(text);
        if (normalized.Length < 2 || normalized == _lastEmitted) return;

        _lastEmitted = normalized;
        TranscriptReady?.Invoke(this, text);
    }

    private static string NormalizeForDedup(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch) && !char.IsPunctuation(ch))
                builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync().ConfigureAwait(false);
        _disposed = true;
    }
}
