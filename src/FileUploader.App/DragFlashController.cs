namespace FileUploader.App;

/// <summary>
/// 「拖拽浮现」上传气泡的开关与装配：常驻一个隐形落点窗口 + 一个拖拽探测器。
/// <para>
/// 组装方式刻意做成「信号不可靠也能用」：探测器只负责让气泡提前半透明浮现；
/// 真正决定出现/收起的是 <see cref="FileDropTarget"/> 收到的拖放数据。
/// </para>
/// </summary>
internal static class DragFlashController
{
    private static FlashDropWindow? _window;

    /// <summary>是否已经启动。</summary>
    public static bool IsRunning => _window is not null;

    /// <summary>启动（幂等）。失败不影响主流程，只是没有这个便捷入口。</summary>
    public static void EnsureStarted()
    {
        if (_window is not null)
        {
            return;
        }

        try
        {
            FlashLog.Info("controller: starting flash window");
            _window = FlashDropWindow.Start(AppWindows.ShowMainFromFlash, AppWindows.OpenSettings);
            FlashLog.Info($"controller: window ready={_window.IsReady}");
        }
        catch (Exception ex)
        {
            CrashLog.Write("DragFlashController.EnsureStarted", ex);
            _window?.Dispose();
            _window = null;
        }
    }

    /// <summary>开发辅助：走一遍 Peek → Armed → Uploading → Done。</summary>
    public static void Preview() => _window?.Preview();

    /// <summary>退出时收摊。</summary>
    public static void Stop()
    {
        _window?.Dispose();
        _window = null;
    }
}
