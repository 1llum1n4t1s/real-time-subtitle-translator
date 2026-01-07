# セットアップガイド

## 開発環境の要件
- Visual Studio 2022 (17.8以降)
- .NET 8.0 SDK
- NVIDIA GPU (CUDA 12.x) または AMD GPU (Vulkan対応)

## ビルド手順
1. リポジトリをクローンします。
   ```bash
   git clone https://github.com/1llum1n4t1s/real-time-subtitle-translator.git
   ```
2. ソリューションファイル (`RealTimeTranslator.sln`) をVisual Studioで開きます。
3. NuGetパッケージを復元します。
4. ターゲットプラットフォームを `x64` に設定してビルドします。

## モデルの配置
Whisperモデルファイル (.bin) を `models/` ディレクトリに配置してください。
- `base.bin` (低遅延用)
- `large-v3.bin` (高精度用)

## 依存ライブラリ
- [NAudio](https://github.com/naudio/NAudio): 音声キャプチャ
- [Whisper.net](https://github.com/sandrohanea/whisper.net): Whisperランタイム
- [Argos Translate](https://github.com/argosopentech/argos-translate): 翻訳エンジン
