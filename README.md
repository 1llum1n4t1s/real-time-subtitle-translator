# Real-Time Subtitle Translator (Windows)

Windows環境で動作する、完全ローカル・GPU駆動のリアルタイム字幕翻訳アプリケーションです。

## 概要

指定したアプリケーション（ゲーム、動画、配信など）の音声をキャプチャし、リアルタイムで日本語字幕をデスクトップにオーバーレイ表示します。

## 特徴

| 特徴 | 説明 |
|------|------|
| **完全ローカル動作** | クラウドAPIを使用せず、サーバーコストがかかりません |
| **二段構えASR** | 低遅延ASR（仮字幕）と高精度ASR（確定字幕）を組み合わせ、速報性と正確性を両立 |
| **GPU最適化** | NVIDIA (CUDA) および AMD (Vulkan) のGPUを活用 |
| **プロセス単位キャプチャ** | 特定のアプリの音声のみを抽出し、ASR精度を最大化 |
| **WPFオーバーレイ** | ゲーム画面などの最前面に透過字幕を表示（クリック透過対応） |

## システム構成

```
音声キャプチャ → VAD → 低遅延ASR → 仮字幕表示
                    ↓
              高精度ASR → 翻訳 → 確定字幕表示
```

### パイプライン詳細

1. **音声キャプチャ**: WASAPIループバックキャプチャでプロセス単位の音声を取得
2. **音声前処理**: 16kHz/mono変換、ゲイン正規化
3. **VAD**: エネルギーベースの発話区間検出（2〜6秒で分割）
4. **低遅延ASR**: Whisper small/mediumモデルで即時文字起こし
5. **高精度ASR**: Whisper large-v3モデルで高精度文字起こし
6. **翻訳**: ローカル翻訳エンジンで英語→日本語変換
7. **オーバーレイ表示**: WPFで透過字幕を最前面表示

## 遅延目標

| 項目 | 目標値 |
|------|--------|
| 仮字幕表示 | 0.3〜0.8秒 |
| 確定字幕表示 | 1.5〜3.0秒 |
| 翻訳確定 | 確定字幕と同時 |

## プロジェクト構成

```
src/
├── RealTimeTranslator.Core/       # 共通インターフェース・モデル
│   ├── Interfaces/
│   │   ├── IAudioCaptureService.cs
│   │   ├── IVADService.cs
│   │   ├── IASRService.cs
│   │   └── ITranslationService.cs
│   └── Models/
│       ├── SubtitleItem.cs
│       └── AppSettings.cs
├── RealTimeTranslator.ASR/        # 音声認識関連
│   └── Services/
│       ├── AudioCaptureService.cs
│       ├── VADService.cs
│       └── WhisperASRService.cs
├── RealTimeTranslator.Translation/ # 翻訳関連
│   └── Services/
│       └── LocalTranslationService.cs
└── RealTimeTranslator.UI/         # WPFアプリケーション
    ├── Views/
    │   ├── MainWindow.xaml
    │   └── OverlayWindow.xaml
    ├── ViewModels/
    │   ├── MainViewModel.cs
    │   └── OverlayViewModel.cs
    └── App.xaml
```

## 開発環境

| 項目 | 要件 |
|------|------|
| IDE | Visual Studio 2022 (17.8以降) |
| SDK | .NET 8.0 |
| GPU | NVIDIA (CUDA 12.x) または AMD (Vulkan対応) |

## 依存ライブラリ

| ライブラリ | 用途 |
|-----------|------|
| [NAudio](https://github.com/naudio/NAudio) | 音声キャプチャ |
| [Whisper.net](https://github.com/sandrohanea/whisper.net) | Whisperランタイム |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVMフレームワーク |

## ビルド手順

1. リポジトリをクローン
   ```bash
   git clone https://github.com/1llum1n4t1s/real-time-subtitle-translator.git
   ```

2. Visual Studioでソリューションを開く
   ```
   RealTimeTranslator.sln
   ```

3. NuGetパッケージを復元

4. ターゲットプラットフォームを `x64` に設定してビルド

## モデルの配置

`models/` ディレクトリに以下のファイルを配置してください：

| ファイル | 用途 | ダウンロード元 |
|----------|------|----------------|
| `ggml-small.bin` | 低遅延ASR | [Hugging Face](https://huggingface.co/ggerganov/whisper.cpp) |
| `ggml-large-v3.bin` | 高精度ASR | [Hugging Face](https://huggingface.co/ggerganov/whisper.cpp) |

## 設定ファイル

`settings.json` で以下の項目をカスタマイズできます：

- ASRモデルパス・言語設定
- GPU設定（CUDA/Vulkan）
- オーバーレイの外観（フォント、色、表示時間）
- VADパラメータ
- ゲーム別プロファイル（ホットワード、辞書）

## ライセンス

MIT License
