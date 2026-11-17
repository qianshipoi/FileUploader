using System.Text.Json;

namespace FileUploader.Core;

/// <summary>一条已上传记录（重启后仍可复制链接）。</summary>
public sealed record HistoryEntry(string FileName, string Key, string Url, long Size, DateTimeOffset UploadedAt);

/// <summary>
/// 上传历史：<c>%APPDATA%\FileUploader\history.json</c>。
/// 只保存成功上传的结果，最多 <see cref="MaxEntries"/> 条（新的在前）。
/// </summary>
public static class HistoryStore
{
    /// <summary>最多保留的条数。</summary>
    public const int MaxEntries = 200;

    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>历史文件完整路径（便携模式在 exe 旁的 data 目录）。</summary>
    public static string FilePath => AppPaths.HistoryPath;

    /// <summary>读取历史（文件不存在或损坏时返回空列表）。</summary>
    public static IReadOnlyList<HistoryEntry> Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return Array.Empty<HistoryEntry>();
                }

                var json = File.ReadAllText(FilePath);
                var entries = JsonSerializer.Deserialize<List<HistoryEntry>>(json, JsonOptions);
                return entries is null ? Array.Empty<HistoryEntry>() : entries.Take(MaxEntries).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return Array.Empty<HistoryEntry>();
            }
        }
    }

    /// <summary>插入一条记录（按 URL 去重，最新的排在最前面）。</summary>
    public static void Add(HistoryEntry entry)
    {
        lock (Gate)
        {
            var entries = new List<HistoryEntry>(Load());
            entries.RemoveAll(e => string.Equals(e.Url, entry.Url, StringComparison.Ordinal));
            entries.Insert(0, entry);
            Write(entries);
        }
    }

    /// <summary>按 URL 删除一条记录。</summary>
    public static void Remove(string url)
    {
        lock (Gate)
        {
            var entries = new List<HistoryEntry>(Load());
            if (entries.RemoveAll(e => string.Equals(e.Url, url, StringComparison.Ordinal)) > 0)
            {
                Write(entries);
            }
        }
    }

    /// <summary>清空历史。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 删除失败时保持文件原样，下一次写入会覆盖。
            }
        }
    }

    private static void Write(List<HistoryEntry> entries)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(entries.Take(MaxEntries), JsonOptions);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, FilePath, overwrite: true);
    }
}
