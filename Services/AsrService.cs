using System.IO;
using System.Text;
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
            StatusChanged?.Invoke(this, "本地 Whisper ASR 已就绪");

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
            StatusChanged?.Invoke(this, $"ASR 服务停止：{ex.Message}");
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
            StatusChanged?.Invoke(this, $"正在下载 Whisper {_modelName} 模型，首次启动可能需要几分钟");
            await using var modelStream = await WhisperGgmlDownloader.Default
                .GetGgmlModelAsync(_modelType)
                .ConfigureAwait(false);
            await using var fileStream = File.Create(_modelPath);
            await modelStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
        }

        _factory = CreateFactoryWithFallback();
        StatusChanged?.Invoke(this, $"Whisper 运行时：{WhisperFactory.GetRuntimeInfo()}");
        _processor = _factory.CreateBuilder()
            .WithLanguageDetection()
            .WithThreads(Math.Max(1, Environment.ProcessorCount - 1))
            .WithNoSpeechThreshold(0.65f)
            .WithMaxSegmentLength(60)
            .WithSingleSegment()
            .WithPrompt("以下是 Windows 系统声音的连续实时字幕，请保持上下文连贯。")
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
            StatusChanged?.Invoke(this, $"ASR 识别失败：{ex.Message}");
            return;
        }

        var text = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusChanged?.Invoke(this, "等待可识别语音");
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
            StatusChanged?.Invoke(this, $"GPU 后端初始化失败，回退 CPU：{ex.Message}");
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
