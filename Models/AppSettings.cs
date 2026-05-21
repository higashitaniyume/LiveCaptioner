namespace LiveCaptioner.Models;

public sealed class AppSettings
{
    public string Language { get; set; } = "zh-CN";
    public string TargetLanguage { get; set; } = "zh-CN";
    public string DeepSeekApiKey { get; set; } = string.Empty;
    public string DeepSeekModel { get; set; } = "deepseek-v4-flash";
    public string LlmProvider { get; set; } = "deepseek";
    public string LlmApiKey { get; set; } = string.Empty;
    public string LlmBaseUrl { get; set; } = "https://api.deepseek.com/v1";
    public string LlmModel { get; set; } = "deepseek-v4-flash";
    public string AsrProvider { get; set; } = "assemblyai";
    public string AssemblyAiApiKey { get; set; } = string.Empty;
    public string AssemblyAiSpeechModel { get; set; } = "universal-streaming-multilingual";
    public string AzureSpeechKey { get; set; } = string.Empty;
    public string AzureSpeechRegion { get; set; } = string.Empty;
    public string AzureSpeechLanguage { get; set; } = "en-US";
    public string GoogleCredentialsPath { get; set; } = string.Empty;
    public string GoogleSpeechLanguage { get; set; } = "en-US";
    public string CustomSttWebSocketUrl { get; set; } = string.Empty;
    public string CustomSttApiKey { get; set; } = string.Empty;
    public bool AutoTranslate { get; set; } = true;
    public bool BilingualComparison { get; set; } = true;
    public bool SaveHistory { get; set; } = true;
    public bool ClickThrough { get; set; }
    public double FontSize { get; set; } = 32;
    public double BackgroundOpacity { get; set; } = 0.65;
    public string WhisperModel { get; set; } = "tiny";
    public string AsrBackend { get; set; } = "auto";
}
