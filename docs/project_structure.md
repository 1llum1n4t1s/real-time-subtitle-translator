# プロジェクト構成

## ディレクトリ構造

- `src/`: ソースコード
  - `RealTimeTranslator.Core/`: 共通ロジック、インターフェース、データモデル
  - `RealTimeTranslator.UI/`: WPFアプリケーション本体（オーバーレイUI、設定画面）
  - `RealTimeTranslator.ASR/`: 音声認識（Whisper）関連の実装
  - `RealTimeTranslator.Translation/`: 翻訳エンジン関連の実装
- `docs/`: ドキュメント
- `assets/`: アイコン、画像リソース
- `models/`: (Git管理外) Whisperモデルファイル等の格納場所

## 主要コンポーネント

1. **AudioCaptureService**: 指定プロセスの音声をキャプチャ
2. **VADService**: 音声から発話区間を検出
3. **ASRService**: Whisperを使用した文字起こし（Fast/Accurateの2系統）
4. **TranslationService**: テキストの翻訳
5. **OverlayWindow**: 字幕表示用の透過ウィンドウ
