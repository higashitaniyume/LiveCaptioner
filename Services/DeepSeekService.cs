using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveCaptioner.Models;

namespace LiveCaptioner.Services;

public sealed class DeepSeekService : IDisposable
{
    private const string SystemPrompt =
        """
        你是一个高精度的实时双语字幕助手。请对输入的粗糙语音识别文本进行：
        1. 纠正同音错别字和明显 ASR 错误；
        2. 根据语气合理添加标点符号；
        3. 保留原文语言，输出校正后的原文；
        4. 输出对应的简体中文翻译；如果原文已经是中文，则中文翻译字段输出同一句校正后的中文。
        严格只输出 JSON，不要 Markdown，不要解释。格式：
        {"corrected_text":"校正后的原文","translated_text":"简体中文翻译"}
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _debounceDelay = TimeSpan.FromMilliseconds(350);
    private CancellationTokenSource? _debounceCts;
    private string _apiKey = string.Empty;
    private bool _disposed;

    public DeepSeekService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress = new Uri("https://api.deepseek.com/");
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
    }

    public bool AutoTranslate { get; set; } = true;
    public bool BilingualComparison { get; set; } = true;
    public string Model { get; set; } = "deepseek-v4-flash";

    public event EventHandler<BilingualSubtitle>? SubtitleReady;
    public event EventHandler<string>? StatusChanged;

    public void SetApiKey(string apiKey)
    {
        _apiKey = apiKey.Trim();
    }

    public void QueueText(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText) || _disposed)
        {
            return;
        }

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();

        var token = _debounceCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounceDelay, token).ConfigureAwait(false);
                var subtitle = await TranslateAndPolishAsync(rawText, token).ConfigureAwait(false);
                SubtitleReady?.Invoke(this, subtitle);
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    public async Task<BilingualSubtitle> TranslateAndPolishAsync(string rawText, CancellationToken cancellationToken = default)
    {
        if (!AutoTranslate)
        {
            return BilingualSubtitle.Raw(rawText);
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            StatusChanged?.Invoke(this, "未配置 DeepSeek API Key，显示原始识别文本");
            return BilingualSubtitle.Raw(rawText);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = JsonContent.Create(new
        {
            model = Model,
            temperature = 0.2,
            messages = new[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = rawText }
            }
        }, options: JsonOptions);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                StatusChanged?.Invoke(this, $"DeepSeek 请求失败：{(int)response.StatusCode} {response.ReasonPhrase}");
                return BilingualSubtitle.Raw(rawText);
            }

            var payload = await response.Content
                .ReadFromJsonAsync<ChatCompletionResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            var content = payload?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();
            return ParseSubtitle(rawText, content);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"DeepSeek 暂不可用：{ex.Message}");
            return BilingualSubtitle.Raw(rawText);
        }
    }

    private BilingualSubtitle ParseSubtitle(string rawText, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return BilingualSubtitle.Raw(rawText);
        }

        var json = ExtractJsonObject(content);
        try
        {
            var result = JsonSerializer.Deserialize<BilingualResult>(json, JsonOptions);
            var corrected = string.IsNullOrWhiteSpace(result?.CorrectedText)
                ? rawText.Trim()
                : result.CorrectedText.Trim();
            var translated = BilingualComparison && !string.IsNullOrWhiteSpace(result?.TranslatedText)
                ? result.TranslatedText.Trim()
                : string.Empty;

            return new BilingualSubtitle
            {
                RawText = rawText.Trim(),
                CorrectedText = corrected,
                TranslatedText = translated
            };
        }
        catch (JsonException)
        {
            StatusChanged?.Invoke(this, "DeepSeek 返回格式不是 JSON，显示返回文本");
            return new BilingualSubtitle
            {
                RawText = rawText.Trim(),
                CorrectedText = content.Trim(),
                TranslatedText = string.Empty
            };
        }
    }

    private static string ExtractJsonObject(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start
            ? content[start..(end + 1)]
            : content;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _httpClient.Dispose();
        _disposed = true;
    }

    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("choices")]
        public List<Choice>? Choices { get; set; }
    }

    private sealed class Choice
    {
        [JsonPropertyName("message")]
        public Message? Message { get; set; }
    }

    private sealed class Message
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }

    private sealed class BilingualResult
    {
        [JsonPropertyName("corrected_text")]
        public string? CorrectedText { get; set; }

        [JsonPropertyName("translated_text")]
        public string? TranslatedText { get; set; }
    }
}
