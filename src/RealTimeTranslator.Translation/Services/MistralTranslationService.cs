namespace RealTimeTranslator.Translation.Services;

using RealTimeTranslator.Core.Services;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

/// <summary>
/// Mistral 7B Instruct v0.2を使用したローカル翻訳サービス
/// LLamaSharpで直接推論実行（自動ダウンロード）
/// </summary>
public class MistralTranslationService : IDisposable
{
    private const string ModelFileName = "mistral-7b-instruct-v0.2.Q4_K_M.gguf";
    private const string ModelDownloadUrl = "https://huggingface.co/TheBloke/Mistral-7B-Instruct-v0.2-GGUF/resolve/main/mistral-7b-instruct-v0.2.Q4_K_M.gguf";
    private const string ServiceName = "Mistral翻訳";
    private const string ModelLabel = "Mistral 7B";

    private readonly HttpClient _httpClient = new();
    private bool _isInitialized;
    private dynamic? _model;
    private dynamic? _context;
    private readonly ConcurrentDictionary<string, string> _translationCache = new();
    private readonly object _initLock = new();

    public event EventHandler<RealTimeTranslator.Core.Models.ModelDownloadProgressEventArgs>? DownloadProgress;
    public event EventHandler<RealTimeTranslator.Core.Models.ModelStatusChangedEventArgs>? StatusChanged;

