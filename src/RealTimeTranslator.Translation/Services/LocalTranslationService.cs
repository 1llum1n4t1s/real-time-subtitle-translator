using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Translation.Services;

/// <summary>
/// ローカル翻訳サービス
/// CTranslate2またはArgos Translateを使用した翻訳
/// </summary>
public class LocalTranslationService : ITranslationService
{
    private readonly TranslationSettings _settings;
    private readonly ConcurrentDictionary<string, string> _cache = new();
    private Dictionary<string, string> _preTranslationDict = new();
    private Dictionary<string, string> _postTranslationDict = new();
    private bool _isModelLoaded = false;
    private Process? _translationProcess;
    private readonly SemaphoreSlim _translateLock = new(1, 1);

    public bool IsModelLoaded => _isModelLoaded;

    public LocalTranslationService(TranslationSettings? settings = null)
    {
        _settings = settings ?? new TranslationSettings();
    }

    /// <summary>
    /// 翻訳エンジンを初期化
    /// </summary>
    public async Task InitializeAsync()
    {
        // CTranslate2またはArgos Translateの初期化
        // 実際の実装では、Pythonプロセスを起動するか、
        // .NETバインディングを使用
        await Task.Run(() =>
        {
            // モデルの存在確認
            if (Directory.Exists(_settings.ModelPath))
            {
                _isModelLoaded = true;
            }
            else
            {
                // モデルが存在しない場合は、ダミーモードで動作
                Console.WriteLine($"Translation model not found at: {_settings.ModelPath}");
                Console.WriteLine("Running in fallback mode (no actual translation)");
                _isModelLoaded = true; // フォールバックモードとして動作
            }
        });
    }

    /// <summary>
    /// テキストを翻訳
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

        // 翻訳前の正規化
        string normalizedText = ApplyPreTranslation(text);

        // キャッシュチェック
        string cacheKey = $"{sourceLanguage}:{targetLanguage}:{normalizedText}";
        if (_cache.TryGetValue(cacheKey, out var cachedTranslation))
        {
            sw.Stop();
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

        await _translateLock.WaitAsync();
        try
        {
            // 実際の翻訳処理
            string translatedText = await PerformTranslationAsync(normalizedText, sourceLanguage, targetLanguage);

            // 翻訳後の補正
            translatedText = ApplyPostTranslation(translatedText);

            // キャッシュに保存
            if (_cache.Count >= _settings.CacheSize)
            {
                // 古いエントリを削除（簡易LRU）
                var keysToRemove = _cache.Keys.Take(_cache.Count / 4).ToList();
                foreach (var key in keysToRemove)
                {
                    _cache.TryRemove(key, out _);
                }
            }
            _cache[cacheKey] = translatedText;

            sw.Stop();
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

    private async Task<string> PerformTranslationAsync(string text, string sourceLanguage, string targetLanguage)
    {
        // 実際の翻訳処理
        // CTranslate2またはArgos Translateを使用
        // ここではフォールバック実装として、テキストをそのまま返す

        // TODO: 実際の翻訳エンジンとの連携
        // 以下はプレースホルダー実装

        // Pythonスクリプトを呼び出す例:
        // var result = await CallPythonTranslatorAsync(text, sourceLanguage, targetLanguage);

        await Task.Delay(10); // シミュレーション

        // フォールバック: 原文をそのまま返す（実際の翻訳エンジンがない場合）
        return $"[{targetLanguage}] {text}";
    }

    /// <summary>
    /// 翻訳前の用語正規化
    /// </summary>
    private string ApplyPreTranslation(string text)
    {
        foreach (var kvp in _preTranslationDict)
        {
            text = Regex.Replace(text, Regex.Escape(kvp.Key), kvp.Value, RegexOptions.IgnoreCase);
        }
        return text;
    }

    /// <summary>
    /// 翻訳後の補正
    /// </summary>
    private string ApplyPostTranslation(string text)
    {
        foreach (var kvp in _postTranslationDict)
        {
            text = text.Replace(kvp.Key, kvp.Value);
        }
        return text;
    }

    /// <summary>
    /// 翻訳前用語辞書を設定
    /// </summary>
    public void SetPreTranslationDictionary(Dictionary<string, string> dictionary)
    {
        _preTranslationDict = new Dictionary<string, string>(dictionary);
    }

    /// <summary>
    /// 翻訳後置換辞書を設定
    /// </summary>
    public void SetPostTranslationDictionary(Dictionary<string, string> dictionary)
    {
        _postTranslationDict = new Dictionary<string, string>(dictionary);
    }

    /// <summary>
    /// キャッシュをクリア
    /// </summary>
    public void ClearCache()
    {
        _cache.Clear();
    }

    public void Dispose()
    {
        _translationProcess?.Kill();
        _translationProcess?.Dispose();
        _translateLock.Dispose();
    }
}
