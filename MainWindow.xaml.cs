using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LiveCaptioner.Localization;
using LiveCaptioner.Models;
using LiveCaptioner.Native;
using LiveCaptioner.Services;

namespace LiveCaptioner;

public partial class MainWindow : Window
{
    private readonly AppSettingsService _settingsService = new();
    private readonly ObservableCollection<HistoryListItem> _inlineHistoryItems = [];
    private readonly ObservableCollection<CaptionConversationInfo> _conversationItems = [];
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private CancellationTokenSource? _clickThroughGraceCts;
    private AppSettings _settings = new();
    private RollingAudioBuffer? _audioBuffer;
    private AudioCaptureService? _audioCaptureService;
    private IAsrService? _asrService;
    private LlmSubtitleService? _llmService;
    private CaptionHistoryService? _historyService;
    private SettingsWindow? _settingsWindow;
    private bool _isUpdatingConversationSelection;
    private bool _isShuttingDown;
    private bool _hotKeyRegistered;

    public MainWindow()
    {
        InitializeComponent();
        InlineHistoryList.ItemsSource = _inlineHistoryItems;
        ConversationBox.ItemsSource = _conversationItems;
        SourceInitialized += MainWindow_SourceInitialized;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = _settingsService.Load();
        LocalizationManager.ApplyCulture(_settings.Language);
        InitializeTrayIcon();
        ApplyVisualSettings();
        if (_settings.ClickThrough)
        {
            EnableClickThroughWithGrace(TimeSpan.FromSeconds(5), save: false);
        }

        _audioBuffer = new RollingAudioBuffer(TimeSpan.FromSeconds(18));
        _audioCaptureService = new AudioCaptureService(_audioBuffer);
        _audioCaptureService.PcmAudioAvailable += AudioCaptureServiceOnPcmAudioAvailable;
        _historyService = new CaptionHistoryService(_settingsService.AppDataDirectory);
        _historyService.StartNewConversation();
        _llmService = new LlmSubtitleService
        {
            Provider = _settings.LlmProvider,
            ApiKey = _settings.LlmApiKey,
            BaseUrl = _settings.LlmBaseUrl,
            AutoTranslate = _settings.AutoTranslate,
            BilingualComparison = _settings.BilingualComparison,
            Model = _settings.LlmModel,
            TargetLanguage = _settings.TargetLanguage,
            TranslationProvider = _settings.TranslationProvider,
            GoogleTranslateApiKey = _settings.GoogleTranslateApiKey,
            MicrosoftTranslatorKey = _settings.MicrosoftTranslatorKey,
            MicrosoftTranslatorRegion = _settings.MicrosoftTranslatorRegion,
            MinTextLength = _settings.MinTextLength,
            DebounceDelayMs = _settings.DebounceDelayMs
        };
        _inlineHistoryItems.Clear();
        await RefreshConversationListAsync(selectCurrent: true);

        _asrService = CreateAsrService();

        _audioCaptureService.StatusChanged += (_, message) => SetStatus(message);
        _llmService.StatusChanged += (_, message) => SetStatus(message);
        AttachAsrEvents(_asrService);
        _llmService.SubtitleReady += async (_, subtitle) =>
        {
            SetCaption(subtitle);
            AddInlineHistory(subtitle);
            await SaveHistoryAsync(subtitle);
        };

        await StartPipelineAsync();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WndProc);

        _hotKeyRegistered = WindowNativeMethods.RegisterRestoreHotKey(this);
        if (!_hotKeyRegistered)
        {
            SetStatus(LocalizationManager.T("HotKeyRegistrationFailed"));
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WindowNativeMethods.WmHotKey && wParam.ToInt32() == WindowNativeMethods.HotKeyId)
        {
            DisableClickThrough(LocalizationManager.T("RestoredByHotkey"), showSettings: false);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private async Task StartPipelineAsync()
    {
        try
        {
            if (_asrService is not null)
            {
                await _asrService.StartAsync();
            }

            _audioCaptureService?.Start();
            StartStopMenuItem.Header = LocalizationManager.T("StopCapture");
        }
        catch (Exception ex)
        {
            SetStatus(LocalizationManager.Format("StartFailed", ex.Message));
            SetCaption(BilingualSubtitle.Raw(LocalizationManager.T("StartupFailedCaption")));
        }
    }

    private async Task StopPipelineAsync()
    {
        _audioCaptureService?.Stop();
        if (_asrService is not null)
        {
            await _asrService.StopAsync();
        }

        StartStopMenuItem.Header = LocalizationManager.T("StartCapture");
    }

    private void CaptionSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private async void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await OpenSettingsWindowAsync();
    }

