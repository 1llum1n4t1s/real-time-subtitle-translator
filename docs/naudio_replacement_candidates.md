# NAudio 利用状況

## 現在の構成

| 箇所 | NAudio 4.x API | 方針 |
|---|---|---|
| プロセスループバック | `WasapiRecorderBuilder.WithProcessLoopback` / `WasapiRecorder` | 対象プロセスと子プロセスを 48kHz / 16-bit / stereo、20ms バッファで取得する |
| byte → float 変換 | `RawSourceWaveStream.ToSampleProvider()` | 2ch は `StereoToMonoSampleProvider`、多チャンネルだけアプリ側で平均する |
| リサンプリング | `WdlResamplingSampleProvider` | `StreamingResampler` がインスタンスを保持し、チャンク境界をまたいで FIR 履歴を維持する |
| デバイス列挙 | `MMDeviceEnumerator` / `AudioSessionManager` | アクティブな音声セッションと対象 PID の解決に使う |
| WAV 出力 | `WaveFileWriter` | デバッグ送信音声を実送信レートで保存する |

`ISampleProvider` の実装と呼び出しは NAudio 4.x の `Read(Span<float>)` に統一する。

## 4.0.0 移行で廃止した旧経路

- 独自 COM/PInvoke 実装の `ProcessLoopbackCapture`。
- 非推奨の `WasapiCapture.CreateForProcessCaptureAsync` 互換 API。
- Process Loopback の生成を UI `SynchronizationContext` に固定する処理。
- 独自 COM 実装だけのために参照していた `Microsoft.Windows.CsWin32`。

新しい `WasapiRecorder` は source-generated COM による非同期アクティベーションを内包するため、呼び出し側で STA/UI スレッドへ固定しない。

## アプリ側に残す処理

- VAD 用の float 配列前処理。
- 100ms 単位のチャンク化とバッファ上限管理。
- 48kHz→16kHz VAD 用、48kHz→24kHz送信用の並列ストリーミングリサンプラ。
- 入力ゲイン、レベルメーター、無音判定など翻訳パイプライン固有の処理。
