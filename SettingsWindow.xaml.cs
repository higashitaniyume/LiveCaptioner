using System.Text.Json;
using System.Windows;

using LiveCaptioner.Localization;
using LiveCaptioner.Models;

namespace LiveCaptioner;

public partial class SettingsWindow : Window
{
    private readonly string _originalLanguage;

    private static readonly Dictionary<string, List<string>> ProviderModels = new()
    {
        ["deepseek"] = ["deepseek-v4-flash", "deepseek-v4-1", "deepseek-v4-pro", "deepseek-v3", "deepseek-r1"],
        ["openai"] = ["gpt-4o-mini", "gpt-4o", "gpt-4.1", "gpt-4.1-mini", "gpt-4.1-nano", "gpt-4.5-preview"],
        ["claude"] = ["claude-haiku-4-5", "claude-sonnet-4-6", "claude-opus-4-7", "claude-3-5-haiku-latest"],
    };

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Settings = Clone(settings);
        _originalLanguage = settings.Language;
        LoadSettings();
        ApplySelectedPage();
    }

    public AppSettings Settings { get; }

    private static AppSettings Clone(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings);
        return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }

    private void LoadSettings()
    {
        SelectLanguage(Settings.Language);

        // Translation page
        SelectComboByTag(TranslationProviderBox, Settings.TranslationProvider);
        ApplyTranslationProviderPanels(Settings.TranslationProvider);
        SelectTargetLanguage(Settings.TargetLanguage);
        SelectCombo(LlmProviderBox, Settings.LlmProvider);
        LlmApiKeyBox.Password = Settings.LlmApiKey;
        LlmBaseUrlBox.Text = Settings.LlmBaseUrl;
        LlmModelBox.Text = Settings.LlmModel;
        PopulateModelSuggestions(Settings.LlmProvider);
        GoogleTranslateKeyBox.Password = Settings.GoogleTranslateApiKey;
        MicrosoftTranslatorKeyBox.Password = Settings.MicrosoftTranslatorKey;
        MicrosoftTranslatorRegionBox.Text = Settings.MicrosoftTranslatorRegion;

        SelectCombo(AsrProviderBox, Settings.AsrProvider);
        AssemblyAiKeyBox.Password = Settings.AssemblyAiApiKey;
        AssemblyAiModelBox.Text = Settings.AssemblyAiSpeechModel;
        AzureSpeechKeyBox.Password = Settings.AzureSpeechKey;
        AzureSpeechRegionBox.Text = Settings.AzureSpeechRegion;
        AzureSpeechLanguageBox.Text = Settings.AzureSpeechLanguage;
        GoogleCredentialsPathBox.Text = Settings.GoogleCredentialsPath;
        GoogleSpeechLanguageBox.Text = Settings.GoogleSpeechLanguage;
        SelectCombo(WhisperModelBox, Settings.WhisperModel);
        SelectCombo(AsrBackendBox, Settings.AsrBackend);
        CustomSttUrlBox.Text = Settings.CustomSttWebSocketUrl;
        CustomSttApiKeyBox.Password = Settings.CustomSttApiKey;
        CustomSttAuthHeaderBox.Text = Settings.CustomSttAuthHeader;
        CustomSttTranscriptFieldBox.Text = Settings.CustomSttTranscriptField;

        AutoTranslateBox.IsChecked = Settings.AutoTranslate;
        BilingualComparisonBox.IsChecked = Settings.BilingualComparison;
        SaveHistoryBox.IsChecked = Settings.SaveHistory;
        ClickThroughBox.IsChecked = Settings.ClickThrough;
        MinTextLengthBox.Text = Settings.MinTextLength.ToString();
        DebounceDelayBox.Text = Settings.DebounceDelayMs.ToString();
        FontSizeSlider.Value = Settings.FontSize;
        OpacitySlider.Value = Settings.BackgroundOpacity;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var newLanguage = ReadLanguage();
        if (!string.Equals(_originalLanguage, newLanguage, StringComparison.OrdinalIgnoreCase))
        {
            System.Windows.MessageBox.Show(
                LocalizationManager.T("LanguageChangedRestartMessage"),
                LocalizationManager.T("LanguageChangedRestartTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        Settings.Language = newLanguage;

        // Translation
        Settings.TranslationProvider = ReadComboByTag(TranslationProviderBox, "llm");
        Settings.TargetLanguage = ReadTargetLanguage();
        Settings.LlmProvider = ReadCombo(LlmProviderBox, "deepseek");
        Settings.LlmApiKey = LlmApiKeyBox.Password.Trim();
        Settings.LlmBaseUrl = string.IsNullOrWhiteSpace(LlmBaseUrlBox.Text)
            ? GetDefaultLlmBaseUrl(Settings.LlmProvider)
            : LlmBaseUrlBox.Text.Trim().TrimEnd('/');
        Settings.LlmModel = string.IsNullOrWhiteSpace(LlmModelBox.Text)
            ? GetDefaultLlmModel(Settings.LlmProvider)
            : LlmModelBox.Text.Trim();
        Settings.GoogleTranslateApiKey = GoogleTranslateKeyBox.Password.Trim();
        Settings.MicrosoftTranslatorKey = MicrosoftTranslatorKeyBox.Password.Trim();
        Settings.MicrosoftTranslatorRegion = MicrosoftTranslatorRegionBox.Text.Trim();
        Settings.DeepSeekApiKey = Settings.LlmApiKey;
        Settings.DeepSeekModel = Settings.LlmModel;

        Settings.AsrProvider = ReadCombo(AsrProviderBox, "assemblyai");
        Settings.AssemblyAiApiKey = AssemblyAiKeyBox.Password.Trim();
        Settings.AssemblyAiSpeechModel = string.IsNullOrWhiteSpace(AssemblyAiModelBox.Text)
            ? "universal-streaming-multilingual"
            : AssemblyAiModelBox.Text.Trim();
        Settings.AzureSpeechKey = AzureSpeechKeyBox.Password.Trim();
        Settings.AzureSpeechRegion = AzureSpeechRegionBox.Text.Trim();
        Settings.AzureSpeechLanguage = string.IsNullOrWhiteSpace(AzureSpeechLanguageBox.Text)
            ? "en-US"
            : AzureSpeechLanguageBox.Text.Trim();
        Settings.GoogleCredentialsPath = GoogleCredentialsPathBox.Text.Trim();
        Settings.GoogleSpeechLanguage = string.IsNullOrWhiteSpace(GoogleSpeechLanguageBox.Text)
            ? "en-US"
            : GoogleSpeechLanguageBox.Text.Trim();
        Settings.WhisperModel = ReadCombo(WhisperModelBox, "tiny");
        Settings.AsrBackend = ReadCombo(AsrBackendBox, "auto");
        Settings.CustomSttWebSocketUrl = CustomSttUrlBox.Text.Trim();
        Settings.CustomSttApiKey = CustomSttApiKeyBox.Password.Trim();
        Settings.CustomSttAuthHeader = CustomSttAuthHeaderBox.Text.Trim();
        Settings.CustomSttTranscriptField = CustomSttTranscriptFieldBox.Text.Trim();

        Settings.AutoTranslate = AutoTranslateBox.IsChecked == true;
        Settings.BilingualComparison = BilingualComparisonBox.IsChecked == true;
        Settings.SaveHistory = SaveHistoryBox.IsChecked == true;
        Settings.ClickThrough = ClickThroughBox.IsChecked == true;
        Settings.MinTextLength = ReadInt(MinTextLengthBox, 8);
        Settings.DebounceDelayMs = ReadInt(DebounceDelayBox, 800);
        Settings.FontSize = FontSizeSlider.Value;
        Settings.BackgroundOpacity = OpacitySlider.Value;

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void NavList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ApplySelectedPage();
    }

    private void TranslationProviderBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var provider = ReadComboByTag(TranslationProviderBox, "llm");
        ApplyTranslationProviderPanels(provider);
    }

    private void ApplyTranslationProviderPanels(string provider)
    {
        if (LlmSettingsPanel is null || GoogleSettingsPanel is null || MicrosoftSettingsPanel is null) return;

        LlmSettingsPanel.Visibility = provider == "llm" ? Visibility.Visible : Visibility.Collapsed;
        GoogleSettingsPanel.Visibility = provider == "google" ? Visibility.Visible : Visibility.Collapsed;
        MicrosoftSettingsPanel.Visibility = provider == "microsoft" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LlmProviderBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var provider = ReadCombo(LlmProviderBox, "deepseek");
        PopulateModelSuggestions(provider);
    }

    private void PopulateModelSuggestions(string provider)
    {
        var currentText = LlmModelBox.Text;

        LlmModelBox.Items.Clear();
        if (ProviderModels.TryGetValue(provider, out var models))
        {
            foreach (var model in models)
            {
                LlmModelBox.Items.Add(model);
            }
        }

        LlmModelBox.Text = currentText;
    }

    private void ApplySelectedPage()
    {
        if (GeneralPage is null || SttPage is null || LlmPage is null || AppearancePage is null || NavList is null)
        {
            return;
        }

        GeneralPage.Visibility = NavList.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        SttPage.Visibility = NavList.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        LlmPage.Visibility = NavList.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        AppearancePage.Visibility = NavList.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void SelectCombo(System.Windows.Controls.ComboBox comboBox, string value)
    {
        foreach (var item in comboBox.Items.OfType<System.Windows.Controls.ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private static string ReadCombo(System.Windows.Controls.ComboBox comboBox, string fallback)
    {
        return comboBox.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
               item.Content?.ToString() is { Length: > 0 } value
            ? value.ToLowerInvariant()
            : fallback;
    }

    private static void SelectComboByTag(System.Windows.Controls.ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items.OfType<System.Windows.Controls.ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private static string ReadComboByTag(System.Windows.Controls.ComboBox comboBox, string fallback)
    {
        return comboBox.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
               item.Tag?.ToString() is { Length: > 0 } value
            ? value.ToLowerInvariant()
            : fallback;
    }

    private void SelectLanguage(string language)
    {
        foreach (var item in LanguageBox.Items.OfType<System.Windows.Controls.ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), language, StringComparison.OrdinalIgnoreCase))
            {
                LanguageBox.SelectedItem = item;
                return;
            }
        }

        LanguageBox.SelectedIndex = 0;
    }

    private string ReadLanguage()
    {
        return LanguageBox.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
               item.Tag?.ToString() is { Length: > 0 } language
            ? language
            : LocalizationManager.Language;
    }

    private void SelectTargetLanguage(string language)
    {
        foreach (var item in TargetLanguageBox.Items.OfType<System.Windows.Controls.ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), language, StringComparison.OrdinalIgnoreCase))
            {
                TargetLanguageBox.SelectedItem = item;
                return;
            }
        }

        TargetLanguageBox.SelectedIndex = 0;
    }

    private static string ReadTargetLanguage(System.Windows.Controls.ComboBox comboBox)
    {
        return comboBox.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
               item.Tag?.ToString() is { Length: > 0 } language
            ? language
            : "zh-CN";
    }

    private string ReadTargetLanguage()
    {
        return ReadTargetLanguage(TargetLanguageBox);
    }

    private static int ReadInt(System.Windows.Controls.TextBox textBox, int fallback)
    {
        return int.TryParse(textBox.Text.Trim(), out var value) ? value : fallback;
    }

    private static string GetDefaultLlmBaseUrl(string provider)
    {
        return provider switch
        {
            "openai" => "https://api.openai.com/v1",
            "claude" => "https://api.anthropic.com",
            "custom-openai" => "https://api.openai.com/v1",
            _ => "https://api.deepseek.com/v1"
        };
    }

    private static string GetDefaultLlmModel(string provider)
    {
        return provider switch
        {
            "openai" => "gpt-4o-mini",
            "claude" => "claude-3-5-haiku-latest",
            "custom-openai" => "gpt-4o-mini",
            _ => "deepseek-v4-flash"
        };
    }
}
