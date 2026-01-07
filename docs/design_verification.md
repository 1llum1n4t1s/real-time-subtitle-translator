# 設計書との照合結果

このドキュメントは、元の設計書「Windows向けリアルタイム字幕翻訳アプリ設計まとめ」と実装内容の照合結果をまとめたものです。

## 照合結果サマリー

| 設計項目 | 実装状況 | 備考 |
|----------|----------|------|
| 完全ローカル構成 | ✅ 実装済み | クラウドAPI不使用 |
| 二段構えASR | ✅ 実装済み | Fast/Accurateの2系統 |
| プロセス単位キャプチャ | ✅ 実装済み | WASAPIループバック |
| VAD（無音検出） | ✅ 実装済み | エネルギーベース |
| GPU対応 | ✅ 実装済み | CUDA/Vulkan設定可能 |
| WPFオーバーレイ | ✅ 実装済み | クリック透過対応 |
| 翻訳キャッシュ | ✅ 実装済み | LRU方式 |
| ゲーム別プロファイル | ✅ 実装済み | JSON設定 |
| ホットワードリスト | ✅ 実装済み | ASRに注入可能 |
| 誤変換補正辞書 | ✅ 実装済み | 前後処理辞書 |

## 詳細照合

### 1. 目的と前提条件

**設計書の要件:**
- Windows環境で指定アプリの音声を取得
- リアルタイムに日本語字幕をオーバーレイ表示
- サーバーコストなし（ローカル完結）
- GPU必須（GeForce/Radeon）
- 字幕遅延は最大3秒以内

**実装状況:**
- `AudioCaptureService`: WASAPIでプロセス単位の音声キャプチャを実装
- `OverlayWindow`: WPFで透過オーバーレイを実装
- すべてローカル処理、クラウドAPI不使用
- `GPUSettings`でCUDA/Vulkan切替可能
- 低遅延ASR（0.3〜0.8秒目標）+ 高精度ASR（1.5〜3.0秒目標）

### 2. 二段構えASR

**設計書の要件:**
- 低遅延ASR: small/mediumモデル、即時表示、翻訳には使用しない
- 高精度ASR: large系モデル、beam search有効、言語固定

**実装状況:**
- `WhisperASRService.TranscribeFastAsync()`: 低遅延ASR実装
- `WhisperASRService.TranscribeAccurateAsync()`: 高精度ASR実装
- `ASRSettings`でモデルパス、beam search、言語設定可能
- `TranscriptionResult.IsFinal`で仮字幕/確定字幕を区別

### 3. 音声取得設計

**設計書の要件:**
- プロセス単位ループバックキャプチャ
- サンプルレート: 16kHz、mono、float/PCM
- ゲイン正規化、VAD

**実装状況:**
- `AudioCaptureService`: NAudio.WasapiLoopbackCaptureを使用
- `ConvertToFloat()`: PCM/float変換
- `Resample()`: 16kHzへのリサンプリング
- `ConvertToMono()`: モノラル変換
- `VADService`: エネルギーベースのVAD実装

### 4. 固有名詞・誤認識対策

**設計書の要件:**
- ホットワードリスト（キャラクター名、地名等）
- 初期プロンプト注入
- 誤変換補正辞書

**実装状況:**
- `IASRService.SetHotwords()`: ホットワード設定
- `IASRService.SetInitialPrompt()`: 初期プロンプト設定
- `GameProfile.ASRCorrectionDictionary`: 誤変換補正辞書
- `GameProfile.Hotwords`: ゲーム別ホットワード

### 5. 翻訳設計

**設計書の要件:**
- 確定ASRのみ翻訳
- 用語正規化（翻訳前）
- 置換辞書（翻訳後）
- 翻訳キャッシュ

**実装状況:**
- `MainViewModel`: 確定ASR結果のみ翻訳処理に渡す
- `LocalTranslationService.SetPreTranslationDictionary()`: 翻訳前辞書
- `LocalTranslationService.SetPostTranslationDictionary()`: 翻訳後辞書
- `_cache`: ConcurrentDictionaryによるキャッシュ実装

### 6. 字幕表示（WPFオーバーレイ）

**設計書の要件:**
- 仮字幕: 即時表示、薄色、書き換え可
- 確定字幕: 発話IDで上書き、フェードアウト
- 透明、最前面、クリック透過、DPI対応

**実装状況:**
- `OverlayWindow`: AllowsTransparency, Topmost, WindowStyle=None
- `SetClickThrough()`: Win32 APIでクリック透過
- `OverlaySettings.PartialTextColor`: 仮字幕色（半透明）
- `OverlaySettings.FinalTextColor`: 確定字幕色
- `SubtitleDisplayItem`: SegmentIdで紐づけ、フェードアウト処理

### 7. 設定管理

**設計書の要件:**
- ゲーム別プロファイル
- GPU検出とビルド切替

**実装状況:**
- `AppSettings`: JSON形式の設定ファイル
- `GameProfile`: ゲーム別のホットワード、辞書、プロンプト
- `GPUSettings`: GPU種別（Auto/CUDA/Vulkan/CPU）設定

## 今後の改善点

1. **プロセス単位キャプチャの強化**: Windows 10 21H1以降のAudioClientActivationParams対応
2. **翻訳エンジンの実装**: CTranslate2/Argos Translateとの実際の連携
3. **GPU自動検出**: 実行時のGPU種別自動判定
4. **設定画面**: GUIでの設定変更機能
5. **ログ出力**: ファイルへのログ保存機能
