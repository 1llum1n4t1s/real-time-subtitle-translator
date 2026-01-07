namespace RealTimeTranslator.Core.Interfaces;

/// <summary>
/// VAD（Voice Activity Detection）サービスのインターフェース
/// 無音区間を検出し、発話単位で音声を分割
/// </summary>
public interface IVADService
{
    /// <summary>
    /// 音声データを処理し、発話区間を検出
    /// </summary>
    /// <param name="audioData">音声データ</param>
    /// <returns>発話区間のリスト</returns>
    IEnumerable<SpeechSegment> DetectSpeech(float[] audioData);

    /// <summary>
    /// VADの感度設定（0.0〜1.0）
    /// </summary>
    float Sensitivity { get; set; }

    /// <summary>
    /// 最小発話長（秒）
    /// </summary>
    float MinSpeechDuration { get; set; }

    /// <summary>
    /// 最大発話長（秒）
    /// </summary>
    float MaxSpeechDuration { get; set; }
}

/// <summary>
/// 発話区間を表すクラス
/// </summary>
public class SpeechSegment
{
    /// <summary>
    /// 発話ID
    /// </summary>
    public string Id { get; } = Guid.NewGuid().ToString();

    /// <summary>
    /// 開始時刻（秒）
    /// </summary>
    public float StartTime { get; set; }

    /// <summary>
    /// 終了時刻（秒）
    /// </summary>
    public float EndTime { get; set; }

    /// <summary>
    /// 音声データ
    /// </summary>
    public float[] AudioData { get; set; } = Array.Empty<float>();

    /// <summary>
    /// 発話長（秒）
    /// </summary>
    public float Duration => EndTime - StartTime;
}
