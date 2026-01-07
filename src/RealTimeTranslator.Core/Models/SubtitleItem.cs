namespace RealTimeTranslator.Core.Models;

/// <summary>
/// 字幕アイテム
/// 仮字幕と確定字幕の両方を表現
/// </summary>
public class SubtitleItem
{
    /// <summary>
    /// 発話ID（SpeechSegmentと紐づけ）
    /// </summary>
    public string SegmentId { get; set; } = string.Empty;

    /// <summary>
    /// 原文（英語）
    /// </summary>
    public string OriginalText { get; set; } = string.Empty;

    /// <summary>
    /// 翻訳文（日本語）
    /// </summary>
    public string TranslatedText { get; set; } = string.Empty;

    /// <summary>
    /// 確定字幕かどうか
    /// true: 確定字幕（高精度ASR + 翻訳済み）
    /// false: 仮字幕（低遅延ASR、翻訳なし）
    /// </summary>
    public bool IsFinal { get; set; }

    /// <summary>
    /// 表示開始時刻
    /// </summary>
    public DateTime DisplayStartTime { get; set; } = DateTime.Now;

    /// <summary>
    /// 表示終了時刻（フェードアウト開始）
    /// </summary>
    public DateTime DisplayEndTime { get; set; }

    /// <summary>
    /// 表示時間（秒）
    /// </summary>
    public double DisplayDurationSeconds { get; set; } = 5.0;

    /// <summary>
    /// フェードアウト中かどうか
    /// </summary>
    public bool IsFadingOut => DateTime.Now >= DisplayEndTime;

    /// <summary>
    /// 表示すべきテキスト
    /// 確定字幕の場合は翻訳文、仮字幕の場合は原文
    /// </summary>
    public string DisplayText => IsFinal && !string.IsNullOrEmpty(TranslatedText) 
        ? TranslatedText 
        : OriginalText;
}
