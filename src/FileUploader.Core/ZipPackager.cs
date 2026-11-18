using System.IO.Compression;

namespace FileUploader.Core;

/// <summary>要写进 zip 的一个本地文件。<paramref name="EntryName"/> 是压缩包内的相对路径。</summary>
public sealed record ZipEntry(string LocalPath, string EntryName);

/// <summary>打包结果。</summary>
public sealed record ZipPackResult(bool Success, long Bytes, int Entries, int Skipped, string? Error)
{
    public static ZipPackResult Fail(string error) => new(false, 0, 0, 0, error);
}

/// <summary>
/// 把一批本地文件打成一个 zip。
/// <para>
/// 「多个文件打包成一个 zip 上传」用的就是它：压缩包只是一次上传的中间产物，
/// 落在临时目录里，传完（或取消、移除）就删掉，不会留在用户的文件夹里。
/// </para>
/// </summary>
public static class ZipPackager
{
    private const int BufferSize = 1024 * 256;

    /// <summary>
    /// 已经压过的格式，打 zip 时直接 stored（不再 deflate）：
    /// 照片/视频/压缩包再压一遍体积几乎不变，只会白烧 CPU（一整文件夹手机照片能差出几分钟）。
    /// </summary>
    private static readonly HashSet<string> IncompressibleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".avif", ".bmp",
        ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v",
        ".mp3", ".aac", ".m4a", ".flac", ".ogg", ".opus",
        ".zip", ".7z", ".rar", ".gz", ".bz2", ".xz", ".zst", ".tgz",
        ".docx", ".xlsx", ".pptx", ".apk", ".jar", ".epub", ".pdf",
    };

    /// <summary>
    /// 生成 zip。<paramref name="completedEntries"/> 每写完一个文件报一次已完成数量（用于界面进度）。
    /// 单个文件读不到（被移动、没权限、被独占）时跳过它并计数，不让整包失败。
    /// </summary>
    public static Task<ZipPackResult> CreateAsync(
        string zipPath,
        IReadOnlyList<ZipEntry> entries,
        IProgress<int>? completedEntries,
        CancellationToken cancellationToken) =>
        // ZipArchive 的写入是同步 IO，放到线程池上跑，别卡 UI 线程。
        Task.Run(() => Create(zipPath, entries, completedEntries, cancellationToken), cancellationToken);

    /// <summary>zip 文件名：取第一个顶层文件夹名；都是散文件时叫「打包.zip」。</summary>
    public static string ChooseFileName(IEnumerable<string?> topLevelFolderNames)
    {
        foreach (var name in topLevelFolderNames)
        {
            var trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            return trimmed.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + ".zip";
        }

        return "打包.zip";
    }

    /// <summary>拼压缩包内的相对路径（丢掉空段、统一成 '/'）。</summary>
    public static string CombineEntryName(params string?[] parts)
    {
        var segments = new List<string>();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            foreach (var segment in part.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                segments.Add(segment);
            }
        }

        return string.Join('/', segments);
    }

    private static ZipPackResult Create(
        string zipPath,
        IReadOnlyList<ZipEntry> entries,
        IProgress<int>? completedEntries,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var completed = 0;
        var skipped = 0;
        try
        {
            using var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        // FileShare.ReadWrite | Delete：正在被别的程序打开（比如日志文件）也能读；
                        // 打不开就跳过，这一条记进 Skipped。
                        using var source = new FileStream(
                            entry.LocalPath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete,
                            BufferSize,
                            FileOptions.SequentialScan);
                        var zipEntry = archive.CreateEntry(
                            entry.EntryName,
                            IsAlreadyCompressed(entry.EntryName) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                        using var target = zipEntry.Open();
                        source.CopyTo(target, BufferSize);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        skipped++;
                        continue;
                    }

                    completed++;
                    completedEntries?.Report(completed);
                }
            }
        }
        catch (OperationCanceledException)
        {
            DeleteQuietly(zipPath);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(zipPath);
            return ZipPackResult.Fail(ex.Message);
        }

        if (completed == 0)
        {
            DeleteQuietly(zipPath);
            return ZipPackResult.Fail("所有文件都读不到（可能已被移动，或者没有权限）。");
        }

        long bytes;
        try
        {
            bytes = new FileInfo(zipPath).Length;
        }
        catch (IOException)
        {
            bytes = 0;
        }

        return new ZipPackResult(true, bytes, completed, skipped, null);
    }

    private static bool IsAlreadyCompressed(string entryName) =>
        IncompressibleExtensions.Contains(Path.GetExtension(entryName));

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
            // 删不掉就当临时文件留着，系统清理时会被带走。
        }
    }
}
