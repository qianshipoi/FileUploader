using System.Diagnostics;
using System.Security.Cryptography;
using FileUploader.Core;
using FileUploader.Core.Storage;

namespace FileUploader.App;

/// <summary>
/// 自更新：下载新版单文件 exe → 校验 SHA256 → 换掉当前 exe → 启动新进程。
/// <para>
/// 关键点：运行中的 exe <b>可以</b>改名（实测 Move 成功，只是不能覆盖写入）。
/// 所以不需要额外的 helper 脚本/批处理，顺序是
/// 「拷成 FileUploader.new.exe → 当前 exe 改名成 .old → .new 顶替正式名 → 启动新版」，
/// 中间任何一步失败都会把 .old 改回去，不会留下一个打不开的目录。
/// </para>
/// </summary>
internal static class Updater
{
    /// <summary>单文件版下载到本地后的结果。</summary>
    public sealed record DownloadResult(bool Success, string? Path, string? Error);

    /// <summary>替换并重启的结果。</summary>
    public sealed record ApplyResult(bool Success, string? Error);

    /// <summary>下载新版 exe 到临时目录并校验 SHA256（Release 里没公布校验和时跳过校验）。</summary>
    public static async Task<DownloadResult> DownloadAsync(
        UpdateCheckResult update,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(update.AssetUrl) || string.IsNullOrEmpty(update.AssetName))
        {
            return new DownloadResult(false, null, "这个 Release 里没有 win-x64 单文件 exe，请手动下载替换。");
        }

        var directory = Path.Combine(Path.GetTempPath(), "FileUploader", "update-" + (update.LatestVersion ?? "latest"));
        var target = Path.Combine(directory, "FileUploader.exe");
        Directory.CreateDirectory(directory);

        // 下载过程按"已下载 / 总大小"折成 0..1 报给界面。
        IProgress<long>? downloadProgress = update.AssetSize > 0
            ? new Progress<long>(bytes => progress?.Report(Math.Min(1d, bytes / (double)update.AssetSize)))
            : null;

        var download = await ObjectDownloader
            .DownloadAsync(update.AssetUrl, target, downloadProgress, cancellationToken)
            .ConfigureAwait(false);
        if (!download.Success)
        {
            return new DownloadResult(false, null, download.Error ?? "下载失败。");
        }

        progress?.Report(1);

        if (string.IsNullOrEmpty(update.AssetSha256))
        {
            return new DownloadResult(true, target, null);
        }

        var actual = await Sha256Async(target, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, update.AssetSha256, StringComparison.OrdinalIgnoreCase))
        {
            DeleteQuietly(target);   // 校验不过的文件别留着（90 MB 的垃圾）
            return new DownloadResult(false, null,
                $"校验失败：下载到的文件 SHA256 是 {Preview(actual)}，Release 公布的是 {Preview(update.AssetSha256)}。已丢弃，没有替换任何文件。");
        }

        return new DownloadResult(true, target, null);
    }

    /// <summary>用下载好的 exe 替换当前程序并启动它（调用方随后应立刻退出）。</summary>
    public static ApplyResult ApplyAndRestart(string downloadedExe)
    {
        var current = Environment.ProcessPath;
        if (string.IsNullOrEmpty(current) || !File.Exists(current))
        {
            return new ApplyResult(false, "拿不到当前 exe 的路径。");
        }

        var directory = Path.GetDirectoryName(current);
        if (string.IsNullOrEmpty(directory))
        {
            return new ApplyResult(false, "拿不到当前 exe 所在目录。");
        }

        var incoming = Path.Combine(directory, "FileUploader.new.exe");
        var backup = current + ".old";

        try
        {
            File.Copy(downloadedExe, incoming, overwrite: true);

            if (File.Exists(backup))
            {
                // 上一次更新留下的备份：删不掉就算了（下次启动还会再试）。
                DeleteQuietly(backup);
            }

            File.Move(current, backup);
            try
            {
                File.Move(incoming, current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 顶替失败：把原文件改回去，绝不能留一个没有 exe 的目录。
                File.Move(backup, current);
                return new ApplyResult(false, $"替换失败，已还原原文件：{ex.Message}");
            }

            // 先把单实例锁交出去：新进程马上要起来，还占着锁的话它会以为自己是"第二份"而直接退出。
            SingleInstance.Release();

            var started = Process.Start(new ProcessStartInfo(current)
            {
                UseShellExecute = true,
                WorkingDirectory = directory,
            });

            return started is null
                ? new ApplyResult(false, $"新版已经替换好了，但没能自动启动，请手动打开 {current}。")
                : new ApplyResult(true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(incoming);
            return new ApplyResult(false,
                $"更新失败：{ex.Message}。如果程序装在 Program Files 这类需要管理员权限的目录，请手动下载新版替换。");
        }
    }

    /// <summary>
    /// 启动时清理上一次更新留下的 <c>*.old</c>。
    /// 那个文件是旧版 exe，新进程跑起来后它就不再被占用，可以删了。
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