    private async void HistoryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryPanel.Visibility == Visibility.Visible)
        {
            HistoryPanel.Visibility = Visibility.Collapsed;
            return;
        }

        await RefreshHistoryAsync();
        HistoryPanel.Visibility = Visibility.Visible;
    }

    private async void NewConversationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await StartNewConversationAsync();
    }

    private async void StartStopMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_audioCaptureService?.IsRunning == true)
        {
            await StopPipelineAsync();
        }
        else
        {
            await StartPipelineAsync();
        }
    }

    private async void ClickThroughMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (ClickThroughMenuItem.IsChecked)
        {
            EnableClickThroughWithGrace(TimeSpan.FromSeconds(3), save: false);
        }
        else
        {
            DisableClickThrough(LocalizationManager.T("ClickThroughDisabled"), showSettings: false);
        }

        await SaveSettingsAsync();
    }

    private async void RefreshHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshHistoryAsync();
    }

    private async void NewConversationButton_Click(object sender, RoutedEventArgs e)
    {
        await StartNewConversationAsync();
    }

    private async void ConversationBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isUpdatingConversationSelection || _historyService is null || ConversationBox.SelectedItem is not CaptionConversationInfo conversation)
        {
            return;
        }

        try
        {
            _historyService.SwitchConversation(conversation.Id);
            HistoryPathText.Text = _historyService.CurrentConversationPath;
            await LoadCurrentConversationToInlineAsync();
            await RefreshHistoryAsync();
            SetStatus(LocalizationManager.Format("SwitchedConversation", conversation.DisplayName));
        }
        catch (Exception ex)
        {
            SetStatus(LocalizationManager.Format("SwitchConversationFailed", ex.Message));
        }
    }

    private void CloseHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        HistoryPanel.Visibility = Visibility.Collapsed;
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void InitializeTrayIcon()
    {
        if (_notifyIcon is not null)
        {
            return;
        }

        var contextMenu = new System.Windows.Forms.ContextMenuStrip();
        contextMenu.Items.Add(LocalizationManager.T("RestoreWindowInteraction"), null, (_, _) =>
            Dispatcher.Invoke(() => DisableClickThrough(LocalizationManager.T("RestoredFromTray"), showSettings: false)));
        contextMenu.Items.Add(LocalizationManager.T("ToggleClickThrough"), null, async (_, _) =>
            await Dispatcher.InvokeAsync(async () =>
            {
                if (_settings.ClickThrough)
                {
                    DisableClickThrough(LocalizationManager.T("ClickThroughDisabled"), showSettings: false);
                }
                else
                {
                    EnableClickThroughWithGrace(TimeSpan.FromSeconds(3), save: false);
                }

                await SaveSettingsAsync();
            }));
        contextMenu.Items.Add(LocalizationManager.T("Settings"), null, (_, _) =>
            Dispatcher.Invoke(async () =>
            {
                DisableClickThrough(LocalizationManager.T("RestoredOpenSettings"), showSettings: false);
                await OpenSettingsWindowAsync();
            }));
        contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        contextMenu.Items.Add(LocalizationManager.T("Exit"), null, (_, _) => Dispatcher.Invoke(Close));

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = LocalizationManager.T("TrayText"),
            ContextMenuStrip = contextMenu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) =>
            Dispatcher.Invoke(() => DisableClickThrough(LocalizationManager.T("RestoredFromTray"), showSettings: false));
    }

    private void EnableClickThroughWithGrace(TimeSpan delay, bool save)
    {
        _clickThroughGraceCts?.Cancel();
        _clickThroughGraceCts?.Dispose();
        _clickThroughGraceCts = new CancellationTokenSource();

        _settings.ClickThrough = true;
        SyncClickThroughControls(true);
        BringOverlayToFront();
        WindowNativeMethods.SetClickThrough(this, false);
        SetStatus(LocalizationManager.Format("ClickThroughEnableCountdown", Math.Ceiling(delay.TotalSeconds)));

        var token = _clickThroughGraceCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!_settings.ClickThrough || token.IsCancellationRequested)
                    {
                        return;
                    }

                    WindowNativeMethods.SetClickThrough(this, true);
                    SetStatus(LocalizationManager.T("ClickThroughEnabled"));
                    _notifyIcon?.ShowBalloonTip(
                        2500,
                        LocalizationManager.T("ClickThroughBalloonTitle"),
                        LocalizationManager.T("ClickThroughBalloonText"),
                        System.Windows.Forms.ToolTipIcon.Info);
                });
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        if (save)
        {
            _ = SaveSettingsAsync();
        }
    }

    private void DisableClickThrough(string status, bool showSettings)
    {
        _clickThroughGraceCts?.Cancel();
        _settings.ClickThrough = false;
        SyncClickThroughControls(false);
        WindowNativeMethods.SetClickThrough(this, false);
        BringOverlayToFront();

        if (showSettings)
        {
            _ = OpenSettingsWindowAsync();
        }

        SetStatus(status);
        _ = SaveSettingsAsync();
    }

    private void SyncClickThroughControls(bool enabled)
    {
        ClickThroughMenuItem.IsChecked = enabled;
    }

    private void BringOverlayToFront()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = false;
        Topmost = true;
    }

    private async Task OpenSettingsWindowAsync()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var window = new SettingsWindow(_settings)
        {
            Owner = this
        };

        _settingsWindow = window;
        try
        {
            if (window.ShowDialog() == true)
            {
                await ApplyUpdatedSettingsAsync(window.Settings);
            }
        }
        finally
        {
            _settingsWindow = null;
        }
    }

    private async Task ApplyUpdatedSettingsAsync(AppSettings updatedSettings)
    {
        var previousAsrProvider = _settings.AsrProvider;
        var previousAssemblyAiApiKey = _settings.AssemblyAiApiKey;
        var previousAssemblyAiModel = _settings.AssemblyAiSpeechModel;
        var previousAzureKey = _settings.AzureSpeechKey;
        var previousAzureRegion = _settings.AzureSpeechRegion;
        var previousAzureLanguage = _settings.AzureSpeechLanguage;
        var previousGoogleCredentialsPath = _settings.GoogleCredentialsPath;
        var previousGoogleLanguage = _settings.GoogleSpeechLanguage;
        var previousWhisperModel = _settings.WhisperModel;
        var previousAsrBackend = _settings.AsrBackend;
        var previousCustomSttUrl = _settings.CustomSttWebSocketUrl;
        var previousCustomSttApiKey = _settings.CustomSttApiKey;
        var previousCustomSttAuthHeader = _settings.CustomSttAuthHeader;
        var previousCustomSttTranscriptField = _settings.CustomSttTranscriptField;
        var previousLanguage = _settings.Language;

        _settings = updatedSettings;
        if (_llmService is not null)
        {
            _llmService.Provider = _settings.LlmProvider;
            _llmService.ApiKey = _settings.LlmApiKey;
            _llmService.BaseUrl = _settings.LlmBaseUrl;
            _llmService.AutoTranslate = _settings.AutoTranslate;
            _llmService.BilingualComparison = _settings.BilingualComparison;
            _llmService.Model = _settings.LlmModel;
            _llmService.TargetLanguage = _settings.TargetLanguage;
            _llmService.TranslationProvider = _settings.TranslationProvider;
            _llmService.GoogleTranslateApiKey = _settings.GoogleTranslateApiKey;
            _llmService.MicrosoftTranslatorKey = _settings.MicrosoftTranslatorKey;
            _llmService.MicrosoftTranslatorRegion = _settings.MicrosoftTranslatorRegion;
            _llmService.MinTextLength = _settings.MinTextLength;
            _llmService.DebounceDelayMs = _settings.DebounceDelayMs;
        }

        ApplyVisualSettings();
        if (_settings.ClickThrough)
        {
            EnableClickThroughWithGrace(TimeSpan.FromSeconds(3), save: false);
        }
        else
        {
            DisableClickThrough(LocalizationManager.T("ClickThroughDisabled"), showSettings: false);
        }

        await SaveSettingsAsync();

        if (!string.Equals(previousAsrProvider, _settings.AsrProvider, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(previousAssemblyAiApiKey, _settings.AssemblyAiApiKey, StringComparison.Ordinal) ||
            !string.Equals(previousAssemblyAiModel, _settings.AssemblyAiSpeechModel, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(previousAzureKey, _settings.AzureSpeechKey, StringComparison.Ordinal) ||
            !string.Equals(previousAzureRegion, _settings.AzureSpeechRegion, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(previousAzureLanguage, _settings.AzureSpeechLanguage, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(previousGoogleCredentialsPath, _settings.GoogleCredentialsPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(previousGoogleLanguage, _settings.GoogleSpeechLanguage, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(previousWhisperModel, _settings.WhisperModel, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(previousCustomSttUrl, _settings.CustomSttWebSocketUrl, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(previousCustomSttApiKey, _settings.CustomSttApiKey, StringComparison.Ordinal) ||
            !string.Equals(previousCustomSttAuthHeader, _settings.CustomSttAuthHeader, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(previousCustomSttTranscriptField, _settings.CustomSttTranscriptField, StringComparison.OrdinalIgnoreCase))
        {
            await RestartAsrAsync();
        }
        else if (!string.Equals(previousAsrBackend, _settings.AsrBackend, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(LocalizationManager.T("AsrBackendSavedRequiresRestart"));
        }

        if (!string.Equals(previousLanguage, _settings.Language, StringComparison.OrdinalIgnoreCase))
        {
            LocalizationManager.ApplyCulture(_settings.Language);
            SetStatus(LocalizationManager.T("LanguageRestartRequired"));
            return;
        }

        SetStatus(LocalizationManager.T("SettingsSaved"));
    }

    private void ApplyVisualSettings()
    {
        PrimaryCaptionText.FontSize = _settings.FontSize;
        PrimaryCaptionText.LineHeight = _settings.FontSize * 1.32;
        SecondaryCaptionText.FontSize = Math.Max(14, _settings.FontSize * 0.72);
        SecondaryCaptionText.LineHeight = Math.Max(19, _settings.FontSize * 0.96);
        SetBackgroundOpacity(_settings.BackgroundOpacity);
    }

    private void SetBackgroundOpacity(double opacity)
    {
        var alpha = (byte)Math.Round(Math.Clamp(opacity, 0.15, 0.9) * 255);
        CaptionSurface.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, 0, 0, 0));
    }

    private IAsrService CreateAsrService()
    {
        if (_audioBuffer is null)
        {
            throw new InvalidOperationException(LocalizationManager.T("AudioBufferNotInitialized"));
        }

        if (string.Equals(_settings.AsrProvider, "assemblyai", StringComparison.OrdinalIgnoreCase))
        {
            return new AssemblyAiStreamingAsrService(_settings.AssemblyAiApiKey, _settings.AssemblyAiSpeechModel);
        }

        if (string.Equals(_settings.AsrProvider, "azure", StringComparison.OrdinalIgnoreCase))
        {
            return new AzureSpeechAsrService(_settings.AzureSpeechKey, _settings.AzureSpeechRegion, _settings.AzureSpeechLanguage);
        }

        if (string.Equals(_settings.AsrProvider, "google", StringComparison.OrdinalIgnoreCase))
        {
            return new GoogleSpeechAsrService(_settings.GoogleCredentialsPath, _settings.GoogleSpeechLanguage);
        }

        if (string.Equals(_settings.AsrProvider, "custom", StringComparison.OrdinalIgnoreCase))
        {
            return new CustomStreamingAsrService(
                _settings.CustomSttWebSocketUrl,
                _settings.CustomSttApiKey,
                _settings.CustomSttAuthHeader,
                _settings.CustomSttTranscriptField);
        }

        var modelPath = Path.Combine(
            _settingsService.AppDataDirectory,
            "models",
            $"ggml-{_settings.WhisperModel}.bin");

        return new AsrService(_audioBuffer, modelPath, _settings.WhisperModel, _settings.AsrBackend);
    }

    private void AttachAsrEvents(IAsrService asrService)
    {
        asrService.StatusChanged += (_, message) => SetStatus(message);
        asrService.TranscriptReady += (_, text) => _llmService?.QueueText(text);
    }

    private void AudioCaptureServiceOnPcmAudioAvailable(object? sender, byte[] pcm)
    {
        if (_asrService is IStreamingAudioConsumer streamingAsr)
        {
            streamingAsr.AddAudio(pcm);
        }
    }

    private async Task RestartAsrAsync()
    {
        var wasRunning = _audioCaptureService?.IsRunning == true;
        await StopPipelineAsync();

        if (_asrService is not null)
        {
            await _asrService.DisposeAsync();
        }

        _asrService = CreateAsrService();
        AttachAsrEvents(_asrService);

        if (wasRunning)
        {
            await StartPipelineAsync();
        }
        else
        {
            SetStatus(LocalizationManager.Format("AsrProviderSwitched", _settings.AsrProvider));
        }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await _settingsService.SaveAsync(_settings);
        }
        catch (Exception ex)
        {
            SetStatus(LocalizationManager.Format("SaveSettingsFailed", ex.Message));
        }
    }

    private async Task SaveHistoryAsync(BilingualSubtitle subtitle)
    {
        if (!_settings.SaveHistory || _historyService is null)
        {
            return;
        }

        try
        {
            await _historyService.AppendAsync(CaptionHistoryEntry.FromSubtitle(subtitle)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetStatus(LocalizationManager.Format("SaveHistoryFailed", ex.Message));
        }
    }

    private async Task RefreshHistoryAsync()
    {
        if (_historyService is null)
        {
            return;
        }

        try
        {
            var entries = await _historyService.LoadCurrentRecentAsync(80).ConfigureAwait(false);
            var items = entries.Select(HistoryListItem.FromEntry).ToList();
            Dispatcher.Invoke(() =>
            {
                HistoryPathText.Text = _historyService.CurrentConversationPath;
                HistoryList.ItemsSource = items;
            });
        }
        catch (Exception ex)
        {
            SetStatus(LocalizationManager.Format("ReadHistoryFailed", ex.Message));
        }
    }

    private void SetCaption(BilingualSubtitle subtitle)
    {
        Dispatcher.Invoke(() =>
        {
            PrimaryCaptionText.Text = subtitle.PrimaryText;

            if (_settings.BilingualComparison && subtitle.HasTranslation)
            {
                SecondaryCaptionText.Text = subtitle.SecondaryText;
                SecondaryCaptionText.Visibility = Visibility.Visible;
            }
            else
            {
                SecondaryCaptionText.Text = string.Empty;
                SecondaryCaptionText.Visibility = Visibility.Collapsed;
            }
        });
    }

    private async Task LoadCurrentConversationToInlineAsync()
    {
        if (_historyService is null)
        {
            return;
        }

        try
        {
            var entries = await _historyService.LoadCurrentRecentAsync(10).ConfigureAwait(false);
            Dispatcher.Invoke(() =>
            {
                _inlineHistoryItems.Clear();
                foreach (var entry in entries)
                {
                    _inlineHistoryItems.Add(HistoryListItem.FromEntry(entry));
                }
            });
        }
        catch (Exception ex)
        {
            SetStatus(LocalizationManager.Format("LoadInlineHistoryFailed", ex.Message));
        }
    }

    private async Task StartNewConversationAsync()
    {
        if (_historyService is null)
        {
            return;
        }

        var conversation = _historyService.StartNewConversation();
        Dispatcher.Invoke(() =>
        {
            _inlineHistoryItems.Clear();
            HistoryList.ItemsSource = null;
            PrimaryCaptionText.Text = LocalizationManager.T("NewConversationCaption");
            SecondaryCaptionText.Text = string.Empty;
            SecondaryCaptionText.Visibility = Visibility.Collapsed;
        });

        await RefreshConversationListAsync(selectCurrent: true);
        SetStatus(LocalizationManager.Format("NewConversationStatus", conversation.DisplayName));
    }

    private Task RefreshConversationListAsync(bool selectCurrent)
    {
        if (_historyService is null)
        {
            return Task.CompletedTask;
        }

        var conversations = _historyService.GetConversations();
        Dispatcher.Invoke(() =>
        {
            _isUpdatingConversationSelection = true;
            try
            {
                _conversationItems.Clear();
                foreach (var conversation in conversations)
                {
                    _conversationItems.Add(conversation);
                }

                if (selectCurrent)
                {
                    ConversationBox.SelectedItem = _conversationItems.FirstOrDefault(item =>
                        string.Equals(item.Id, _historyService.CurrentConversation.Id, StringComparison.OrdinalIgnoreCase));
                }

                HistoryPathText.Text = _historyService.CurrentConversationPath;
            }
            finally
            {
                _isUpdatingConversationSelection = false;
            }
        });

        return Task.CompletedTask;
    }

    private void AddInlineHistory(BilingualSubtitle subtitle)
    {
        Dispatcher.Invoke(() =>
        {
            _inlineHistoryItems.Insert(0, HistoryListItem.FromSubtitle(subtitle));
            while (_inlineHistoryItems.Count > 12)
            {
                _inlineHistoryItems.RemoveAt(_inlineHistoryItems.Count - 1);
            }

            InlineHistoryScrollViewer.ScrollToTop();
        });
    }

    private void SetStatus(string message)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = message;
        });
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isShuttingDown)
        {
            return;
        }

        e.Cancel = true;
        _isShuttingDown = true;
        await ShutdownAsync();
        await Dispatcher.BeginInvoke(new Action(() =>
        {
            Closing -= Window_Closing;
            Close();
        }), DispatcherPriority.ApplicationIdle);
    }

    private async Task ShutdownAsync()
    {
        try
        {
            await StopPipelineAsync();
            if (_asrService is not null)
            {
                await _asrService.DisposeAsync();
            }

            _audioCaptureService?.Dispose();
            _llmService?.Dispose();
            _clickThroughGraceCts?.Cancel();
            _clickThroughGraceCts?.Dispose();
            _notifyIcon?.Dispose();

            if (_hotKeyRegistered)
            {
                WindowNativeMethods.UnregisterRestoreHotKey(this);
            }
        }
        catch
        {
            // Shutdown should be quiet; all services are best-effort disposed above.
        }
    }

    private sealed class HistoryListItem
    {
        public string TimestampText { get; init; } = string.Empty;
        public string PrimaryText { get; init; } = string.Empty;
        public string SecondaryText { get; init; } = string.Empty;

        public static HistoryListItem FromSubtitle(BilingualSubtitle subtitle)
        {
            return new HistoryListItem
            {
                TimestampText = subtitle.CreatedAt.ToLocalTime().ToString("HH:mm:ss"),
                PrimaryText = subtitle.PrimaryText,
                SecondaryText = subtitle.SecondaryText
            };
        }

        public static HistoryListItem FromEntry(CaptionHistoryEntry entry)
        {
            var primary = string.IsNullOrWhiteSpace(entry.CorrectedText)
                ? entry.RawText
                : entry.CorrectedText;

            return new HistoryListItem
            {
                TimestampText = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss"),
                PrimaryText = primary,
                SecondaryText = entry.TranslatedText
            };
        }
    }
}
