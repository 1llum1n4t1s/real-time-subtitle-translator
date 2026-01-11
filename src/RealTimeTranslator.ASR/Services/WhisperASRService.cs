using System.Diagnostics;
using System.Management;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace RealTimeTranslator.ASR.Services;

/// <summary>
/// Whisper.netを使用したASRサービス
/// 二段構え構成：低遅延ASR（仮字幕）と高精度ASR（確定字幕）
/// </summary>
public class WhisperASRService : IASRService
{
    private const string ServiceName = "ASR";
    private const string FastModelLabel = "高速ASRモデル";
    private const string AccurateModelLabel = "高精度ASRモデル";
    private const string DefaultFastModelFileName = "ggml-small.bin";
    private const string DefaultAccurateModelFileName = "ggml-large-v3.bin";
    private const string DefaultFastModelDownloadUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin";
    private const string DefaultAccurateModelDownloadUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin";
    private WhisperProcessor? _fastProcessor;
    private WhisperProcessor? _accurateProcessor;
    private WhisperFactory? _fastFactory;
    private WhisperFactory? _accurateFactory;
    private readonly ASRSettings _settings;
    private readonly ModelDownloadService _downloadService;
    private readonly List<string> _hotwords = new();
    private Dictionary<string, string> _correctionDictionary = new();
    private readonly Dictionary<string, System.Text.RegularExpressions.Regex> _compiledCorrectionRegexes = new();
    private readonly Dictionary<string, System.Text.RegularExpressions.Regex> _compiledHotwordRegexes = new();
    private string _initialPrompt = string.Empty;
    private bool _isModelLoaded = false;
    private readonly SemaphoreSlim _fastLock = new(1, 1);
    private readonly SemaphoreSlim _accurateLock = new(1, 1);

    public bool IsModelLoaded => _isModelLoaded;

    public event EventHandler<ModelDownloadProgressEventArgs>? ModelDownloadProgress;
    public event EventHandler<ModelStatusChangedEventArgs>? ModelStatusChanged;

    public WhisperASRService(ASRSettings settings, ModelDownloadService downloadService)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _downloadService = downloadService ?? throw new ArgumentNullException(nameof(downloadService));

