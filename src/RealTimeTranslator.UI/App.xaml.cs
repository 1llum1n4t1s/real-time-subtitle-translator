using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using RealTimeTranslator.ASR.Services;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;
using RealTimeTranslator.Translation.Services;
using RealTimeTranslator.UI.Services;
using RealTimeTranslator.UI.ViewModels;
using RealTimeTranslator.UI.Views;
using Velopack;

namespace RealTimeTranslator.UI;

/// <summary>
/// アプリケーションエントリポイント
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private OverlayWindow? _overlayWindow;
    private CancellationTokenSource? _updateCancellation;

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("OnStartup: 起動開始");
            VelopackApp.Build().Run();
            System.Diagnostics.Debug.WriteLine("OnStartup: Velopack初期化完了");
            base.OnStartup(e);
            System.Diagnostics.Debug.WriteLine("OnStartup: base.OnStartup完了");

            // 設定を読み込み
            var settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            System.Diagnostics.Debug.WriteLine($"OnStartup: settingsPath={settingsPath}");
            var settings = AppSettings.Load(settingsPath);
            System.Diagnostics.Debug.WriteLine("OnStartup: 設定読み込み完了");

            // DIコンテナを構築
            var services = new ServiceCollection();
            ConfigureServices(services, settings, settingsPath);
            _serviceProvider = services.BuildServiceProvider();
            System.Diagnostics.Debug.WriteLine("OnStartup: DI構築完了");

            var updateService = _serviceProvider.GetRequiredService<IUpdateService>();
            updateService.UpdateSettings(settings.Update);
            _updateCancellation = new CancellationTokenSource();
            _ = updateService.StartAsync(_updateCancellation.Token);
            System.Diagnostics.Debug.WriteLine("OnStartup: 更新サービス開始");

            // オーバーレイウィンドウを表示
            var overlayViewModel = _serviceProvider.GetRequiredService<OverlayViewModel>();
            _overlayWindow = new OverlayWindow(overlayViewModel);
            _overlayWindow.Show();
            System.Diagnostics.Debug.WriteLine("OnStartup: オーバーレイウィンドウ表示");

            // メインウィンドウを表示
            var mainViewModel = _serviceProvider.GetRequiredService<MainViewModel>();
            var mainWindow = new MainWindow(mainViewModel);
            mainWindow.Show();
            System.Diagnostics.Debug.WriteLine("OnStartup: メインウィンドウ表示");

            MainWindow = mainWindow;

            // モデルをバックグラウンドで初期化（UIスレッドで開始してawaitしない）
            _ = mainViewModel.InitializeModelsAsync();
            System.Diagnostics.Debug.WriteLine("OnStartup: 起動完了");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"アプリケーション起動エラー: {ex}");
            MessageBox.Show($"アプリケーション起動に失敗しました:\n\n{ex.Message}\n\n{ex.StackTrace}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            Current.Shutdown(1);
        }
    }

    private void ConfigureServices(IServiceCollection services, AppSettings settings, string settingsPath)
    {
        // 設定
        services.AddSingleton(settings);
        services.AddSingleton(settings.ASR);
        services.AddSingleton(settings.Translation);
        services.AddSingleton(settings.Overlay);
        services.AddSingleton(settings.AudioCapture);
        services.AddSingleton(new SettingsFilePath(settingsPath));

        // HttpClient（シングルトン）
        services.AddSingleton<HttpClient>(sp =>
        {
            var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(30); // 大きなファイルのダウンロード用
            return httpClient;
        });

        // モデルダウンロードサービス
        services.AddSingleton<ModelDownloadService>();

        // サービス
        services.AddSingleton<IAudioCaptureService, AudioCaptureService>();
        services.AddSingleton<IVADService, VADService>();
        services.AddSingleton<IASRService, WhisperASRService>();
        services.AddSingleton<ITranslationService, OnnxTranslationService>();
        services.AddSingleton<IUpdateService, UpdateService>();

        // ViewModels
        services.AddSingleton<OverlayViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton(sp =>
            new SettingsViewModel(
                sp.GetRequiredService<AppSettings>(),
                sp.GetRequiredService<SettingsFilePath>().Value,
                sp.GetRequiredService<OverlayViewModel>()));

        services.AddTransient<SettingsWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("OnExit: アプリケーション終了開始");

        // 更新サービスをキャンセル
        _updateCancellation?.Cancel();
        _updateCancellation?.Dispose();
        System.Diagnostics.Debug.WriteLine("OnExit: 更新サービス停止");

        // オーバーレイウィンドウを閉じる
        if (_overlayWindow != null)
        {
            _overlayWindow.Close();
            _overlayWindow = null;
        }
        System.Diagnostics.Debug.WriteLine("OnExit: オーバーレイウィンドウ終了");

        // サービスとViewModelを適切に破棄
        if (_serviceProvider != null)
        {
            // MainViewModelのDispose（イベントハンドラ登録解除、キャプチャ停止）
            var mainViewModel = _serviceProvider.GetService<MainViewModel>();
            mainViewModel?.Dispose();
            System.Diagnostics.Debug.WriteLine("OnExit: MainViewModel Dispose完了");

            // OverlayViewModelのDispose
            var overlayViewModel = _serviceProvider.GetService<OverlayViewModel>();
            overlayViewModel?.Dispose();
            System.Diagnostics.Debug.WriteLine("OnExit: OverlayViewModel Dispose完了");

            // 音声キャプチャサービスをDispose
            var audioCaptureService = _serviceProvider.GetService<IAudioCaptureService>();
            audioCaptureService?.Dispose();
            System.Diagnostics.Debug.WriteLine("OnExit: AudioCaptureService Dispose完了");

            // ASRサービスをDispose
            var asrService = _serviceProvider.GetService<IASRService>();
            asrService?.Dispose();
            System.Diagnostics.Debug.WriteLine("OnExit: ASRService Dispose完了");

            // 翻訳サービスをDispose
            var translationService = _serviceProvider.GetService<ITranslationService>();
            translationService?.Dispose();
            System.Diagnostics.Debug.WriteLine("OnExit: TranslationService Dispose完了");

            // HttpClientとModelDownloadServiceを適切に破棄
            var httpClient = _serviceProvider.GetService<HttpClient>();
            httpClient?.Dispose();

            var downloadService = _serviceProvider.GetService<ModelDownloadService>();
            downloadService?.Dispose();
            System.Diagnostics.Debug.WriteLine("OnExit: DownloadService Dispose完了");

            _serviceProvider.Dispose();
            _serviceProvider = null;
            System.Diagnostics.Debug.WriteLine("OnExit: ServiceProvider Dispose完了");
        }

        System.Diagnostics.Debug.WriteLine("OnExit: アプリケーション終了完了");
        base.OnExit(e);
    }
}
