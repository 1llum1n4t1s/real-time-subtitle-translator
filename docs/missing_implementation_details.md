# 不足実装の詳細と今後の実装方針

## 1. はじめに

現在の実装は、アプリケーションの全体的な構造と主要なコンポーネント間の連携を確立していますが、完全な機能を実現するためには、いくつかの重要な技術的詳細が未実装のままです。これらの実装は、主にOS固有の機能呼び出しや、外部エンジンとの連携、実行時環境の動的なハンドリングに関連しています。

このドキュメントでは、主要な不足実装箇所を特定し、それぞれの技術的な課題と具体的な実装方針を概説します。

## 2. 主要な不足実装項目

現在、アプリケーションが完全に機能するために不可欠な、以下の4つの主要な実装が不足しています。

| 項目 | 現状 | 課題 |
| :--- | :--- | :--- |
| **プロセス単位音声キャプチャ** | システム全体の音声をキャプチャ | 特定アプリの音声分離が不完全 |
| **ローカル翻訳エンジン連携** | 翻訳処理をシミュレート | 実際の翻訳が行われない |
| **GPUランタイムの動的選択** | 手動での設定が必要 | ユーザー環境への自動適応が不可 |
| **GUI設定画面** | 実装なし | GUIでの設定変更が不可能 |

--- 

## 3. 各項目の技術詳細と実装方針

### 3.1. プロセス単位音声キャプチャの強化

**現状の課題**

現在の `AudioCaptureService` は、NAudioの `WasapiLoopbackCapture()` をデフォルトコンストラクタで初期化しています。この方法では、システム全体で再生されているすべての音声をキャプチャしてしまい、設計書で要求されている「指定したアプリの音声のみを取得する」という要件を完全には満たせていません。

**実装方針**

Windows 10 (21H1) 以降で導入された `AudioClientActivationParams` を利用して、特定のプロセスIDに紐づくオーディオセッションから直接ループバックキャプチャを行います。これにより、他のアプリケーションの通知音やBGMなどを完全に排除し、ASRの精度を最大化します。

具体的な実装手順は以下の通りです。

1.  **Win32 APIの定義**: P/Invoke (`DllImport`) を使用して、`ActivateAudioInterfaceAsync` 関数と、`AUDIOCLIENT_ACTIVATION_PARAMS` 構造体をC#コード内に定義します。

    ```csharp
    [StructLayout(LayoutKind.Sequential)]
    public struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public AUDIOCLIENT_ACTIVATION_TYPE ActivationType;
        public PROCESS_LOOPBACK_MODE ProcessLoopbackMode;
        public uint TargetProcessId;
    }
    ```

2.  **オーディオクライアントの有効化**: `ActivateAudioInterfaceAsync` を呼び出し、対象のプロセスIDを設定した `AUDIOCLIENT_ACTIVATION_PARAMS` を渡すことで、そのプロセス専用の `IAudioClient` インターフェースを取得します。

3.  **NAudioとの連携**: 取得した `IAudioClient` を利用して、NAudioの `WasapiLoopbackCapture` を初期化します。これにより、キャプチャ対象が指定したプロセスに限定されます。

### 3.2. ローカル翻訳エンジンの実装

**現状の課題**

`LocalTranslationService` は現在、実際の翻訳処理を行わず、`"[ja] {text}"` のような形式で原文を返すフォールバック実装になっています。

**実装方針**

.NET環境内で完結し、配布の容易性に優れる `ArgosTranslate.NET` を利用した実装を第一候補とします。パフォーマンスが要件を満たさない場合は、より高速なCTranslate2をPython経由で呼び出す方式を検討します。

1.  **ArgosTranslate.NETの導入**: `ArgosTranslate.NET` のNuGetパッケージを `RealTimeTranslator.Translation` プロジェクトに追加します。

2.  **モデルのロード**: `TranslationSettings` で指定されたモデルパスから、`ArgosTranslate.Models.Package.LoadFrom` メソッドを使用して翻訳パッケージをロードします。

3.  **翻訳処理の実装**: `PerformTranslationAsync` メソッド内で、ロードした翻訳モデルの `Translate` メソッドを呼び出し、実際の翻訳処理を行います。

    ```csharp
    // LocalTranslationService.cs 内の実装イメージ
    private async Task<string> PerformTranslationAsync(string text, string sourceLanguage, string targetLanguage)
    {
        if (!_isModelLoaded || _translationModel == null)
            return text; // フォールバック

        var translation = await Task.Run(() => _translationModel.Translate(text));
        return translation;
    }
    ```

### 3.3. GPUランタイムの動的選択

**現状の課題**

`WhisperASRService` は、`Whisper.net.Runtime.Cublas` などのGPUサポートライブラリに静的に依存しています。これにより、ユーザーの環境（NVIDIA/AMD/CPUのみ）に関わらず、すべてのランタイムが同梱されるため、配布パッケージが肥大化します。

**実装方針**

アプリケーション起動時にユーザーのGPUを検出し、適切な `Whisper.net` ランタイムを動的にロードする仕組みを実装します。

1.  **GPU検出ロジック**: `System.Management` を利用して、インストールされているビデオコントローラを検出し、`Name` プロパティから "NVIDIA" または "AMD" の文字列を検索します。

2.  **動的ライブラリロード**: 検出結果に基づき、`NativeLibrary.Load` メソッドを使用して、対応するランタイムDLL（例: `whisper.net.runtime.cublas.dll`）を動的に読み込みます。

3.  **WhisperFactoryの初期化**: ランタイムをロードした後、`WhisperFactory` を初期化します。これにより、適切なGPUバックエンドが自動的に利用されます。

### 3.4. GUI設定画面の実装

**現状の課題**

`MainViewModel` の `OpenSettingsCommand` は実装されておらず、`settings.json` ファイルを直接編集する以外に設定を変更する方法がありません。

**実装方針**

MVVMパターンに従い、`AppSettings` モデルにバインドされた設定ウィンドウを新たに作成します。

1.  **SettingsWindowの作成**: `SettingsWindow.xaml` と `SettingsViewModel.cs` を作成します。

2.  **ViewModelの実装**: `SettingsViewModel` は `AppSettings` オブジェクトを保持し、UIコントロール（TextBox, ComboBox, Sliderなど）のプロパティを双方向バインディングで公開します。

3.  **保存ロジック**: 「保存」ボタンがクリックされた際に、`AppSettings.Save` メソッドを呼び出して、変更を `settings.json` ファイルに書き込みます。

4.  **メインウィンドウとの連携**: `MainViewModel` から `OpenSettingsCommand` が実行された際に、DIコンテナ経由で `SettingsWindow` のインスタンスを生成し、モーダルダイアログとして表示します。