        // イベントを転送
        _downloadService.DownloadProgress += (sender, e) => ModelDownloadProgress?.Invoke(this, e);
        _downloadService.StatusChanged += (sender, e) => ModelStatusChanged?.Invoke(this, e);
    }

    /// <summary>
    /// モデルを初期化
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                "ASR",
                ModelStatusType.Info,
                "ASRモデルの初期化を開始しました。"));

            LoggerService.LogDebug("InitializeAsync: モデル確認開始");
            var fastModelPath = await EnsureModelAsync(
                _settings.FastModelPath,
                DefaultFastModelFileName,
                DefaultFastModelDownloadUrl,
                FastModelLabel);
            LoggerService.LogDebug($"InitializeAsync: 高速モデルパス={fastModelPath}");

            var accurateModelPath = await EnsureModelAsync(
                _settings.AccurateModelPath,
                DefaultAccurateModelFileName,
                DefaultAccurateModelDownloadUrl,
                AccurateModelLabel);
            LoggerService.LogDebug($"InitializeAsync: 高精度モデルパス={accurateModelPath}");

            await Task.Run(() =>
            {
            // GPUランタイムの初期化条件:
            // - AMD Vulkan: Whisper.net.Runtime.Vulkan が必要。Vulkan対応ドライバと ggml 系モデルが必須。
            // - NVIDIA CUDA: Whisper.net.Runtime.Cublas が必要。CUDA対応ドライバが必須。
            // - Auto/CPU: 明示的なGPU設定は行わず、Whisper.netに委譲。
            ConfigureGpuRuntime();
            ValidateModelCompatibility(fastModelPath, accurateModelPath);

            // 低遅延モデル（small/medium）の初期化
            if (!string.IsNullOrWhiteSpace(fastModelPath) && File.Exists(fastModelPath))
            {
                try
                {
                    var fileInfo = new FileInfo(fastModelPath);
                    LoggerService.LogDebug($"Loading fast model from: {fileInfo.FullName} (Size: {fileInfo.Length} bytes)");

                    LoggerService.LogDebug($"Creating WhisperFactory from path: {fastModelPath}");
                    _fastFactory = WhisperFactory.FromPath(fastModelPath);
                    LoggerService.LogDebug($"WhisperFactory created successfully");

                    LoggerService.LogDebug($"Creating builder from factory");
                    var fastBuilder = _fastFactory.CreateBuilder();
                    LoggerService.LogDebug($"Builder created successfully");

                    LoggerService.LogDebug($"Configuring builder: Language={_settings.Language}, Threads=4");
                    fastBuilder = fastBuilder
                        .WithLanguage(_settings.Language)
                        .WithThreads(4);
                    ConfigurePromptAndHotwords(fastBuilder);
                    LoggerService.LogDebug($"Building processor");
                    _fastProcessor = fastBuilder.Build();
                    LoggerService.LogDebug($"Processor built successfully");
                    OnModelStatusChanged(new ModelStatusChangedEventArgs(
                        ServiceName,
                        FastModelLabel,
                        ModelStatusType.LoadSucceeded,
                        "高速ASRモデルの読み込みが完了しました。"));
                }
                catch (Exception ex)
                {
                    var errorMsg = $"高速ASRモデルの読み込みに失敗しました: {ex.Message}";
                    var debugMsg = $"Failed to load fast ASR model from {fastModelPath}: {ex.GetType().Name}: {ex.Message}";
                    var current = ex;
                    var level = 0;
                    while (current != null)
                    {
                        level++;
                        debugMsg += $"\n  [{level}] {current.GetType().FullName}: {current.Message}";
                        current = current.InnerException;
                    }
                    OnModelStatusChanged(new ModelStatusChangedEventArgs(
                        ServiceName,
                        FastModelLabel,
                        ModelStatusType.LoadFailed,
                        errorMsg,
                        ex));
                    LoggerService.LogException(errorMsg, ex);
                }
            }
            else
            {
                var pathStatus = string.IsNullOrWhiteSpace(fastModelPath) ? "未設定" :
                                  File.Exists(fastModelPath) ? "存在" : "不存在";
                var errorMsg = $"高速ASRモデルが見つかりません: {fastModelPath} ({pathStatus})";
                OnModelStatusChanged(new ModelStatusChangedEventArgs(
                    ServiceName,
                    FastModelLabel,
                    ModelStatusType.LoadFailed,
                    errorMsg));
                LoggerService.LogWarning($"Fast model not found. Path: {fastModelPath}, Status: {pathStatus}");
            }

            // 高精度モデル（large系）の初期化
            if (!string.IsNullOrWhiteSpace(accurateModelPath) && File.Exists(accurateModelPath))
            {
                try
                {
                    var fileInfo = new FileInfo(accurateModelPath);
                    LoggerService.LogDebug($"Loading accurate model from: {fileInfo.FullName} (Size: {fileInfo.Length} bytes)");

                    Debug.WriteLine($"Creating WhisperFactory from path: {accurateModelPath}");
                    _accurateFactory = WhisperFactory.FromPath(accurateModelPath);
                    LoggerService.LogDebug($"WhisperFactory created successfully");

                    LoggerService.LogDebug($"Creating builder from factory");
                    var builder = _accurateFactory.CreateBuilder();
                    LoggerService.LogDebug($"Builder created successfully");

                    LoggerService.LogDebug($"Configuring builder: Language={_settings.Language}, Threads=4");
                    builder = builder
                        .WithLanguage(_settings.Language)
                        .WithThreads(4);

                    ApplyBeamSearchSettings(builder);

                    ConfigurePromptAndHotwords(builder);
                    LoggerService.LogDebug($"Building processor");
                    _accurateProcessor = builder.Build();
                    LoggerService.LogDebug($"Processor built successfully");
                    OnModelStatusChanged(new ModelStatusChangedEventArgs(
                        ServiceName,
                        AccurateModelLabel,
                        ModelStatusType.LoadSucceeded,
                        "高精度ASRモデルの読み込みが完了しました。"));
                }
                catch (Exception ex)
                {
                    var errorMsg = $"高精度ASRモデルの読み込みに失敗しました: {ex.Message}";
                    var debugMsg = $"Failed to load accurate ASR model from {accurateModelPath}: {ex.GetType().Name}: {ex.Message}";
                    var current = ex;
                    var level = 0;
                    while (current != null)
                    {
                        level++;
                        debugMsg += $"\n  [{level}] {current.GetType().FullName}: {current.Message}";
                        current = current.InnerException;
                    }
                    OnModelStatusChanged(new ModelStatusChangedEventArgs(
                        ServiceName,
                        AccurateModelLabel,
                        ModelStatusType.LoadFailed,
                        errorMsg,
                        ex));
                    LoggerService.LogException(errorMsg, ex);
                }
            }
            else
            {
                var pathStatus = string.IsNullOrWhiteSpace(accurateModelPath) ? "未設定" :
                                  File.Exists(accurateModelPath) ? "存在" : "不存在";
                var errorMsg = $"高精度ASRモデルが見つかりません: {accurateModelPath} ({pathStatus})";
                OnModelStatusChanged(new ModelStatusChangedEventArgs(
                    ServiceName,
                    AccurateModelLabel,
                    ModelStatusType.LoadFailed,
                    errorMsg));
                LoggerService.LogWarning($"Accurate model not found. Path: {accurateModelPath}, Status: {pathStatus}");
            }

            _isModelLoaded = _fastProcessor != null || _accurateProcessor != null;
        });

            if (!_isModelLoaded)
            {
                OnModelStatusChanged(new ModelStatusChangedEventArgs(
                    ServiceName,
                    "ASR",
                    ModelStatusType.Fallback,
                    "ASRモデルが未ロードのため音声認識は実行できません。"));
            }
        }
        catch (Exception ex)
        {
            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                "ASR",
                ModelStatusType.LoadFailed,
                $"ASR初期化エラー: {ex.Message}",
                ex));
            LoggerService.LogError($"InitializeAsync error: {ex}");
            _isModelLoaded = false;
        }
    }

    /// <summary>
    /// 低遅延ASRで音声を文字起こし（仮字幕用）
    /// </summary>
    public async Task<TranscriptionResult> TranscribeFastAsync(SpeechSegment segment)
    {
        if (_fastProcessor == null)
        {
            // フォールバック: 高精度モデルを使用
            if (_accurateProcessor != null)
            {
                return await TranscribeAccurateAsync(segment);
            }
            throw new InvalidOperationException("No ASR model loaded");
        }

        await _fastLock.WaitAsync();
        try
        {
            var sw = Stopwatch.StartNew();
            var result = await ProcessAudioAsync(_fastProcessor, segment.AudioData);
            sw.Stop();

            return new TranscriptionResult
            {
                SegmentId = segment.Id,
                Text = ApplyCorrections(result.Text),
                IsFinal = false,
                Confidence = result.Confidence,
                DetectedLanguage = _settings.Language,
                ProcessingTimeMs = sw.ElapsedMilliseconds
            };
        }
        finally
        {
            _fastLock.Release();
        }
    }

    /// <summary>
    /// 高精度ASRで音声を文字起こし（確定字幕用）
    /// </summary>
    public async Task<TranscriptionResult> TranscribeAccurateAsync(SpeechSegment segment)
    {
        if (_accurateProcessor == null)
        {
            // フォールバック: 低遅延モデルを使用
            if (_fastProcessor != null)
            {
                var fallbackResult = await TranscribeFastAsync(segment);
                fallbackResult.IsFinal = true;
                return fallbackResult;
            }
            throw new InvalidOperationException("No ASR model loaded");
        }

        await _accurateLock.WaitAsync();
        try
        {
            var sw = Stopwatch.StartNew();
            var result = await ProcessAudioAsync(_accurateProcessor, segment.AudioData);
            sw.Stop();

            return new TranscriptionResult
            {
                SegmentId = segment.Id,
                Text = ApplyCorrections(result.Text),
                IsFinal = true,
                Confidence = result.Confidence,
                DetectedLanguage = _settings.Language,
                ProcessingTimeMs = sw.ElapsedMilliseconds
            };
        }
        finally
        {
            _accurateLock.Release();
        }
    }

    /// <summary>
    /// 音声データを処理して文字起こし結果を取得
    /// </summary>
    private async Task<(string Text, float Confidence)> ProcessAudioAsync(WhisperProcessor processor, float[] audioData)
    {
        var segments = new List<string>();
        float totalConfidence = 0;
        var segmentCount = 0;

        try
        {
            await foreach (var segment in processor.ProcessAsync(audioData))
            {
                segments.Add(segment.Text.Trim());
                totalConfidence += segment.Probability;
                segmentCount++;
            }
        }
        catch (FileNotFoundException ex)
        {
            LoggerService.LogException("ProcessAudioAsync: FileNotFoundException", ex);
            throw;
        }

        var text = string.Join(" ", segments);
        var avgConfidence = segmentCount > 0 ? totalConfidence / segmentCount : 0;

        return (text, avgConfidence);
    }

    /// <summary>
    /// 検出されたGPU種別を取得
    /// </summary>
    public GPUType DetectedGpuType { get; private set; } = GPUType.Auto;

    /// <summary>
    /// 検出されたGPU名を取得
    /// </summary>
    public string DetectedGpuName { get; private set; } = string.Empty;

    /// <summary>
    /// GPUランタイムを設定
    /// </summary>
    private void ConfigureGpuRuntime()
    {
        Debug.WriteLine($"ConfigureGpuRuntime: GPU.Enabled={_settings.GPU.Enabled}, GPU.Type={_settings.GPU.Type}, GPU.DeviceId={_settings.GPU.DeviceId}");

        // 自動検出モードの場合、GPU種別を検出
        if (_settings.GPU.Type == GPUType.Auto || _settings.GPU.Enabled)
        {
            LoggerService.LogDebug("ConfigureGpuRuntime: Auto detecting GPU type");
            var (detectedType, gpuName) = DetectGpuTypeWithName();
            DetectedGpuType = detectedType;
            DetectedGpuName = gpuName;
            LogGpuDetection(detectedType, gpuName);

            // 自動検出の結果を設定に反映
            if (_settings.GPU.Type == GPUType.Auto)
            {
                _settings.GPU.Type = detectedType;
                // GPUが検出されなかった場合はGPUを無効化
                if (detectedType == GPUType.CPU)
                {
                    _settings.GPU.Enabled = false;
                    LoggerService.LogWarning("ConfigureGpuRuntime: No GPU detected, disabling GPU mode");
                }
            }
        }

        // GPU無効時はCPUモード
        if (!_settings.GPU.Enabled)
        {
            LoggerService.LogInfo("ConfigureGpuRuntime: GPU disabled, using CPU mode");
            Environment.SetEnvironmentVariable("GGML_VK_DEVICE", null);
            Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", null);
            return;
        }

        var effectiveGpuType = _settings.GPU.Type;

        switch (effectiveGpuType)
        {
            case GPUType.NVIDIA_CUDA:
                // NVIDIA GeForce: CUDAランタイムのみ使用（Vulkanは除外）
                LoggerService.LogInfo($"ConfigureGpuRuntime: Setting CUDA device {_settings.GPU.DeviceId} for NVIDIA GPU");
                Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", _settings.GPU.DeviceId.ToString());
                Environment.SetEnvironmentVariable("GGML_VK_DEVICE", null);
                // CUDA → CPU の優先順位（Vulkanは使用しない）
                RuntimeOptions.RuntimeLibraryOrder = [
                    RuntimeLibrary.Cuda,
                    RuntimeLibrary.Cpu
                ];
                LoggerService.LogInfo("ConfigureGpuRuntime: RuntimeLibraryOrder set to [Cuda, Cpu]");
                break;
            case GPUType.AMD_Vulkan:
                // AMD Radeon: Vulkanランタイムを優先
                LoggerService.LogInfo($"ConfigureGpuRuntime: Setting Vulkan device {_settings.GPU.DeviceId} for AMD GPU");
                Environment.SetEnvironmentVariable("GGML_VK_DEVICE", _settings.GPU.DeviceId.ToString());
                Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", null);
                // Vulkan → CPU の優先順位（CUDAは使用しない）
                RuntimeOptions.RuntimeLibraryOrder = [
                    RuntimeLibrary.Vulkan,
                    RuntimeLibrary.Cpu
                ];
                LoggerService.LogInfo("ConfigureGpuRuntime: RuntimeLibraryOrder set to [Vulkan, Cpu]");
                break;
            case GPUType.CPU:
            case GPUType.Auto:
            default:
                LoggerService.LogInfo($"ConfigureGpuRuntime: Using CPU mode (effectiveGpuType={effectiveGpuType})");
                Environment.SetEnvironmentVariable("GGML_VK_DEVICE", null);
                Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", null);
                // CPUのみ
                RuntimeOptions.RuntimeLibraryOrder = [
                    RuntimeLibrary.Cpu
                ];
                LoggerService.LogInfo("ConfigureGpuRuntime: RuntimeLibraryOrder set to [Cpu]");
                break;
        }
    }

    /// <summary>
    /// GPU検出結果をログ出力
    /// </summary>
    private static void LogGpuDetection(GPUType gpuType, string gpuName)
    {
        var runtimeInfo = gpuType switch
        {
            GPUType.NVIDIA_CUDA => "CUDA (Whisper.net.Runtime.Cuda)",
            GPUType.AMD_Vulkan => "Vulkan (Whisper.net.Runtime.Vulkan)",
            GPUType.CPU => "CPU (Whisper.net.Runtime)",
            _ => "不明"
        };
        var message = $"検出したGPU: {gpuName}, 種別: {gpuType}, 使用ランタイム: {runtimeInfo}";
        Trace.WriteLine(message);
        LoggerService.LogDebug(message);
    }

    /// <summary>
    /// GPU種別を検出（GPU名も取得）
    /// </summary>
    private static (GPUType Type, string Name) DetectGpuTypeWithName()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return (GPUType.CPU, "非Windows環境");
            }

            string? nvidiaName = null;
            string? amdName = null;
            var allGpuNames = new List<string>();

            using var searcher = new ManagementObjectSearcher("select Name from Win32_VideoController");
            foreach (var result in searcher.Get())
            {
                if (result is not ManagementObject obj)
                {
                    continue;
                }

                var name = obj["Name"]?.ToString();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                allGpuNames.Add(name);
                LoggerService.LogDebug($"DetectGpuTypeWithName: Found GPU: {name}");

                if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("GeForce", StringComparison.OrdinalIgnoreCase))
                {
                    nvidiaName ??= name;
                }

                if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                {
                    amdName ??= name;
                }
            }

            // NVIDIA GPUを優先
            if (nvidiaName != null)
            {
                LoggerService.LogInfo($"DetectGpuTypeWithName: Selected NVIDIA GPU: {nvidiaName}");
                return (GPUType.NVIDIA_CUDA, nvidiaName);
            }

            // AMD GPUを検出
            if (amdName != null)
            {
                LoggerService.LogInfo($"DetectGpuTypeWithName: Selected AMD GPU: {amdName}");
                return (GPUType.AMD_Vulkan, amdName);
            }

            // 内蔵GPUのみの場合はCPUモード
            var gpuSummary = allGpuNames.Count > 0 ? string.Join(", ", allGpuNames) : "なし";
            LoggerService.LogInfo($"DetectGpuTypeWithName: No discrete GPU found, using CPU. Available GPUs: {gpuSummary}");
            return (GPUType.CPU, gpuSummary);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"GPU検出に失敗しました: {ex.Message}");
            LoggerService.LogError($"GPU検出に失敗しました: {ex.Message}");
        }

        return (GPUType.CPU, "検出エラー");
    }

    /// <summary>
    /// GPU種別を検出（後方互換性のため維持）
    /// </summary>
    private static GPUType DetectGpuType()
    {
        var (gpuType, _) = DetectGpuTypeWithName();
        return gpuType;
    }

    private void ValidateModelCompatibility(string? fastModelPath, string? accurateModelPath)
    {
        if (!_settings.GPU.Enabled || _settings.GPU.Type != GPUType.AMD_Vulkan)
        {
            return;
        }

        EnsureGgmlModel(fastModelPath);
        EnsureGgmlModel(accurateModelPath);
    }

    private static void EnsureGgmlModel(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            return;
        }

        var fileName = Path.GetFileName(modelPath);
        if (fileName.IndexOf("ggml", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                $"AMD Vulkanランタイムはggml形式モデルのみ対応しています。ggml-*.bin を指定してください: {modelPath}");
        }
    }

    /// <summary>
    /// モデルファイルを確認し、必要に応じてWhisperGgmlDownloaderでダウンロード
    /// </summary>
    private async Task<string?> EnsureModelAsync(
        string modelPath,
        string defaultFileName,
        string downloadUrl,
        string modelLabel)
    {
        // まずModelDownloadServiceで確認
        var resolvedPath = await _downloadService.EnsureModelAsync(
            modelPath,
            defaultFileName,
            downloadUrl,
            ServiceName,
            modelLabel,
            CancellationToken.None);

        // ファイルが存在しない場合、WhisperGgmlDownloaderでダウンロード
        if (string.IsNullOrEmpty(resolvedPath) || !File.Exists(resolvedPath))
        {
            resolvedPath = await DownloadModelWithWhisperGgmlAsync(defaultFileName, modelLabel);
        }

        return resolvedPath;
    }

    /// <summary>
    /// WhisperGgmlDownloaderを使用してモデルをダウンロード
    /// </summary>
    private async Task<string?> DownloadModelWithWhisperGgmlAsync(string defaultFileName, string modelLabel)
    {
        try
        {
            var ggmlType = GetGgmlTypeFromFileName(defaultFileName);
            if (ggmlType == null)
            {
                LoggerService.LogError($"DownloadModelWithWhisperGgmlAsync: Unknown model type for {defaultFileName}");
                return null;
            }

            var modelsDir = Path.Combine(AppContext.BaseDirectory, "models");
            if (!Directory.Exists(modelsDir))
            {
                Directory.CreateDirectory(modelsDir);
            }

            var targetPath = Path.Combine(modelsDir, defaultFileName);

            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                modelLabel,
                ModelStatusType.Downloading,
                $"WhisperGgmlDownloaderで{modelLabel}をダウンロード中..."));

            LoggerService.LogInfo($"DownloadModelWithWhisperGgmlAsync: Downloading {ggmlType.Value} to {targetPath}");

            using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(ggmlType.Value);
            await using var fileWriter = File.Create(targetPath);
            await modelStream.CopyToAsync(fileWriter);

            LoggerService.LogInfo($"DownloadModelWithWhisperGgmlAsync: Downloaded successfully to {targetPath}");

            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                modelLabel,
                ModelStatusType.DownloadCompleted,
                $"{modelLabel}のダウンロードが完了しました。"));

            return targetPath;
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"DownloadModelWithWhisperGgmlAsync: Error - {ex.Message}");
            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                modelLabel,
                ModelStatusType.DownloadFailed,
                $"{modelLabel}のダウンロードに失敗しました: {ex.Message}",
                ex));
            return null;
        }
    }

    /// <summary>
    /// ファイル名からGgmlTypeを取得
    /// </summary>
    private static GgmlType? GetGgmlTypeFromFileName(string fileName)
    {
        return fileName.ToLowerInvariant() switch
        {
            "ggml-tiny.bin" => GgmlType.Tiny,
            "ggml-base.bin" => GgmlType.Base,
            "ggml-small.bin" => GgmlType.Small,
            "ggml-medium.bin" => GgmlType.Medium,
            "ggml-large.bin" => GgmlType.LargeV1,
            "ggml-large-v1.bin" => GgmlType.LargeV1,
            "ggml-large-v2.bin" => GgmlType.LargeV2,
            "ggml-large-v3.bin" => GgmlType.LargeV3,
            "ggml-large-v3-turbo.bin" => GgmlType.LargeV3Turbo,
            _ => null
        };
    }

    private void OnModelStatusChanged(ModelStatusChangedEventArgs args)
    {
        ModelStatusChanged?.Invoke(this, args);
    }

    /// <summary>
    /// ホットワードリストを設定
    /// </summary>
    public void SetHotwords(IEnumerable<string> hotwords)
    {
        _hotwords.Clear();
        _hotwords.AddRange(hotwords);

        // 正規表現を事前コンパイル
        _compiledHotwordRegexes.Clear();
        foreach (var hotword in _hotwords)
        {
            if (!string.IsNullOrWhiteSpace(hotword))
            {
                var pattern = System.Text.RegularExpressions.Regex.Escape(hotword);
                _compiledHotwordRegexes[hotword] = new System.Text.RegularExpressions.Regex(
                    pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
            }
        }
    }

    /// <summary>
    /// 初期プロンプトを設定
    /// </summary>
    public void SetInitialPrompt(string prompt)
    {
        _initialPrompt = prompt;
    }

    /// <summary>
    /// ASR誤変換補正辞書を設定
    /// </summary>
    public void SetCorrectionDictionary(Dictionary<string, string> dictionary)
    {
        _correctionDictionary = new Dictionary<string, string>(dictionary);

        // 正規表現を事前コンパイル
        _compiledCorrectionRegexes.Clear();
        foreach (var entry in _correctionDictionary)
        {
            if (!string.IsNullOrWhiteSpace(entry.Key))
            {
                var pattern = System.Text.RegularExpressions.Regex.Escape(entry.Key);
                _compiledCorrectionRegexes[entry.Key] = new System.Text.RegularExpressions.Regex(
                    pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
            }
        }
    }

    private void ConfigurePromptAndHotwords(object builder)
    {
        var hasHotwords = _hotwords.Count > 0;
        var hasPrompt = !string.IsNullOrWhiteSpace(_initialPrompt);

        var hotwordsApplied = false;
        if (hasHotwords)
        {
            hotwordsApplied = TryInvokeBuilder(builder, "WithHotwords", _hotwords)
                || TryInvokeBuilder(builder, "WithHotwords", string.Join(", ", _hotwords));
        }

        var promptText = hasPrompt ? _initialPrompt : string.Empty;
        if (hasHotwords && !hotwordsApplied)
        {
            promptText = BuildPromptText();
        }

        if (!string.IsNullOrWhiteSpace(promptText))
        {
            TryInvokeBuilder(builder, "WithInitialPrompt", promptText);
            TryInvokeBuilder(builder, "WithPrompt", promptText);
        }
    }

    private string BuildPromptText()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(_initialPrompt))
        {
            parts.Add(_initialPrompt.Trim());
        }

        if (_hotwords.Count > 0)
        {
            parts.Add(string.Join(", ", _hotwords));
        }

        return string.Join(" ", parts).Trim();
    }

    private void ApplyBeamSearchSettings(object builder)
    {
        if (!_settings.UseBeamSearch)
        {
            return;
        }

        var beamSize = Math.Max(1, _settings.BeamSize);
        var appliedWithSize = TryInvokeBuilder(builder, "WithBeamSearchSamplingStrategy", beamSize);
        if (appliedWithSize)
        {
            return;
        }

        var appliedWithoutSize = TryInvokeBuilder(builder, "WithBeamSearchSamplingStrategy");
        if (appliedWithoutSize)
        {
            LogBeamSizeIgnored(beamSize);
            return;
        }

        LogBeamSearchUnsupported(beamSize);
    }

    private static void LogBeamSizeIgnored(int beamSize)
    {
        var message = $"Beam Size指定({beamSize})は未対応のため無視されます。";
        Trace.WriteLine(message);
        LoggerService.LogDebug(message);
    }

    private static void LogBeamSearchUnsupported(int beamSize)
    {
        var message = $"Beam Search設定が未対応のため、Beam Size({beamSize})は適用されません。";
        Trace.WriteLine(message);
        LoggerService.LogDebug(message);
    }

    private static bool TryInvokeBuilder(object builder, string methodName, object argument)
    {
        try
        {
            var method = builder.GetType()
                .GetMethods()
                .FirstOrDefault(info =>
                    string.Equals(info.Name, methodName, StringComparison.Ordinal)
                    && info.GetParameters().Length == 1
                    && info.GetParameters()[0].ParameterType.IsAssignableFrom(argument.GetType()));

            if (method == null)
            {
                return false;
            }

            method.Invoke(builder, new[] { argument });
            return true;
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"Failed to invoke {methodName}: {ex.Message}");
            return false;
        }
    }

    private static bool TryInvokeBuilder(object builder, string methodName)
    {
        try
        {
            var method = builder.GetType()
                .GetMethods()
                .FirstOrDefault(info =>
                    string.Equals(info.Name, methodName, StringComparison.Ordinal)
                    && info.GetParameters().Length == 0);

            if (method == null)
            {
                return false;
            }

            method.Invoke(builder, Array.Empty<object>());
            return true;
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"Failed to invoke {methodName}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 誤変換補正を適用
    /// </summary>
    private string ApplyCorrections(string text)
    {
        // 事前コンパイルした正規表現を使用
        foreach (var entry in _correctionDictionary)
        {
            if (_compiledCorrectionRegexes.TryGetValue(entry.Key, out var regex))
            {
                text = regex.Replace(text, entry.Value);
            }
        }

        // ホットワードに基づく補正（簡易実装）
        // 実際の実装では、より高度なマッチングアルゴリズムを使用
        foreach (var hotword in _hotwords)
        {
            if (_compiledHotwordRegexes.TryGetValue(hotword, out var regex))
            {
                // 大文字小文字を無視した置換
                text = regex.Replace(text, hotword);
            }
        }

        return text;
    }

    public void Dispose()
    {
        _fastProcessor?.Dispose();
        _accurateProcessor?.Dispose();
        _fastFactory?.Dispose();
        _accurateFactory?.Dispose();
        _fastLock.Dispose();
        _accurateLock.Dispose();
    }
}
