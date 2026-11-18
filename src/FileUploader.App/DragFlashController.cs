using System.ComponentModel;

namespace FileUploader.App;

/// <summary>
/// 「拖拽浮现」上传气泡的开关与装配。
/// <para>
/// 跟着设置里的「右下角拖拽浮现上传气泡」走：打开时创建隐形落点窗口 + 拖拽探测器，
/// 关掉时立刻收摊 —— 既不占一个隐形窗口，也不装全局鼠标钩子。
/// 改设置后立即生效，不用重启。
/// </para>
/// </summary>
internal static class DragFlashController
{
    private static FlashDropWindow? _window;
    private static bool _attached;

    /// <summary>是否正在运行（隐形窗口已创建）。</summary>
    public static bool IsRunning => _window is not null;

    /// <summary>按当前设置启动/停止；幂等，重复调用安全。</summary>
    public static void Apply()
    {
        if (!_attached)
        {
            _attached = true;
            SettingsState.Current.PropertyChanged += OnSettingsChanged;
        }

        SetEnabled(SettingsState.Current.Value.FlashDropEnabled);
    }

    private static void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 配置是整份替换的，任何一次修改都会以 Value 的名字通知。
        if (e.PropertyName is not (null or "" or nameof(SettingsState.Value)))
        {
            return;
        }

        SetEnabled(SettingsState.Current.Value.FlashDropEnabled);
    }

    private static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            Start();
        }
        else
        {
            Stop();
        }
    }

    private static void Start()
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
            CrashLog.Write("DragFlashController.Start", ex);
            _window?.Dispose();
            _window = null;
        }
    }

    private static void Stop()
    {
        var window = _window;
        _window = null;
        if (window is null)
        {
            return;
        }

        FlashLog.Info("controller: stopping flash window");
        try
        {
            window.Dispose();

            // 等窗口线程真的退出：静态的 _active 是进程级共享的，
            // 新旧两个实例同时活着会把消息派发到错误的对象上。
            window.WaitForClose(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // 已经关掉了。
        }
    }

    /// <summary>开发辅助：走一遍 Peek → Armed → Uploading → Done。</summary>
    public static void Preview() => _window?.Preview();

    /// <summary>
    /// 在右下角气泡里显示一条提示（静默启动时用）。
    /// 气泡功能被关掉时这里什么也不做 —— 那种情况下用户本来就知道程序在托盘。
    /// </summary>
    public static void ShowHint(string message, string? subtitle = null) => _window?.Hint(message, subtitle);

    /// <summary>进程退出时彻底收摊（解订阅 + 关窗口）。</summary>
    public static void Shutdown()
    {
        if (_attached)
        {
            SettingsState.Current.PropertyChanged -= OnSettingsChanged;
            _attached = false;
        }

        Stop();
    }
}
