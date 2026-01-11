using System.Diagnostics;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;
using Whisper.net;
using Whisper.net.Ggml;
using static RealTimeTranslator.Core.Services.LoggerService;

namespace RealTimeTranslator.Translation.Services;

/// <summary>
/// Whisper.net ベースのGPU翻訳サービス
/// OpenAI Whisper の翻訳機能を使用した高精度翻訳
/// GPU（CUDA/Vulkan/CPU）で高速実行
/// 音声データを直接翻訳して、テキスト翻訳結果を提供
/// </summary>
public class WhisperTranslationService : ITranslationService
{
    private const string ServiceName = "翻訳";
    private const string ModelLabel = "Whisper翻訳モデル";
    private const int MaxCacheSize = 1000;

    private readonly TranslationSettings _settings;
    private readonly ModelDownloadService _downloadService;
    private readonly HttpClient _httpClient;

    // LRUキャッシュ（最適化版）: LinkedListNodeを直接保持してO(1)アクセスを実現
    private readonly Dictionary<string, (string Value, LinkedListNode<string> Node)> _cache = new();
    private readonly LinkedList<string> _cacheOrder = new();
    private readonly object _cacheLock = new();

    private bool _isModelLoaded = false;
    private WhisperProcessor? _processor;
    private WhisperFactory? _factory;
    private MistralTranslationService? _mistralService;

    private Dictionary<string, string> _preTranslationDict = new();
    private Dictionary<string, string> _postTranslationDict = new();
    private readonly SemaphoreSlim _translateLock = new(1, 1);

    /// <summary>
    /// モデルが読み込まれているかどうか
    /// </summary>
    public bool IsModelLoaded => _isModelLoaded;

    public event EventHandler<ModelDownloadProgressEventArgs>? ModelDownloadProgress;
    public event EventHandler<ModelStatusChangedEventArgs>? ModelStatusChanged;

    public WhisperTranslationService(TranslationSettings settings, ModelDownloadService downloadService, HttpClient httpClient)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _downloadService = downloadService ?? throw new ArgumentNullException(nameof(downloadService));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

