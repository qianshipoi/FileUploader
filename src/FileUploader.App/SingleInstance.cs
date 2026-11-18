using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FileUploader.App;

/// <summary>
/// 单实例：同一份 exe 只允许跑一份。
/// <para>
/// 为什么需要：用户双击了两次下载来的 exe，托盘里就多出一份后台进程，主窗口不出现，
/// 看起来像"闪退了"。第二次启动现在改成把已经在跑的那份窗口叫到前台，然后自己退出。
/// </para>
/// <para>
/// 锁名带 exe 完整路径的哈希：开发构建目录、便携副本、安装目录互不影响
/// （回归脚本经常同时跑好几份，不能互相挡住）。
/// </para>
/// </summary>
internal static class SingleInstance
{
    private static Mutex? _mutex;

    /// <summary>
    /// 尝试成为唯一实例。返回 false 表示已经有同一份 exe 在跑
    /// （调用方应试着 <see cref="ActivateExisting"/>，成功就退出自己）。
    /// </summary>
    public static bool TryAcquire()
    {
        var exePath = (Environment.ProcessPath ?? "FileUploader").ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exePath)))[..16];

        try
        {
            var mutex = new Mutex(initiallyOwned: true, $"Local\\FileUploader.Single.{hash}", out var created);
            if (created)
            {
                _mutex = mutex;
                return true;
            }

            mutex.Dispose();
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // 检查失败就按"可以多开"处理：宁可多一份进程，也不能把程序挡在门外。
            CrashLog.Write("单实例检查失败（按可多开处理）", ex);
            return true;
        }
    }

    /// <summary>
    /// 把已经在运行的那份叫到前台。成功返回 true（调用方随后应直接退出）。
    /// 对方可能还在启动（窗口还没建出来），最多等 3 秒。
    /// </summary>
    public static bool ActivateExisting()
    {
        for (var attempt = 0; attempt < 15; attempt++)
        {
            var handle = FindExistingMainWindow();
            if (handle != IntPtr.Zero)
            {
                WindowForeground.ShowAndActivate(handle);
                return true;
            }

            Thread.Sleep(200);
        }

        return false;
    }

    /// <summary>
    /// 主动放掉实例锁。
    /// 自动更新要「先起新版进程、再退自己」，这时必须先把锁交出去，
    /// 否则新版会以为自己是被挡住的那一份、直接退出。
    /// </summary>
    public static void Release()
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (Exception)
        {
            // 没持有（比如已经释放过）就忽略。
        }

        _mutex?.Dispose();
        _mutex = null;
    }

    private static IntPtr FindExistingMainWindow()
    {
        var processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
        var selfPath = Environment.ProcessPath;
        var found = IntPtr.Zero;

        EnumWindows(
            (handle, _) =>
            {
                GetWindowThreadProcessId(handle, out var pid);
                if (pid == 0)
                {
                    return true;
                }

                try
                {
                    using var process = Process.GetProcessById((int)pid);
                    if (!string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    // 同名进程可能来自别的目录（开发构建 vs 便携副本），比一下 exe 路径。
                    var modulePath = process.MainModule?.FileName;
                    if (modulePath is not null && selfPath is not null &&
                        !string.Equals(modulePath, selfPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                    return true; // 拿不到进程信息就跳过，别因为权限问题误判
                }

                var className = new StringBuilder(256);
                GetClassNameW(handle, className, className.Capacity);
                if (!string.Equals(className.ToString(), "WinUIDesktopWin32WindowClass", StringComparison.Ordinal))
                {
                    return true;
                }

                found = handle;
                return false;
            },
            IntPtr.Zero);

        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder className, int maxCount);
}
