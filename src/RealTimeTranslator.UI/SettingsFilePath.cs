namespace RealTimeTranslator.UI;

/// <summary>
/// 設定ファイルパスの共有用コンテナ
/// </summary>
public sealed class SettingsFilePath
{
    public SettingsFilePath(string value)
    {
        Value = value;
    }

    public string Value { get; }
}
