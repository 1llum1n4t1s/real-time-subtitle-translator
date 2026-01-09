using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.UI.ViewModels;

/// <summary>
/// 設定画面のViewModel
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly string _settingsPath;

    public SettingsViewModel(AppSettings settings, string settingsPath)
    {
        _settings = settings;
        _settingsPath = settingsPath;
        GpuTypes = new ReadOnlyCollection<GPUType>(Enum.GetValues<GPUType>());
    }

    public AppSettings Settings => _settings;

    public ReadOnlyCollection<GPUType> GpuTypes { get; }

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [RelayCommand]
    private void Save()
    {
        _settings.Save(_settingsPath);
        StatusMessage = $"設定を保存しました: {DateTime.Now:HH:mm:ss}";
    }
}
