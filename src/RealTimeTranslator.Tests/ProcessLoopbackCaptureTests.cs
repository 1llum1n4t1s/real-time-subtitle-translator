using Microsoft.VisualStudio.TestTools.UnitTesting;

using NAudio.CoreAudioApi;
using NAudio.Wave;

using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.Tests;

/// <summary>
/// 1llum1n4t1s.NAudio 4.x の Process Loopback 契約を、単体テストと実機 Integration で検証する。
/// </summary>
[TestClass]
public sealed class ProcessLoopbackCaptureTests
{
    [TestMethod]
    public async Task RecorderBuilder_PreCanceledActivation_DoesNotCallWindows()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        var builder = new WasapiRecorderBuilder()
            .WithProcessLoopback(1, ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(new WaveFormat(48000, 16, 2))
            .WithBufferLength(20);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => builder.BuildAsync(source.Token));
    }

    [TestMethod]
    [TestCategory("Integration")]
    [DoNotParallelize]
    public async Task RecorderBuilder_CurrentProcess_CanStartAndStop()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            Assert.Inconclusive("Process Loopback には Windows 10 2004 (build 19041) 以降が必要です。");
        }

        using var source = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var capture = new AudioCaptureService();
        var started = await capture.StartCaptureWithRetryAsync(Environment.ProcessId, source.Token);

        Assert.IsTrue(started);
        Assert.IsTrue(capture.IsCapturing);

        capture.StopCapture();
        Assert.IsFalse(capture.IsCapturing);
    }
}
