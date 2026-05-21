using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveCaptioner.Localization;
using LiveCaptioner.Models;

namespace LiveCaptioner.Services;

public sealed class LlmSubtitleService : IDisposable
{
    private static readonly Dictionary<string, string> TargetLanguageNames = new()
    {
        ["zh-CN"] = "简体中文",
        ["zh-TW"] = "繁體中文",
        ["en"] = "English",
        ["ja"] = "日本語",
        ["ko"] = "한국어",
        ["fr"] = "Français",
        ["de"] = "Deutsch",
        ["es"] = "Español",
        ["pt"] = "Português",
        ["ru"] = "Русский",
        ["ar"] = "العربية",
        ["th"] = "ไทย",
        ["vi"] = "Tiếng Việt",
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _debounceDelay = TimeSpan.FromMilliseconds(350);
    private CancellationTokenSource? _debounceCts;
    private bool _disposed;

    public LlmSubtitleService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(25);
    }

    public string Provider { get; set; } = "deepseek";
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";
    public string Model { get; set; } = "deepseek-v4-flash";
    public string TargetLanguage { get; set; } = "zh-CN";
    public bool AutoTranslate { get; set; } = true;
    public bool BilingualComparison { get; set; } = true;

    private string BuildSystemPrompt()
    {
        var targetName = TargetLanguageNames.TryGetValue(TargetLanguage, out var name)
            ? name
            : TargetLanguage;
        return LocalizationManager.T("LlmSystemPrompt").Replace("{TargetLanguage}", targetName);
    }

    public event EventHandler<BilingualSubtitle>? SubtitleReady;
    public event EventHandler<string>? StatusChanged;

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

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            StatusChanged?.Invoke(this, LocalizationManager.T("LlmMissingKey"));
            return BilingualSubtitle.Raw(rawText);
        }

        try
        {
            var content = IsClaudeProvider()
                ? await CallClaudeAsync(rawText, cancellationToken).ConfigureAwait(false)
                : await CallOpenAiCompatibleAsync(rawText, cancellationToken).ConfigureAwait(false);

            return ParseSubtitle(rawText, content);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, LocalizationManager.Format("LlmUnavailable", ex.Message));
            return BilingualSubtitle.Raw(rawText);
        }
    }

    private async Task<string?> CallOpenAiCompatibleAsync(string rawText, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{NormalizeBaseUrl()}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        request.Content = JsonContent.Create(new
        {
            model = Model,
            temperature = 0.2,
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new { role = "system", content = BuildSystemPrompt() },
                new { role = "user", content = rawText }
            }
        }, options: JsonOptions);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            StatusChanged?.Invoke(this, LocalizationManager.Format("LlmRequestFailed", (int)response.StatusCode, response.ReasonPhrase));
            return null;
        }

        var payload = await response.Content
            .ReadFromJsonAsync<OpenAiChatCompletionResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return payload?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();
    }

    private async Task<string?> CallClaudeAsync(string rawText, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{NormalizeBaseUrl()}/v1/messages");
        request.Headers.Add("x-api-key", ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = JsonContent.Create(new
        {
            model = Model,
            max_tokens = 800,
            temperature = 0.2,
            system = BuildSystemPrompt(),
            messages = new[]
            {
                new { role = "user", content = rawText }
            }
        }, options: JsonOptions);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            StatusChanged?.Invoke(this, LocalizationManager.Format("ClaudeRequestFailed", (int)response.StatusCode, response.ReasonPhrase));
            return null;
        }

        var payload = await response.Content
            .ReadFromJsonAsync<ClaudeMessageResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return payload?.Content?.FirstOrDefault(item => string.Equals(item.Type, "text", StringComparison.OrdinalIgnoreCase))
            ?.Text
            ?.Trim();
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
            StatusChanged?.Invoke(this, LocalizationManager.T("LlmJsonParseFailed"));
            return new BilingualSubtitle
            {
                RawText = rawText.Trim(),
                CorrectedText = content.Trim(),
                TranslatedText = string.Empty
            };
        }
    }

    private bool IsClaudeProvider()
    {
        return string.Equals(Provider, "claude", StringComparison.OrdinalIgnoreCase);
    }

    private string NormalizeBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(BaseUrl))
        {
            return BaseUrl.Trim().TrimEnd('/');
        }

        return IsClaudeProvider() ? "https://api.anthropic.com" : "https://api.deepseek.com/v1";
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

    private sealed class OpenAiChatCompletionResponse
    {
        [JsonPropertyName("choices")]
        public List<OpenAiChoice>? Choices { get; set; }
    }

    private sealed class OpenAiChoice
    {
        [JsonPropertyName("message")]
        public OpenAiMessage? Message { get; set; }
    }

    private sealed class OpenAiMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }

    private sealed class ClaudeMessageResponse
    {
        [JsonPropertyName("content")]
        public List<ClaudeContent>? Content { get; set; }
    }

    private sealed class ClaudeContent
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    private sealed class BilingualResult
    {
        [JsonPropertyName("corrected_text")]
        public string? CorrectedText { get; set; }

        [JsonPropertyName("translated_text")]
        public string? TranslatedText { get; set; }
    }
}
