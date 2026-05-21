using System.IO;
using System.Text.Json;
using LiveCaptioner.Models;

namespace LiveCaptioner.Services;

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string AppDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiveCaptioner");

    public string SettingsPath => Path.Combine(AppDataDirectory, "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var stream = File.OpenRead(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions);
                if (settings is not null)
                {
                    return Normalize(settings);
                }
            }
        }
        catch
        {
            // A corrupt settings file should not prevent captions from starting.
        }

        return Normalize(new AppSettings
        {
            DeepSeekApiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? string.Empty
        });
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(AppDataDirectory);
        await using var stream = File.Create(SettingsPath);
        await JsonSerializer.SerializeAsync(stream, Normalize(settings), JsonOptions, cancellationToken);
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.FontSize = Math.Clamp(settings.FontSize, 16, 72);
        settings.BackgroundOpacity = Math.Clamp(settings.BackgroundOpacity, 0.15, 0.9);
        settings.Language = NormalizeLanguage(settings.Language);
        settings.TargetLanguage = NormalizeTargetLanguage(settings.TargetLanguage);
        settings.AsrProvider = NormalizeAsrProvider(settings.AsrProvider);
        settings.LlmProvider = NormalizeLlmProvider(settings.LlmProvider);
        if (string.IsNullOrWhiteSpace(settings.LlmApiKey) && !string.IsNullOrWhiteSpace(settings.DeepSeekApiKey))
        {
            settings.LlmApiKey = settings.DeepSeekApiKey;
        }

        settings.LlmApiKey = settings.LlmApiKey.Trim();
        settings.LlmBaseUrl = NormalizeLlmBaseUrl(settings.LlmProvider, settings.LlmBaseUrl);
        settings.LlmModel = string.IsNullOrWhiteSpace(settings.LlmModel)
            ? NormalizeDefaultLlmModel(settings.LlmProvider, settings.DeepSeekModel)
            : settings.LlmModel.Trim();
        settings.AssemblyAiApiKey = string.IsNullOrWhiteSpace(settings.AssemblyAiApiKey)
            ? Environment.GetEnvironmentVariable("ASSEMBLYAI_API_KEY") ?? string.Empty
            : settings.AssemblyAiApiKey.Trim();
        settings.AssemblyAiSpeechModel = string.IsNullOrWhiteSpace(settings.AssemblyAiSpeechModel)
            ? "universal-streaming-multilingual"
            : settings.AssemblyAiSpeechModel.Trim();
        settings.WhisperModel = NormalizeWhisperModel(settings.WhisperModel);
        settings.AsrBackend = NormalizeAsrBackend(settings.AsrBackend);
        settings.DeepSeekModel = string.IsNullOrWhiteSpace(settings.DeepSeekModel)
            ? "deepseek-v4-flash"
            : settings.DeepSeekModel.Trim();
        settings.AzureSpeechKey = string.IsNullOrWhiteSpace(settings.AzureSpeechKey)
            ? Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY") ?? string.Empty
            : settings.AzureSpeechKey.Trim();
        settings.AzureSpeechRegion = string.IsNullOrWhiteSpace(settings.AzureSpeechRegion)
            ? Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION") ?? string.Empty
            : settings.AzureSpeechRegion.Trim();
        settings.AzureSpeechLanguage = string.IsNullOrWhiteSpace(settings.AzureSpeechLanguage)
            ? "en-US"
            : settings.AzureSpeechLanguage.Trim();
        settings.GoogleCredentialsPath = string.IsNullOrWhiteSpace(settings.GoogleCredentialsPath)
            ? Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS") ?? string.Empty
            : settings.GoogleCredentialsPath.Trim();
        settings.GoogleSpeechLanguage = string.IsNullOrWhiteSpace(settings.GoogleSpeechLanguage)
            ? "en-US"
            : settings.GoogleSpeechLanguage.Trim();
        settings.CustomSttWebSocketUrl = settings.CustomSttWebSocketUrl.Trim();
        settings.CustomSttApiKey = settings.CustomSttApiKey.Trim();

        return settings;
    }

    private static string NormalizeWhisperModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return "tiny";
        }

        var normalized = model.Trim().ToLowerInvariant();
        return normalized is "tiny" or "base" or "small" ? normalized : "tiny";
    }

    private static string NormalizeLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "zh-CN";
        }

        var normalized = language.Trim();
        return normalized.Equals("en", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("en-US", StringComparison.OrdinalIgnoreCase)
            ? "en"
            : "zh-CN";
    }

    private static readonly HashSet<string> ValidTargetLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "zh-CN", "zh-TW", "en", "ja", "ko", "fr", "de", "es", "pt", "ru", "ar", "th", "vi"
    };

    private static string NormalizeTargetLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "zh-CN";
        }

        var normalized = language.Trim();
        return ValidTargetLanguages.Contains(normalized) ? normalized : "zh-CN";
    }

    private static string NormalizeAsrProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return "assemblyai";
        }

        var normalized = provider.Trim().ToLowerInvariant();
        return normalized is "assemblyai" or "azure" or "google" or "custom" or "local" ? normalized : "assemblyai";
    }

    private static string NormalizeAsrBackend(string backend)
    {
        if (string.IsNullOrWhiteSpace(backend))
        {
            return "auto";
        }

        var normalized = backend.Trim().ToLowerInvariant();
        return normalized is "auto" or "cuda" or "vulkan" or "cpu" ? normalized : "auto";
    }

    private static string NormalizeLlmProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return "deepseek";
        }

        var normalized = provider.Trim().ToLowerInvariant();
        return normalized is "deepseek" or "openai" or "claude" or "custom-openai" ? normalized : "deepseek";
    }

    private static string NormalizeDefaultLlmModel(string provider, string oldDeepSeekModel)
    {
        return provider switch
        {
            "openai" => "gpt-4o-mini",
            "claude" => "claude-3-5-haiku-latest",
            "custom-openai" => string.IsNullOrWhiteSpace(oldDeepSeekModel) ? "gpt-4o-mini" : oldDeepSeekModel.Trim(),
            _ => string.IsNullOrWhiteSpace(oldDeepSeekModel) ? "deepseek-v4-flash" : oldDeepSeekModel.Trim()
        };
    }

    private static string NormalizeLlmBaseUrl(string provider, string baseUrl)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            return baseUrl.Trim().TrimEnd('/');
        }

        return provider switch
        {
            "openai" => "https://api.openai.com/v1",
            "claude" => "https://api.anthropic.com",
            "custom-openai" => "https://api.openai.com/v1",
            _ => "https://api.deepseek.com/v1"
        };
    }
}
