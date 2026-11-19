using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileUploader.Core.Storage;

/// <summary>一个已传完的分片。重试时靠它跳过已传的部分。</summary>
public sealed record MultipartPart(int PartNumber, long Size, string ETag);

/// <summary>
/// 一次未完成的分片上传（断点续传的全部状态）。
/// <para>
/// 落盘后可以跨进程恢复，所以这里必须同时记清"服务端身份"（endpoint / 桶 / 协议 / uploadId / 对象键）
/// 和"本地文件身份"（路径 / 长度 / 最后写入时间）：三者任一变了就不能再续，
/// 否则会拿一个过期的 uploadId 去传另一个文件的内容，甚至拼出一个内容错乱的对象。
/// </para>
/// </summary>
public sealed record MultipartSession
{
    /// <summary>当前状态文件格式版本（读到别的版本一律当成不存在）。</summary>
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>Endpoint（已归一化，不含协议前缀）。</summary>
    public string Endpoint { get; init; } = string.Empty;

    public string Bucket { get; init; } = string.Empty;

    public StorageProtocol Protocol { get; init; }

    /// <summary>InitiateMultipartUpload 返回的 UploadId。</summary>
    public string UploadId { get; init; } = string.Empty;

    /// <summary>对象键（续传必须沿用同一个键，否则等于换了个对象）。</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>上传时用的 Content-Type（Initiate 时已经写进服务端，续传不用再传）。</summary>
    public string ContentType { get; init; } = string.Empty;

    /// <summary>原始文件名（界面上要显示）。</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>本地源文件完整路径。</summary>
    public string LocalPath { get; init; } = string.Empty;

    /// <summary>源文件长度（字节）。</summary>
    public long FileLength { get; init; }

    /// <summary>源文件的最后写入时间（UTC ticks）。</summary>
    public long LastWriteUtcTicks { get; init; }

    /// <summary>分片大小（字节）。续传时必须沿用，否则分片边界变了、已传分片对不上。</summary>
    public long PartSize { get; init; }

    /// <summary>会话创建时间（界面上按它排序，并用于清理过期会话）。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>已传完的分片。</summary>
    public List<MultipartPart> Parts { get; init; } = [];

    /// <summary>已经传上去的字节数（用于显示"已传 xx%"）。</summary>
    public long UploadedBytes => Parts.Sum(p => p.Size);

    /// <summary>这个会话是不是属于当前配置（换桶 / 换服务后旧会话就不该再出现）。</summary>
    public bool MatchesSettings(AppSettings settings) =>
        Protocol == settings.Protocol
        && string.Equals(Endpoint, settings.NormalizedEndpoint, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Bucket, settings.Bucket.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>源文件还是当初那一个（路径、长度、最后写入时间都一致）。</summary>
    public bool MatchesFile(string localPath, long fileLength, long lastWriteUtcTicks) =>
        FileLength == fileLength
        && LastWriteUtcTicks == lastWriteUtcTicks
        && PathsEqual(LocalPath, localPath);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path ?? string.Empty;
        }
    }
}

/// <summary>
/// 分片上传会话的落盘：<c>%APPDATA%\FileUploader\uploads\&lt;id&gt;.json</c>（便携模式在 exe 旁的 data 目录）。
/// <para>
/// 一个对象键一份文件，文件名是 endpoint + 桶 + 协议 + 键的 SHA-256 前 32 位 ——
/// 这样同一个键永远落到同一个文件，不用在 json 里再存一份 id。
/// </para>
/// </summary>
public sealed class MultipartSessionStore
{
    private const int IdLength = 32;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly object Gate = new();

    /// <summary>程序运行时用的实例（跟着配置目录走）。</summary>
    public static MultipartSessionStore Default { get; } = new(AppPaths.UploadsDirectory);

    /// <summary>用一个指定目录（自测时用临时目录，别碰用户的真状态）。</summary>
    public MultipartSessionStore(string directoryPath) => DirectoryPath = directoryPath;

    /// <summary>状态文件所在目录。</summary>
    public string DirectoryPath { get; }

    /// <summary>会话 id（也是状态文件名）。</summary>
    public static string IdFor(AppSettings settings, string key) =>
        IdFor(settings.NormalizedEndpoint, settings.Bucket, settings.Protocol, key);

    /// <summary>会话 id（同上，参数拆开给启动时读旧会话用）。</summary>
    public static string IdFor(string endpoint, string bucket, StorageProtocol protocol, string key)
    {
        var raw = string.Join(
            '|',
            (endpoint ?? string.Empty).Trim().ToLowerInvariant(),
            (bucket ?? string.Empty).Trim().ToLowerInvariant(),
            ((int)protocol).ToString(CultureInfo.InvariantCulture),
            key ?? string.Empty);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant()[..IdLength];
    }

    /// <summary>某个会话的状态文件路径。</summary>
    public string PathFor(string id) => Path.Combine(DirectoryPath, id + ".json");

    /// <summary>读取一个会话；文件不存在、损坏或版本不符时返回 <c>null</c>。</summary>
    public MultipartSession? TryLoad(string id)
    {
        lock (Gate)
        {
            try
            {
                var path = PathFor(id);
                if (!File.Exists(path))
                {
                    return null;
                }

                var session = JsonSerializer.Deserialize<MultipartSession>(File.ReadAllText(path), JsonOptions);
                return session is { Version: MultipartSession.CurrentVersion, UploadId.Length: > 0 } ? session : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 保存会话。返回 <c>null</c> 表示成功，否则是给用户看的失败原因 ——
    /// 写不进去不影响这次上传，只是中断以后没法续传，所以要让调用方有机会说明。
    /// </summary>
    public string? Save(MultipartSession session)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = PathFor(IdFor(session.Endpoint, session.Bucket, session.Protocol, session.Key));
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(session, JsonOptions), Encoding.UTF8);
                File.Move(temp, path, overwrite: true);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ex.Message;
            }
        }
    }

    /// <summary>删除会话状态（服务端的分片要不要清由调用方决定）。</summary>
    public void Delete(string id)
    {
        lock (Gate)
        {
            try
            {
                var path = PathFor(id);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 删不掉就留着：下次启动会再看到它，用户点「移除」时会重试清理。
            }
        }
    }

    /// <summary>读出所有会话（读不出来的文件直接跳过，不要把启动搞崩）。</summary>
    public IReadOnlyList<MultipartSession> LoadAll()
    {
        lock (Gate)
        {
            try
            {
                if (!Directory.Exists(DirectoryPath))
                {
                    return [];
                }

                var sessions = new List<MultipartSession>();
                foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
                {
                    try
                    {
                        var session = JsonSerializer.Deserialize<MultipartSession>(File.ReadAllText(path), JsonOptions);
                        if (session is { Version: MultipartSession.CurrentVersion, UploadId.Length: > 0 })
                        {
                            sessions.Add(session);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        // 单个文件坏了不影响其它会话。
                    }
                }

                return sessions;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }
}
