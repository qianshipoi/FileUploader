using System.Runtime.InteropServices;

namespace FileUploader.App;

/// <summary>
/// 显示 / 激活窗口的 Win32 小工具。
/// 托盘点击、简洁模式返回、以及"第二次启动时把已在运行的窗口叫出来"都走这里。
/// </summary>
internal static class WindowForeground
{
    public const int SwHide = 0;
    public const int SwShowNormal = 1;
    public const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>窗口当前是否可见（不看是否被遮挡）。</summary>
    public static bool IsVisible(IntPtr handle) => handle != IntPtr.Zero && IsWindowVisible(handle);

    /// <summary>显示（最小化时先还原）并抢到前台。</summary>
    public static void ShowAndActivate(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        ShowWindow(handle, IsIconic(handle) ? SwRestore : SwShowNormal);
        Force(handle);
    }

    /// <summary>
    /// 把窗口真正提到前台，返回是否成功。
    /// <para>
    /// 踩过的坑：点托盘图标时，用户输入是发给 shell（explorer）的，系统不认为我们进程拿到了"最后一次输入"，
    /// 于是 <c>SetForegroundWindow</c> 被前台锁定规则静默拒绝 —— 返回 false、窗口纹丝不动，
    /// 表现就是「窗口压在别的窗口下面时点托盘没反应」（实测：普通 SetForegroundWindow = false，
    /// 临时置顶再取消也 = false）。Win32 的标准解法是先 <c>AttachThreadInput</c> 挂到当前前台线程上，
    /// 让两者的输入队列合并，再 BringWindowToTop + SetForegroundWindow（实测 = true）。
    /// </para>
    /// </summary>
    public static bool Force(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        if (SetForegroundWindow(handle) || GetForegroundWindow() == handle)
        {
            return true;
        }

        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, IntPtr.Zero);
        var currentThread = GetCurrentThreadId();
        var attached = false;
        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attached = AttachThreadInput(currentThread, foregroundThread, true);
            }

            BringWindowToTop(handle);
            SetForegroundWindow(handle);
        }
        catch (Exception)
        {
            // 纯 Win32 调用不该抛异常，但绝不能让"叫回窗口"这一步把程序搞崩。
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        if (GetForegroundWindow() != handle)
        {
            CrashLog.Write("窗口没能拿到前台", new InvalidOperationException(
                $"hwnd=0x{handle.ToInt64():X} foreground=0x{GetForegroundWindow().ToInt64():X} attached={attached}"));
            return false;
        }

        return true;
    }
}
