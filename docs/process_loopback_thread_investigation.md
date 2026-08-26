# Process Loopback スレッド要件

## 現在の結論

1llum1n4t1s.NAudio 4.0.0 の `WasapiRecorderBuilder.WithProcessLoopback` は、source-generated COM の agile completion handler で `ActivateAudioInterfaceAsync` を処理する。従来必要だった UI `SynchronizationContext` への Post と、同一 STA スレッド上で create/start する回避策は不要になった。

現在の開始経路は次のとおり。

1. `AudioCaptureService` が `WasapiRecorderBuilder` を構築する。
2. `WithProcessLoopback(..., IncludeTargetProcessTree)`、48kHz / 16-bit / stereo、20ms バッファを指定する。
3. `BuildAsync(cancellationToken)` を任意の呼び出しスレッドから await する。
4. `DataAvailable` / `RecordingStopped` を登録して `StartRecording()` を呼ぶ。

`WasapiRecorder` 4.0.0 は capture thread が `Starting` から `Capturing` へ遷移する前に停止すると、`Stopping` が `Capturing` で上書きされる競合がある。`AudioCaptureService` は開始成功を返す前に `Capturing` を確認し、停止・破棄時も `Starting` を抜けるまで待つ。

## 廃止した回避策

- `captureCreationContext` の受け渡し。
- `RunFullCaptureStartOnContextAsync` と UI コンテキストへの Post。
- 非推奨の `WasapiCapture.CreateForProcessCaptureAsync`。
- 独自 `ProcessLoopbackCapture` の COM/PInvoke 実装。

停止は引き続き native callback スレッドの終了待ちを含み得るため、翻訳パイプラインでは UI を塞がないようバックグラウンドで `StopCapture()` を実行する。