    /// <summary>
    /// 初期化（モデルダウンロード＆ロード）
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        lock (_initLock)
        {
            if (_isInitialized)
            {
                return;
            }

            try
            {
                LoggerService.LogInfo("[MistralTranslationService] 初期化開始");

                var modelPath = DownloadModelIfNeeded();

                LoggerService.LogInfo($"[MistralTranslationService] モデルをロード: {modelPath}");
                var sw = Stopwatch.StartNew();

                // LLamaSharp動的ロード
                LoadModelDynamically(modelPath);

                sw.Stop();
                LoggerService.LogInfo($"[MistralTranslationService] モデルロード完了 ({sw.ElapsedMilliseconds}ms)");

                _isInitialized = true;
                LoggerService.LogInfo("[MistralTranslationService] 初期化完了");
            }
            catch (Exception ex)
            {
                LoggerService.LogError($"[MistralTranslationService] 初期化失敗: {ex.Message}");
                LoggerService.LogError($"[MistralTranslationService] スタックトレース: {ex.StackTrace}");
                throw;
            }
        }
    }

    /// <summary>
    /// LLamaSharpモデルを動的にロード（リフレクション使用）
    /// </summary>
    private void LoadModelDynamically(string modelPath)
    {
        try
        {
            // LLamaSharp アセンブリを取得
            var llamaAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "LLamaSharp")
                ?? throw new InvalidOperationException("LLamaSharp アセンブリが見つかりません");

            // ModelParams 型を取得
            var modelParamsType = llamaAssembly.GetType("LLama.ModelParams")
                ?? throw new InvalidOperationException("ModelParams 型が見つかりません");

            // LLamaWeights 型を取得
            var llamaWeightsType = llamaAssembly.GetType("LLama.LLamaWeights")
                ?? throw new InvalidOperationException("LLamaWeights 型が見つかりません");

            // ModelParams インスタンスを作成
            dynamic modelParams = Activator.CreateInstance(modelParamsType, modelPath)!;
            modelParams.ContextSize = 2048;
            modelParams.GpuLayerCount = 50;
            modelParams.Threads = Math.Max(4, Environment.ProcessorCount - 2);

            LoggerService.LogDebug("[MistralTranslationService] ModelParams を設定しました");

            // LLamaWeights.LoadFromFile() を呼び出し
            var loadFromFileMethod = llamaWeightsType.GetMethod(
                "LoadFromFile",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public,
                null,
                new[] { modelParamsType },
                null) ?? throw new InvalidOperationException("LoadFromFile メソッドが見つかりません");

            var weights = loadFromFileMethod.Invoke(null, new object[] { modelParams });
            if (weights == null)
            {
                throw new InvalidOperationException("モデルウェイトのロードに失敗しました");
            }

            LoggerService.LogDebug("[MistralTranslationService] モデルウェイトをロードしました");

            // CreateContext() を呼び出し
            var createContextMethod = weights.GetType().GetMethod(
                "CreateContext",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                null,
                new[] { modelParamsType },
                null) ?? throw new InvalidOperationException("CreateContext メソッドが見つかりません");

            _context = createContextMethod.Invoke(weights, new object[] { modelParams });
            _model = weights;

            LoggerService.LogDebug("[MistralTranslationService] コンテキストを作成しました");
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"[MistralTranslationService] モデルロードエラー: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// ローカル翻訳実行
    /// </summary>
    public async Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage)
    {
        if (!_isInitialized)
        {
            await InitializeAsync();
        }

        if (_context == null || _model == null)
        {
            LoggerService.LogError("[MistralTranslationService] コンテキストが初期化されていません");
            return text;
        }

        try
        {
            var cacheKey = $"{text}|{sourceLanguage}|{targetLanguage}";
            if (_translationCache.TryGetValue(cacheKey, out var cachedResult))
            {
                LoggerService.LogDebug($"[MistralTranslationService] キャッシュヒット: {text}");
                return cachedResult;
            }

            LoggerService.LogDebug($"[MistralTranslationService] 翻訳開始: {text} ({sourceLanguage} → {targetLanguage})");
            var sw = Stopwatch.StartNew();

            var translatedText = await TranslateWithMistralAsync(text, sourceLanguage, targetLanguage);

            sw.Stop();
            LoggerService.LogDebug($"[MistralTranslationService] 翻訳完了 ({sw.ElapsedMilliseconds}ms): {translatedText}");

            _translationCache.TryAdd(cacheKey, translatedText);

            return translatedText;
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"[MistralTranslationService] 翻訳失敗: {ex.Message}");
            return text;
        }
    }

    /// <summary>
    /// Mistral実際の翻訳処理
    /// </summary>
    private async Task<string> TranslateWithMistralAsync(string text, string sourceLanguage, string targetLanguage)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (_context == null || _model == null)
                {
                    return text;
                }

                var sourceLanguageFullName = GetLanguageName(sourceLanguage);
                var targetLanguageFullName = GetLanguageName(targetLanguage);

                var prompt = $"""
                    Translate the following text from {sourceLanguageFullName} to {targetLanguageFullName}.
                    Output only the translated text, nothing else.

                    Text: {text}
                    Translation:
                    """;

                LoggerService.LogDebug("[MistralTranslationService] 推論実行中...");
                var sw = Stopwatch.StartNew();

                // InteractiveExecutor を動的に取得・作成
                var response = new StringBuilder();

                try
                {
                    var executorType = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.GetName().Name == "LLamaSharp")
                        ?.GetType("LLama.InteractiveExecutor")
                        ?? throw new InvalidOperationException("InteractiveExecutor 型が見つかりません");

                    dynamic executor = Activator.CreateInstance(executorType, _context)!;

                    // InferAsync メソッドを取得
                    var inferAsyncMethod = executorType.GetMethod(
                        "InferAsync",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                        ?? throw new InvalidOperationException("InferAsync メソッドが見つかりません");

                    // InferAsync() を実行してジェネレータを取得
                    var task = inferAsyncMethod.Invoke(executor, new object[] { prompt });

                    if (task is System.Collections.IEnumerable enumerable)
                    {
                        foreach (var token in enumerable)
                        {
                            response.Append(token);
                            if (response.Length > 256)
                            {
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LoggerService.LogDebug($"[MistralTranslationService] インファレンスエラー: {ex.Message}");
                }

                sw.Stop();
                LoggerService.LogDebug($"[MistralTranslationService] 推論完了 ({sw.ElapsedMilliseconds}ms)");

                var result = response.ToString().Trim();
                return string.IsNullOrWhiteSpace(result) ? text : CleanupResponse(result);
            }
            catch (Exception ex)
            {
                LoggerService.LogError($"[MistralTranslationService] Mistral処理エラー: {ex.Message}");
                return text;
            }
        });
    }

    /// <summary>
    /// 応答テキストをクリーニング
    /// </summary>
    private static string CleanupResponse(string response)
    {
        response = response.Trim();

        // 改行で区切られている場合は最初の行を取得
        var lines = response.Split(new[] { "\n\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > 0)
        {
            response = lines[0].Trim();
        }

        // 「翻訳:」や「Translation:」を削除
        response = response.Replace("翻訳:", "").Replace("Translation:", "").Trim();

        // 括弧内のテキストを削除
        response = System.Text.RegularExpressions.Regex.Replace(response, @"\([^)]*\)", "").Trim();

        return response;
    }

    /// <summary>
    /// 言語コードから言語名に変換
    /// </summary>
    private static string GetLanguageName(string languageCode)
    {
        return languageCode.ToLower() switch
        {
            "en" => "English",
            "ja" => "Japanese",
            "es" => "Spanish",
            "fr" => "French",
            "de" => "German",
            "zh" => "Chinese",
            "ko" => "Korean",
            "ru" => "Russian",
            "ar" => "Arabic",
            "pt" => "Portuguese",
            "it" => "Italian",
            "nl" => "Dutch",
            "pl" => "Polish",
            "tr" => "Turkish",
            _ => languageCode,
        };
    }

    /// <summary>
    /// モデルファイルダウンロード（必要に応じて）
    /// </summary>
    private string DownloadModelIfNeeded()
    {
        var modelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RealTimeTranslator",
            "models"
        );
        var modelPath = Path.Combine(modelDir, ModelFileName);

        if (File.Exists(modelPath))
        {
            LoggerService.LogInfo($"[MistralTranslationService] モデルキャッシュ使用: {modelPath}");
            return modelPath;
        }

        Directory.CreateDirectory(modelDir);

        LoggerService.LogInfo($"[MistralTranslationService] モデルダウンロード開始");
        LoggerService.LogInfo($"[MistralTranslationService] URL: {ModelDownloadUrl}");
        LoggerService.LogInfo($"[MistralTranslationService] 保存先: {modelPath}");
        LoggerService.LogInfo($"[MistralTranslationService] ⚠️ 初回ダウンロード（約4GB）のため、10-30分かかる可能性があります");

        try
        {
            var progressReporter = new Progress<long>(bytes =>
            {
                var mb = bytes / (1024 * 1024);
                LoggerService.LogInfo($"[MistralTranslationService] ダウンロード進行中: {mb}MB");
            });

            var downloadTask = DownloadFileWithProgressAsync(ModelDownloadUrl, modelPath, progressReporter);
            downloadTask.Wait();

            LoggerService.LogInfo($"[MistralTranslationService] モデルダウンロード完了");
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"[MistralTranslationService] モデルダウンロード失敗: {ex.Message}");
            throw;
        }

        return modelPath;
    }

    /// <summary>
    /// プログレス付きファイルダウンロード
    /// </summary>
    private async Task DownloadFileWithProgressAsync(string url, string filePath, IProgress<long> progress)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        var canReportProgress = totalBytes != -1;

        if (canReportProgress)
        {
            var gbSize = totalBytes / (1024 * 1024 * 1024.0);
            LoggerService.LogInfo($"[MistralTranslationService] ダウンロード総サイズ: {gbSize:F2}GB");
        }

        using var contentStream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);

        var totalRead = 0L;
        var buffer = new byte[65536];
        int read;
        var lastProgressReport = DateTime.Now;

        while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) != 0)
        {
            await fileStream.WriteAsync(buffer, 0, read);
            totalRead += read;

            if (canReportProgress && (DateTime.Now - lastProgressReport).TotalSeconds >= 2)
            {
                var percentage = totalBytes > 0 ? (totalRead * 100.0 / totalBytes) : 0;
                OnDownloadProgress(new RealTimeTranslator.Core.Models.ModelDownloadProgressEventArgs(
                    ServiceName,
                    ModelLabel,
                    totalRead,
                    totalBytes,
                    percentage));

                progress?.Report(totalRead);
                lastProgressReport = DateTime.Now;
            }
        }

        if (canReportProgress)
        {
            OnDownloadProgress(new RealTimeTranslator.Core.Models.ModelDownloadProgressEventArgs(
                ServiceName,
                ModelLabel,
                totalRead,
                totalBytes,
                100.0));

            progress?.Report(totalRead);
        }
    }

    /// <summary>
    /// ダウンロード進捗イベントを発火
    /// </summary>
    private void OnDownloadProgress(RealTimeTranslator.Core.Models.ModelDownloadProgressEventArgs e)
    {
        DownloadProgress?.Invoke(this, e);
    }

    /// <summary>
    /// ステータス変更イベントを発火
    /// </summary>
    private void OnStatusChanged(RealTimeTranslator.Core.Models.ModelStatusChangedEventArgs e)
    {
        StatusChanged?.Invoke(this, e);
    }

    /// <summary>
    /// リソース解放
    /// </summary>
    public void Dispose()
    {
        try
        {
            _context?.Dispose();
            _model?.Dispose();
        }
        catch { }

        _httpClient?.Dispose();
        GC.SuppressFinalize(this);
    }
}
