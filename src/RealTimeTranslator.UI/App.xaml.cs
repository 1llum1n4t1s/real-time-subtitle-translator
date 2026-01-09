using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using RealTimeTranslator.ASR.Services;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Translation.Services;
using RealTimeTranslator.UI.ViewModels;
using RealTimeTranslator.UI.Views;
using Velopack;
using Velopack.Sources;

namespace RealTimeTranslator.UI;

/// <summary>
/// アプリケーションエントリポイント
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private OverlayWindow? _overlayWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        VelopackApp.Build().Run();
        base.OnStartup(e);

        // 設定を読み込み
        var settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        var settings = AppSettings.Load(settingsPath);

        // DIコンテナを構築
        var services = new ServiceCollection();
        ConfigureServices(services, settings, settingsPath);
        _serviceProvider = services.BuildServiceProvider();

        _ = InitializeUpdatesAsync(settings.Update);

        // オーバーレイウィンドウを表示
        var overlayViewModel = _serviceProvider.GetRequiredService<OverlayViewModel>();
        _overlayWindow = new OverlayWindow(overlayViewModel);
        _overlayWindow.Show();

        // メインウィンドウを表示
        var mainViewModel = _serviceProvider.GetRequiredService<MainViewModel>();
        var mainWindow = new MainWindow(mainViewModel);
        mainWindow.Show();

        MainWindow = mainWindow;
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

        // サービス
        services.AddSingleton<IAudioCaptureService, AudioCaptureService>();
        services.AddSingleton<IVADService, VADService>();
        services.AddSingleton<IASRService>(sp =>
        {
            var asrService = new WhisperASRService(sp.GetRequiredService<ASRSettings>());
            // 非同期初期化は別途行う
            return asrService;
        });
        services.AddSingleton<ITranslationService>(sp =>
        {
            var translationService = new LocalTranslationService(sp.GetRequiredService<TranslationSettings>());
            return translationService;
        });

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
        _overlayWindow?.Close();
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    private static async Task InitializeUpdatesAsync(UpdateSettings updateSettings)
    {
        if (!updateSettings.Enabled || string.IsNullOrWhiteSpace(updateSettings.FeedUrl))
        {
            return;
        }

        try
        {
            var source = new SimpleWebSource(new Uri(updateSettings.FeedUrl));
            using var manager = new UpdateManager(source);
            var updateInfo = await manager.CheckForUpdatesAsync();
            if (updateInfo is null)
            {
                return;
            }

            await manager.DownloadUpdatesAsync(updateInfo);
            if (updateSettings.AutoApply)
            {
                manager.ApplyUpdatesAndRestart(updateInfo);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Velopack update failed: {ex.Message}");
        }
    }
}
