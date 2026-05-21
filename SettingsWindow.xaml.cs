using System.Text.Json;
using System.Windows;

using LiveCaptioner.Models;

namespace LiveCaptioner;

public partial class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Settings = Clone(settings);
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
        SelectCombo(LlmProviderBox, Settings.LlmProvider);
        LlmApiKeyBox.Password = Settings.LlmApiKey;
        LlmBaseUrlBox.Text = Settings.LlmBaseUrl;
        LlmModelBox.Text = Settings.LlmModel;

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

        AutoTranslateBox.IsChecked = Settings.AutoTranslate;
        BilingualComparisonBox.IsChecked = Settings.BilingualComparison;
        SaveHistoryBox.IsChecked = Settings.SaveHistory;
        ClickThroughBox.IsChecked = Settings.ClickThrough;
        FontSizeSlider.Value = Settings.FontSize;
        OpacitySlider.Value = Settings.BackgroundOpacity;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Settings.LlmProvider = ReadCombo(LlmProviderBox, "deepseek");
        Settings.LlmApiKey = LlmApiKeyBox.Password.Trim();
        Settings.LlmBaseUrl = string.IsNullOrWhiteSpace(LlmBaseUrlBox.Text)
            ? GetDefaultLlmBaseUrl(Settings.LlmProvider)
            : LlmBaseUrlBox.Text.Trim().TrimEnd('/');
        Settings.LlmModel = string.IsNullOrWhiteSpace(LlmModelBox.Text)
            ? GetDefaultLlmModel(Settings.LlmProvider)
            : LlmModelBox.Text.Trim();
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

        Settings.AutoTranslate = AutoTranslateBox.IsChecked == true;
        Settings.BilingualComparison = BilingualComparisonBox.IsChecked == true;
        Settings.SaveHistory = SaveHistoryBox.IsChecked == true;
        Settings.ClickThrough = ClickThroughBox.IsChecked == true;
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
