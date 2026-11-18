using System.Security;
using FileUploader.Core;
using Microsoft.Win32;

namespace FileUploader.App;

/// <summary>
/// 「开机自启」的注册：写 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>。
/// <para>
/// 为什么用 Run 键而不是开始菜单快捷方式：任务管理器的「启动」页能直接看到并关掉，
/// 也不容易被"清理"工具连快捷方式一起删；写当前用户范围，切换开关时不需要管理员权限。
/// </para>
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Run 键里的值名（任务管理器「启动」页显示的就是它）。</summary>
    public const string ValueName = "FileUploader";

    /// <summary>本进程 exe 的完整路径（单文件发布时也是真实 exe，不是解压目录）。</summary>
    private static string ExecutablePath => Environment.ProcessPath ?? string.Empty;

    /// <summary>Run 键里那行命令；路径必须带引号，否则带空格的目录会被拆成参数。</summary>
    private static string CommandLine => $"\"{ExecutablePath}\"";

    /// <summary>Run 键里当前写的是什么（没有就是 null）。</summary>
    public static string? ReadRegisteredCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>当前是否已经注册，并且指向的正是这次运行的 exe。</summary>
    public static bool IsRegisteredToThisExe() =>
        string.Equals(ReadRegisteredCommand()?.Trim(), CommandLine, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 打开/关闭自启。失败时返回 false 并给出原因，调用方负责提示用户并把开关回滚。
    /// </summary>
    public static bool TrySet(bool enabled, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                error = "打不开当前用户的启动项注册表键。";
                return false;
            }

            if (enabled)
            {
                if (string.IsNullOrEmpty(ExecutablePath))
                {
                    error = "拿不到本程序的 exe 路径。";
                    return false;
                }

                key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 启动时校正，以「注册表里的实际状态」为准：
    /// <list type="bullet">
    /// <item>配置说关着 → 什么都不做（不抢用户在别处自己加的启动项）。</item>
    /// <item>注册项指向别的路径（exe 换目录/改名） → 按当前路径重写。</item>
    /// <item>注册项不在了（用户在任务管理器的「启动」页禁用、或被清理工具删掉） → 返回 <see cref="Removed"/>，
    /// 由调用方把配置同步成关闭 —— 不能偷偷加回来。</item>
    /// </list>
    /// </summary>
    public static StartupSyncResult Sync(AppSettings settings, out string? error)
    {
        error = null;
        if (!settings.StartWithWindows)
        {
            return StartupSyncResult.Disabled;
        }

        var current = ReadRegisteredCommand();
        if (string.IsNullOrWhiteSpace(current))
        {
            return StartupSyncResult.Removed;
        }

        if (IsRegisteredToThisExe())
        {
            return StartupSyncResult.UpToDate;
        }

        return TrySet(true, out error) ? StartupSyncResult.Rewritten : StartupSyncResult.Failed;
    }
}

/// <summary>启动时校正开机自启的结果。</summary>
internal enum StartupSyncResult
{
    /// <summary>配置里没开自启，未做任何事。</summary>
    Disabled,

    /// <summary>已经指向当前 exe，不用改。</summary>
    UpToDate,

    /// <summary>原来指向别的路径，已按当前 exe 重写。</summary>
    Rewritten,

    /// <summary>注册项已经不存在（被任务管理器或清理工具删了），需要把配置同步为关闭。</summary>
    Removed,

    /// <summary>写注册表失败。</summary>
    Failed,
}
