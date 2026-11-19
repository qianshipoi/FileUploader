using System.Security;
using FileUploader.Core;
using Microsoft.Win32;

namespace FileUploader.App;

/// <summary>这个程序是怎么装上的 —— 决定「立即更新」是静默升级还是只下载。</summary>
internal enum InstallKind
{
    /// <summary>
    /// 便携版：exe 旁边有 <c>portable.txt</c>，或者只是把 exe 拷出来跑 / 放 U 盘 / 被挪过目录。
    /// 这种副本不自动替换自己，只下载新版交给用户手动换。
    /// </summary>
    Portable,

    /// <summary>安装版：安装包在注册表里记下的安装目录，正是当前 exe 所在目录。</summary>
    Installed,
}

/// <summary>
/// 安装位置的判定：**不能只看有没有 <c>portable.txt</c>**。
/// <para>
/// 用户很可能把便携 exe 拷到桌面直接跑（没有 portable.txt），如果按"没有标记就是安装版"来判，
/// 程序就会去自动替换那个 exe —— 而他根本没"安装"过任何东西。
/// 所以安装包会写一条注册表标记（<c>HKCU\Software\FileUploader\InstallPath</c>），
/// 只有"标记里的目录 == 当前 exe 所在目录"才算安装版；拷走的副本自然对不上，按便携版处理。
/// </para>
/// </summary>
internal static class InstallLocation
{
    /// <summary>安装包写的注册表键。</summary>
    public const string RegistryKeyPath = @"Software\FileUploader";

    /// <summary>安装目录的值名。</summary>
    public const string InstallPathValueName = "InstallPath";

    /// <summary>安装包写下的安装目录（没有安装记录时为 null）。</summary>
    public static string? RegisteredInstallPath
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: false);
                return (key?.GetValue(InstallPathValueName) as string)?.Trim();
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                return null;
            }
        }
    }

    /// <summary>当前进程属于哪种安装方式。</summary>
    public static InstallKind Kind
    {
        get
        {
            // 用户显式放了 portable.txt：一切以它为准，哪怕注册表里有安装记录。
            if (AppPaths.IsPortable)
            {
                return InstallKind.Portable;
            }

            var registered = RegisteredInstallPath;
            if (string.IsNullOrWhiteSpace(registered))
            {
                return InstallKind.Portable;
            }

            return SamePath(registered, AppPaths.ExeDirectory) ? InstallKind.Installed : InstallKind.Portable;
        }
    }

    /// <summary>是不是安装版（自动更新走静默升级）。</summary>
    public static bool IsInstalled => Kind == InstallKind.Installed;

    private static bool SamePath(string left, string right)
    {
        try
        {
            static string Normalize(string path) =>
                Path.GetFullPath(path.Trim()).TrimEnd('\\', '/');

            return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
