using System.IO;
using System.Text;
using LiveCaptioner.Localization;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace LiveCaptioner.Services;

public sealed class AsrService : IAsrService
{
    private readonly RollingAudioBuffer _audioBuffer;
    private readonly string _modelPath;
    private readonly GgmlType _modelType;
    private readonly string _modelName;
    private readonly string _backend;
    private readonly TimeSpan _windowDuration = TimeSpan.FromSeconds(2.6);
    private readonly TimeSpan _strideDuration = TimeSpan.FromSeconds(0.8);
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private string _lastNormalizedText = string.Empty;
    private bool _disposed;

    public AsrService(RollingAudioBuffer audioBuffer, string modelPath, string modelName, string backend)
    {
        _audioBuffer = audioBuffer;
        _modelPath = modelPath;
        _modelName = NormalizeModelName(modelName);
        _modelType = ToGgmlType(_modelName);
        _backend = NormalizeBackend(backend);
    }

    public event EventHandler<string>? TranscriptReady;
    public event EventHandler<string>? StatusChanged;

    public Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_worker is not null)
        {
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is null || _worker is null)
        {
            return;
        }

        await _cts.CancelAsync();

        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _worker = null;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            StatusChanged?.Invoke(this, LocalizationManager.T("AsrReady"));

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_audioBuffer.TryReadWindow(_windowDuration, _strideDuration, out var pcm))
                {
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await RecognizeAsync(pcm, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, LocalizationManager.Format("AsrServiceStopped", ex.Message));
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_modelPath)!);

        if (!File.Exists(_modelPath))
        {
            StatusChanged?.Invoke(this, LocalizationManager.Format("DownloadingWhisperModel", _modelName));
            await using var modelStream = await WhisperGgmlDownloader.Default
                .GetGgmlModelAsync(_modelType)
                .ConfigureAwait(false);
            await using var fileStream = File.Create(_modelPath);
            await modelStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
        }

        _factory = CreateFactoryWithFallback();
        StatusChanged?.Invoke(this, LocalizationManager.Format("AsrRuntime", WhisperFactory.GetRuntimeInfo()));
        _processor = _factory.CreateBuilder()
            .WithLanguageDetection()
            .WithThreads(Math.Max(1, Environment.ProcessorCount - 1))
            .WithNoSpeechThreshold(0.65f)
            .WithMaxSegmentLength(60)
            .WithSingleSegment()
            .WithPrompt(LocalizationManager.T("WhisperPrompt"))
            .Build();
    }

    private async Task RecognizeAsync(byte[] pcm16Mono16k, CancellationToken cancellationToken)
    {
        if (_processor is null)
        {
            return;
        }

        var samples = ConvertPcm16ToFloat(pcm16Mono16k);
        var builder = new StringBuilder();

        try
        {
            await foreach (var segment in _processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(segment.Text))
                {
                    builder.Append(segment.Text.Trim()).Append(' ');
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, LocalizationManager.Format("AsrFailed", ex.Message));
            return;
        }

        var text = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusChanged?.Invoke(this, LocalizationManager.T("WaitingForSpeech"));
            return;
        }

        var normalized = NormalizeForDedup(text);
        if (normalized.Length < 2 || normalized == _lastNormalizedText)
        {
            return;
        }

        _lastNormalizedText = normalized;
        TranscriptReady?.Invoke(this, text);
    }

    private static float[] ConvertPcm16ToFloat(byte[] pcm)
    {
        var samples = new float[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = BitConverter.ToInt16(pcm, i * 2);
            samples[i] = sample / 32768f;
        }

        return samples;
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

    private static string NormalizeModelName(string modelName)
    {
        var normalized = string.IsNullOrWhiteSpace(modelName)
            ? "tiny"
            : modelName.Trim().ToLowerInvariant();

        return normalized is "tiny" or "base" or "small" ? normalized : "tiny";
    }

    private static GgmlType ToGgmlType(string modelName)
    {
        return modelName switch
        {
            "small" => GgmlType.Small,
            "base" => GgmlType.Base,
            _ => GgmlType.Tiny
        };
    }

    private static string NormalizeBackend(string backend)
    {
        var normalized = string.IsNullOrWhiteSpace(backend)
            ? "auto"
            : backend.Trim().ToLowerInvariant();

        return normalized is "auto" or "cuda" or "vulkan" or "cpu" ? normalized : "auto";
    }

    private static void ConfigureRuntime(string backend)
    {
        RuntimeOptions.RuntimeLibraryOrder = backend switch
        {
            "cuda" => [RuntimeLibrary.Cuda, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            "vulkan" => [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            "cpu" => [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            _ => [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
        };
    }

    private WhisperFactory CreateFactoryWithFallback()
    {
        try
        {
            ConfigureRuntime(_backend);
            return WhisperFactory.FromPath(_modelPath, new WhisperFactoryOptions
            {
                UseGpu = _backend != "cpu",
                GpuDevice = 0
            });
        }
        catch (Exception ex) when (_backend != "cpu")
        {
            StatusChanged?.Invoke(this, LocalizationManager.Format("GpuBackendFallback", ex.Message));
            ConfigureRuntime("cpu");
            return WhisperFactory.FromPath(_modelPath, new WhisperFactoryOptions
            {
                UseGpu = false
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);

        if (_processor is not null)
        {
            await _processor.DisposeAsync().ConfigureAwait(false);
        }

        _factory?.Dispose();
        _disposed = true;
    }
}
