using System.Diagnostics;
using System.Security.Cryptography;
using FileUploader.Core;
using FileUploader.Core.Storage;

namespace FileUploader.App;

/// <summary>
/// 更新分两条路，按"这个程序是怎么装上的"自动选（见 <see cref="InstallLocation"/>）：
/// <list type="bullet">
/// <item>
/// <b>安装版</b>：下载 <c>FileUploader-&lt;版本&gt;-setup.exe</c> → 校验 SHA256 → 静默运行安装包 → 退出。
/// 换 exe、开始菜单快捷方式、卸载项全交给安装器，不会出现"卸载项记着旧版本"这种脏状态；
/// 安装器的 <c>[Run]</c> 会在静默模式下把程序重新拉起来。
/// </item>
/// <item>
/// <b>便携版</b>：只把 <c>FileUploader-&lt;版本&gt;-win-x64.exe</c> 下载到 exe 旁边的
/// <c>update-&lt;版本&gt;\</c> 并校验，<b>绝不替换自己</b> —— 便携版本来就是"用户自己拿着的一份文件"，
/// 程序偷偷换掉它既容易失败（U 盘/只读介质/权限），也不符合便携用户的心理预期。
/// </item>
/// </list>
/// </summary>
internal static class Updater
{
    /// <summary>下载结果。<see cref="IsInstaller"/> 表示下的是安装包。</summary>
    public sealed record DownloadResult(bool Success, string? Path, string? Error, bool IsInstaller = false);

    /// <summary>启动安装包 / 替换失败的结果。</summary>
    public sealed record ApplyResult(bool Success, string? Error);

    /// <summary>
    /// 选一个能写的下载目录。便携版优先放 exe 旁边（便携语义：所有东西都在一个文件夹里），
    /// 写不进去（U 盘只读、目录被锁、沙箱）再退到「下载」文件夹、最后退到 %TEMP%。
    /// </summary>
    public static string ResolveStagingDirectory(string version, InstallKind kind, out string? error)
    {
        var candidates = new List<string>();
        if (kind == InstallKind.Installed)
        {
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FileUploader",
                "update-" + version));
        }
        else
        {
            candidates.Add(Path.Combine(AppPaths.ExeDirectory, "update-" + version));
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                "FileUploader-update-" + version));
        }

        candidates.Add(Path.Combine(Path.GetTempPath(), "FileUploader", "update-" + version));

        var failures = new List<string>();
        foreach (var candidate in candidates)
        {
            if (TryPrepareDirectory(candidate, out var message))
            {
                error = null;
                return candidate;
            }

            failures.Add($"{candidate}（{message}）");
        }

        error = "下载目录都写不进去：" + string.Join("；", failures);
        return string.Empty;
    }

    /// <summary>
    /// 下载新版：安装版拿 setup.exe，便携版拿单文件 exe。
    /// 下完都校验 SHA256；校验不过就把文件删掉，绝不留下半个可执行文件。
    /// </summary>
    public static async Task<DownloadResult> DownloadAsync(
        UpdateCheckResult update,
        InstallKind kind,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var installer = kind == InstallKind.Installed;
        var name = installer ? update.InstallerName : update.AssetName;
        var url = installer ? update.InstallerUrl : update.AssetUrl;
        var sha = installer ? update.InstallerSha256 : update.AssetSha256;
        var size = installer ? update.InstallerSize : update.AssetSize;

        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(name))
        {
            return new DownloadResult(false, null, installer
                ? "这个 Release 没有安装包（…-setup.exe），请到发布页手动下载。"
                : "这个 Release 里没有 win-x64 单文件 exe，请到发布页手动下载。");
        }

        var version = update.LatestVersion ?? "latest";
        var directory = ResolveStagingDirectory(version, kind, out var directoryError);
        if (directory.Length == 0)
        {
            return new DownloadResult(false, null, $"{directoryError}。请到发布页手动下载。", installer);
        }

        var target = Path.Combine(directory, name);

        // 下载过程按"已下载 / 总大小"折成 0..1 报给界面。
        IProgress<long>? downloadProgress = size > 0
            ? new Progress<long>(bytes => progress?.Report(Math.Min(1d, bytes / (double)size)))
            : null;

        var download = await ObjectDownloader
            .DownloadAsync(url, target, downloadProgress, cancellationToken)
            .ConfigureAwait(false);
        if (!download.Success)
        {
            return new DownloadResult(false, null, download.Error ?? "下载失败。", installer);
        }

        progress?.Report(1);

        if (string.IsNullOrEmpty(sha))
        {
            return new DownloadResult(true, target, null, installer);
        }

        var actual = await Sha256Async(target, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, sha, StringComparison.OrdinalIgnoreCase))
        {
            DeleteQuietly(target);   // 校验不过的文件别留着（90 MB 的垃圾）
            return new DownloadResult(
                false,
                null,
                $"校验失败：下载到的文件 SHA256 是 {Preview(actual)}，Release 公布的是 {Preview(sha)}。已丢弃，没有替换任何文件。",
                installer);
        }

        return new DownloadResult(true, target, null, installer);
    }

    /// <summary>
    /// 静默跑安装包（只有安装版会走到这里）。
    /// 返回成功后调用方应立刻退出：安装器会把 exe 换掉，并在静默模式下自己把程序重新拉起来。
    /// </summary>
    public static ApplyResult RunInstaller(string setupPath)
    {
        if (!File.Exists(setupPath))
        {
            return new ApplyResult(false, $"安装包不见了：{setupPath}");
        }

        try
        {
            var start = new ProcessStartInfo(setupPath)
            {
                // /CLOSEAPPLICATIONS 只是兜底：我们紧接着就退出，正常情况下安装器开始复制时进程已经没了。
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(setupPath) ?? AppPaths.ExeDirectory,
            };

            return Process.Start(start) is null
                ? new ApplyResult(false, "没能启动安装包，请手动运行它。")
                : new ApplyResult(true, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new ApplyResult(false, $"启动安装包失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 启动时清理上一次更新留下的 <c>*.old</c>（老版本自替换时会留，现在只有安装版会碰到旧安装器留下的）。
    /// </summary>
    public static void CleanupPreviousVersion()
    {
        var current = Environment.ProcessPath;
        if (string.IsNullOrEmpty(current))
        {
            return;
        }

        DeleteQuietly(current + ".old");
    }

    private static bool TryPrepareDirectory(string directory, out string message)
    {
        try
        {
            Directory.CreateDirectory(directory);

            // 建得出来不代表写得进去（只读介质、被策略拦住的情况都存在），真写一个探针文件才算数。
            var probe = Path.Combine(directory, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            message = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            message = ex.Message;
            return false;
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Preview(string hash) => hash.Length <= 12 ? hash : hash[..12] + "…";

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉（还占着/没权限）不影响使用，下次启动再试。
        }
    }
}