        _downloadService.DownloadProgress += (sender, e) => ModelDownloadProgress?.Invoke(this, e);
        _downloadService.StatusChanged += (sender, e) => ModelStatusChanged?.Invoke(this, e);
    }

    protected virtual void OnModelStatusChanged(ModelStatusChangedEventArgs e)
    {
        ModelStatusChanged?.Invoke(this, e);
    }

    /// <summary>
    /// 翻訳エンジンを初期化
    /// </summary>
    public async Task InitializeAsync()
    {
        OnModelStatusChanged(new ModelStatusChangedEventArgs(
            ServiceName,
            ModelLabel,
            ModelStatusType.Info,
            "翻訳モデルの初期化を開始しました。"));

        try
        {
            // モデルファイルをダウンロード/確認
            // medium: 高速（~3-5倍）、精度はlarge-v3より低い
            const string defaultModelFileName = "ggml-medium.bin";
            const string downloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin";

            var modelFilePath = await _downloadService.EnsureModelAsync(
                _settings.ModelPath,
                defaultModelFileName,
                downloadUrl,
                ServiceName,
                ModelLabel);

            if (string.IsNullOrWhiteSpace(modelFilePath))
            {
                throw new FileNotFoundException($"Failed to ensure model file for: {_settings.ModelPath}");
            }

            // モデルをロード
            await Task.Run(() => LoadModelFromPath(modelFilePath));

            // Mistral翻訳サービスを初期化
            _mistralService = new MistralTranslationService();
            await _mistralService.InitializeAsync();
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"Translation initialization error: {ex}");
            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                ModelLabel,
                ModelStatusType.LoadFailed,
                "翻訳モデルの初期化に失敗しました。",
                ex));
            _isModelLoaded = false;
        }
    }

    /// <summary>
    /// テキストを翻訳
    /// 注：このサービスは主に音声翻訳用です
    /// テキスト翻訳の場合は、音声データが必要なため簡易的な処理のみ実行
    /// </summary>
    public async Task<TranslationResult> TranslateAsync(string text, string sourceLanguage = "en", string targetLanguage = "ja")
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TranslationResult
            {
                OriginalText = text,
                TranslatedText = text,
                SourceLanguage = sourceLanguage,
                TargetLanguage = targetLanguage,
                FromCache = false,
                ProcessingTimeMs = 0
            };
        }

        var sw = Stopwatch.StartNew();

        // 完全一致キャッシュ（高速）
        var cacheKey = $"{sourceLanguage}:{targetLanguage}:{text}";
        if (TryGetFromCache(cacheKey, out var cachedTranslation) && cachedTranslation != null)
        {
            sw.Stop();
            LogDebug($"[TranslateAsync] キャッシュヒット: {text} -> {cachedTranslation}");
            return new TranslationResult
            {
                OriginalText = text,
                TranslatedText = cachedTranslation,
                SourceLanguage = sourceLanguage,
                TargetLanguage = targetLanguage,
                FromCache = true,
                ProcessingTimeMs = sw.ElapsedMilliseconds
            };
        }

        // 部分一致キャッシュ（セグメント数が多い場合の最適化）
        if (text.Length > 200)
        {
            var partialMatch = TryGetPartialCacheMatch(text, sourceLanguage, targetLanguage);
            if (partialMatch != null)
            {
                sw.Stop();
                LogDebug($"[TranslateAsync] 部分キャッシュヒット: {text.Substring(0, 50)}... -> キャッシュ使用");
                return new TranslationResult
                {
                    OriginalText = text,
                    TranslatedText = partialMatch,
                    SourceLanguage = sourceLanguage,
                    TargetLanguage = targetLanguage,
                    FromCache = true,
                    ProcessingTimeMs = sw.ElapsedMilliseconds
                };
            }
        }

        await _translateLock.WaitAsync();
        try
        {
            var preprocessedText = ApplyPreTranslation(text);
            LogDebug($"[TranslateAsync] 翻訳開始: Text={preprocessedText}, Source={sourceLanguage}, Target={targetLanguage}");

            // Mistralローカル翻訳を使用
            string translatedText;
            if (sourceLanguage.Equals(targetLanguage, StringComparison.OrdinalIgnoreCase))
            {
                translatedText = preprocessedText;
            }
            else if (_mistralService != null)
            {
                translatedText = await _mistralService.TranslateAsync(preprocessedText, sourceLanguage, targetLanguage);
            }
            else
            {
                LogWarning($"[TranslateAsync] Mistralサービスが初期化されていません");
                translatedText = preprocessedText;
            }

            translatedText = ApplyPostTranslation(translatedText);
            AddToCache(cacheKey, translatedText);

            sw.Stop();
            LogDebug($"[TranslateAsync] テキスト翻訳完了: Result={translatedText}, Time={sw.ElapsedMilliseconds}ms");

            return new TranslationResult
            {
                OriginalText = text,
                TranslatedText = translatedText,
                SourceLanguage = sourceLanguage,
                TargetLanguage = targetLanguage,
                FromCache = false,
                ProcessingTimeMs = sw.ElapsedMilliseconds
            };
        }
        finally
        {
            _translateLock.Release();
        }
    }

    /// <summary>
    /// 音声データを認識して翻訳（2段階：Whisper ASR → 翻訳）
    /// Whisper で音声を英語に認識し、その後翻訳サービスで目標言語に翻訳
    /// </summary>
    public async Task<string> TranslateAudioAsync(float[] audioData, string sourceLanguage = "en", string targetLanguage = "ja")
    {
        if (!_isModelLoaded || _processor == null)
        {
            LogError($"[TranslateAudioAsync] モデルが読み込まれていません");
            return string.Empty;
        }

        return await Task.Run(async () =>
            {
                try
                {
                    LogDebug($"[TranslateAudioAsync] 音声翻訳開始: Source={sourceLanguage}, Target={targetLanguage}, AudioLength={audioData.Length}");
                    var sw = Stopwatch.StartNew();

                    // ステップ1: Whisper で音声を認識（常に英語）
                    var recognizedSegments = new List<string>();
                    var swStep1 = Stopwatch.StartNew();
                    await foreach (var segment in _processor.ProcessAsync(audioData))
                {
                    recognizedSegments.Add(segment.Text.Trim());
                    LogDebug($"[TranslateAudioAsync] 認識セグメント: {segment.Text}");
                }

                swStep1.Stop();
                var recognizedText = string.Join(" ", recognizedSegments);
                if (string.IsNullOrWhiteSpace(recognizedText))
                {
                    LogDebug($"[TranslateAudioAsync] 音声から認識されたテキストがありません");
                    return string.Empty;
                }

                LogDebug($"[TranslateAudioAsync] 認識完了: {recognizedText} (ASR処理時間: {swStep1.ElapsedMilliseconds}ms)");

                // ステップ2: 認識したテキストを翻訳（英語→目標言語）
                if (targetLanguage.Equals("en", StringComparison.OrdinalIgnoreCase))
                {
                    // ターゲットが英語の場合はそのまま返す
                    LogDebug($"[TranslateAudioAsync] ターゲットが英語のため翻訳をスキップ");
                    return recognizedText;
                }

                var swStep2 = Stopwatch.StartNew();
                var translationResult = await TranslateAsync(recognizedText, "en", targetLanguage);
                swStep2.Stop();
                var translatedText = translationResult.TranslatedText;

                LogDebug($"[TranslateAudioAsync] 翻訳完了: {translatedText} (テキスト翻訳処理時間: {swStep2.ElapsedMilliseconds}ms)");
                sw.Stop();
                LogDebug($"[TranslateAudioAsync] 合計処理時間: {sw.ElapsedMilliseconds}ms (ASR: {swStep1.ElapsedMilliseconds}ms + 翻訳: {swStep2.ElapsedMilliseconds}ms)");
                return translatedText;
            }
            catch (Exception ex)
            {
                LoggerService.LogError($"[TranslateAudioAsync] 翻訳エラー: {ex.GetType().Name} - {ex.Message}");
                LoggerService.LogDebug($"[TranslateAudioAsync] StackTrace: {ex.StackTrace}");
                return string.Empty;
            }
        });
    }

    /// <summary>
    /// キャッシュに追加（LRU キャッシュ戦略 - O(1)最適化版）
    /// </summary>
    private void AddToCache(string key, string value)
    {
        lock (_cacheLock)
        {
            // 既存エントリがあれば削除（O(1)でノードにアクセス）
            if (_cache.TryGetValue(key, out var existing))
            {
                _cacheOrder.Remove(existing.Node);
            }

            // 新しいノードを末尾に追加
            var node = _cacheOrder.AddLast(key);
            _cache[key] = (value, node);

            // キャッシュサイズ制限を超えた場合、最古のエントリを削除
            while (_cacheOrder.Count > MaxCacheSize)
            {
                var oldestKey = _cacheOrder.First!.Value;
                _cacheOrder.RemoveFirst();
                _cache.Remove(oldestKey);
            }
        }
    }

    /// <summary>
    /// 部分キャッシュマッチを試行（セグメント単位での再利用）
    /// </summary>
    private string? TryGetPartialCacheMatch(string text, string sourceLanguage, string targetLanguage)
    {
        var sentences = text.Split(new[] { "。", ".", "!", "?" }, StringSplitOptions.RemoveEmptyEntries);
        if (sentences.Length <= 1) return null;

        var translatedSentences = new List<string>();
        var allCached = true;

        foreach (var sentence in sentences)
        {
            var trimmed = sentence.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var sentenceCacheKey = $"{sourceLanguage}:{targetLanguage}:{trimmed}";
            if (TryGetFromCache(sentenceCacheKey, out var cached) && cached != null)
            {
                translatedSentences.Add(cached);
            }
            else
            {
                allCached = false;
                break;
            }
        }

        return allCached && translatedSentences.Count > 0
            ? string.Join("。", translatedSentences) + "。"
            : null;
    }

    /// <summary>
    /// キャッシュから取得（O(1)最適化版）
    /// </summary>
    private bool TryGetFromCache(string key, out string? value)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                // ノードを末尾に移動（O(1)でノードにアクセス）
                _cacheOrder.Remove(entry.Node);
                var newNode = _cacheOrder.AddLast(key);
                _cache[key] = (entry.Value, newNode);

                value = entry.Value;
                return true;
            }

            value = null;
            return false;
        }
    }

    private string ApplyPreTranslation(string text)
    {
        foreach (var kvp in _preTranslationDict)
        {
            text = text.Replace(kvp.Key, kvp.Value, StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }

    private string ApplyPostTranslation(string text)
    {
        foreach (var kvp in _postTranslationDict)
        {
            text = text.Replace(kvp.Key, kvp.Value);
        }
        return text;
    }

    private void LoadModelFromPath(string modelPath)
    {
        try
        {
            LoggerService.LogDebug($"Whisper翻訳モデルの読み込み開始: {modelPath}");

            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException($"Translation model not found: {modelPath}");
            }

            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                ModelLabel,
                ModelStatusType.Info,
                "WhisperFactory を作成中..."));

            LoggerService.LogDebug($"Loading translation model from: {modelPath}");

            // GPU ランタイムを優先順に試行（CUDA → Vulkan → CPU）
            _factory = TryLoadWithGpuRuntime(modelPath);

            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                ModelLabel,
                ModelStatusType.Info,
                "WhisperProcessor を作成中..."));

            // GPU使用時のスレッド数最適化
            // GPU使用時は少ないスレッド数でCPU-GPU協働実行を効率化
            // CPU使用時は多いスレッド数で並列化
            var processorCount = Math.Max(4, Environment.ProcessorCount - 2);
            var builder = _factory.CreateBuilder()
                .WithThreads(processorCount);

            _processor = builder.Build();
            
            LoggerService.LogDebug($"Whisper Processor created with {processorCount} threads");

            _isModelLoaded = true;
            LoggerService.LogInfo("Whisper翻訳モデルの読み込みが完了しました");

            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                ModelLabel,
                ModelStatusType.LoadSucceeded,
                "Whisper翻訳モデルの読み込みが完了しました。"));
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"Whisper翻訳モデル読み込みに失敗: {ex}");
            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                ModelLabel,
                ModelStatusType.LoadFailed,
                "翻訳モデルの読み込みに失敗しました。",
                ex));
            _isModelLoaded = false;
        }
    }

    /// <summary>
    /// GPU ランタイムを優先順に試行してモデルをロード（CUDA → Vulkan → CPU）
    /// </summary>
    private WhisperFactory TryLoadWithGpuRuntime(string modelPath)
    {
        // GPU ランタイムの優先順位を設定（CUDA → Vulkan → CPU）
        try
        {
            LoggerService.LogDebug("Setting GPU runtime priority: CUDA → Vulkan → CPU");
            Whisper.net.LibraryLoader.RuntimeOptions.RuntimeLibraryOrder =
            [
                Whisper.net.LibraryLoader.RuntimeLibrary.Cuda,
                Whisper.net.LibraryLoader.RuntimeLibrary.Vulkan,
                Whisper.net.LibraryLoader.RuntimeLibrary.Cpu
            ];
        }
        catch (Exception ex)
        {
            LoggerService.LogDebug($"Failed to set runtime order: {ex.Message}");
        }

        // ファクトリを作成（自動的に優先順位に従ってランタイムを選択）
        var factory = WhisperFactory.FromPath(modelPath);
        
        // 使用されているランタイムをログに出力
        try
        {
            var loadedLibrary = Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary;
            LoggerService.LogInfo($"✅ Whisper runtime loaded: {loadedLibrary}");
        }
        catch
        {
            LoggerService.LogDebug("Could not determine loaded runtime library");
        }

        return factory;
    }


    public void SetPreTranslationDictionary(Dictionary<string, string> dictionary)
    {
        _preTranslationDict = new Dictionary<string, string>(dictionary);
    }

    public void SetPostTranslationDictionary(Dictionary<string, string> dictionary)
    {
        _postTranslationDict = new Dictionary<string, string>(dictionary);
    }

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
            _cacheOrder.Clear();
        }
    }

    public void Dispose()
    {
        _processor?.Dispose();
        _factory?.Dispose();
        _mistralService?.Dispose();
        _translateLock.Dispose();
    }
}
