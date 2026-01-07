using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using RealTimeTranslator.UI.ViewModels;

namespace RealTimeTranslator.UI.Views;

/// <summary>
/// オーバーレイウィンドウ
/// 透明・最前面・クリック透過対応
/// </summary>
public partial class OverlayWindow : Window
{
    // Win32 API for click-through
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int GWL_EXSTYLE = -20;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    public OverlayWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public OverlayWindow(OverlayViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // クリック透過を有効化
        SetClickThrough();
    }

    /// <summary>
    /// ウィンドウをクリック透過に設定
    /// </summary>
    private void SetClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED);
    }

    /// <summary>
    /// クリック透過を解除（設定変更時など）
    /// </summary>
    public void DisableClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle & ~WS_EX_TRANSPARENT);
    }

    /// <summary>
    /// クリック透過を再有効化
    /// </summary>
    public void EnableClickThrough()
    {
        SetClickThrough();
    }
}
