namespace FileUploader.Core.Storage;

/// <summary>
/// 大文件分片上传 + 断点续传。
/// <para>
/// 分片状态落在 <see cref="MultipartSessionStore"/> 里（每个对象键一份），
/// 每传完一片就写一次盘，所以网络中断、程序崩掉、用户点取消之后，
/// 再传同一个文件只会补上没传完的那几片，不会从头再来。
/// </para>
/// <para>
/// 判定能不能续传看三件事：同一个配置（endpoint / 桶 / 协议）、同一个对象键、
/// 同一个本地文件（路径 + 长度 + 最后写入时间）。任何一项变了就当新上传。
/// </para>
/// </summary>
public sealed class MultipartUploader
{
    /// <summary>达到这个大小就走分片上传（再小的单次 PUT 更快，也少三次往返）。</summary>
    public const long ThresholdBytes = 8L * 1024 * 1024;

    /// <summary>默认分片大小。</summary>
    public const long PreferredPartSize = 8L * 1024 * 1024;

    /// <summary>
    /// 单片最小体积：S3 规定除最后一片外都不能小于 5 MiB
    /// （阿里云允许更小，但统一按严的来，两边都能用）。
    /// </summary>
    public const long MinPartSize = 5L * 1024 * 1024;

    /// <summary>单个上传最多允许多少片（S3 与 OSS 都是 10000）。</summary>
    public const int MaxParts = 10000;

    private const int PartAttempts = 3;

    private readonly AppSettings _settings;
    private readonly MultipartSessionStore _store;
    private ObjectStorageClient? _client;

    /// <summary>构造一个上传器（不会立刻建 HTTP 客户端，找"能不能续传"时不需要密钥可用）。</summary>
    public MultipartUploader(AppSettings settings, MultipartSessionStore? store = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _store = store ?? MultipartSessionStore.Default;
    }

    private ObjectStorageClient Client => _client ??= new ObjectStorageClient(_settings);

    /// <summary>最近一次写续传状态失败的原因（写不进去不影响上传，但中断后就没法续传了）。</summary>
    public string? LastResumeStateError { get; private set; }

    /// <summary>这么大的文件才值得分片。</summary>
    public static bool ShouldUseMultipart(long fileLength) => fileLength >= ThresholdBytes;

    /// <summary>
    /// 选分片大小：默认 8 MiB，文件大到 8 MiB × 10000 装不下时按需要放大（向上取整到 MiB）。
    /// </summary>
    public static long ChoosePartSize(long fileLength)
    {
        if (fileLength <= 0)
        {
            return PreferredPartSize;
        }

        const long mib = 1024L * 1024;
        var needed = (fileLength + MaxParts - 1) / MaxParts;
        var size = Math.Max(PreferredPartSize, needed);
        size = (size + mib - 1) / mib * mib;
        return Math.Max(size, MinPartSize);
    }

    /// <summary>这个文件会被切成几片。</summary>
    public static int PartCount(long fileLength, long partSize) =>
        fileLength <= 0 || partSize <= 0 ? 0 : (int)((fileLength + partSize - 1) / partSize);

    /// <summary>第 <paramref name="partNumber"/> 片（从 1 开始）在文件里的偏移与长度。</summary>
    public static (long Offset, long Length) PartRange(long fileLength, long partSize, int partNumber)
    {
        var offset = partSize * (partNumber - 1L);
        var length = Math.Min(partSize, fileLength - offset);
        return (offset, Math.Max(0, length));
    }

    /// <summary>
    /// 这个本地文件有没有未完成的分片上传。有的话**必须沿用它的对象键**再传，
    /// 否则等于换了个对象，之前传的分片就白传了（服务端还会一直挂着）。
    /// </summary>
    public MultipartSession? FindPending(string localPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
            {
                return null;
            }

            var info = new FileInfo(localPath);
            foreach (var session in _store.LoadAll())
            {
                if (!session.MatchesSettings(_settings))
                {
                    continue;
                }

                if (session.MatchesFile(localPath, info.Length, info.LastWriteTimeUtc.Ticks))
                {
                    return session;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到本地文件信息就当没有可续传的会话（上传流程自己会报错）。
        }

        return null;
    }

    /// <summary>
    /// 上传整个文件：有可续传的会话就接着传，否则初始化一个新会话。
    /// </summary>
    /// <param name="localPath">本地源文件。</param>
    /// <param name="key">对象键（续传时必须是上次那个）。</param>
    /// <param name="contentType">对象 Content-Type（只在初始化时生效）。</param>
    /// <param name="progress">已上传总字节数（含续传前传完的部分）。</param>
    /// <param name="cancellationToken">取消：只停手，不放弃会话，之后还能接着传。</param>
    public async Task<StorageResult> UploadAsync(
        string localPath,
        string key,
        string contentType,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var (result, restart) = await UploadOnceAsync(localPath, key, contentType, progress, cancellationToken)
                .ConfigureAwait(false);

            // 服务端已经没有这个 uploadId 了（过期、被其它客户端清理、或上一轮其实已经合成完）：
            // 丢掉本地会话重开一个，只重开这一次，避免无谓的循环。
            if (!restart || attempt >= 1)
            {
                return result;
            }

            _store.Delete(MultipartSessionStore.IdFor(_settings, key));
        }
    }

    /// <summary>放弃一个本地会话并清掉服务端残留的分片（"移除"这个文件时用）。</summary>
    public async Task AbortAsync(MultipartSession session, CancellationToken cancellationToken)
    {
        _store.Delete(MultipartSessionStore.IdFor(session.Endpoint, session.Bucket, session.Protocol, session.Key));

        try
        {
            await Client.AbortMultipartUploadAsync(session.Key, session.UploadId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // 服务端删不掉（离线/权限）也不该拦住用户移除记录：本地状态已经清了，
            // 剩下的分片由存储服务自己的生命周期规则回收。
        }
    }

    private async Task<(StorageResult Result, bool Restart)> UploadOnceAsync(
        string localPath,
        string key,
        string contentType,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(localPath);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return (StorageResult.Fail(0, "FileNotFound", ex.Message), false);
        }

        if (!info.Exists)
        {
            return (StorageResult.Fail(0, "FileNotFound", $"本地文件不存在：{localPath}"), false);
        }

        var fileLength = info.Length;
        var lastWriteTicks = info.LastWriteTimeUtc.Ticks;
        var id = MultipartSessionStore.IdFor(_settings, key);

        var session = _store.TryLoad(id);
        if (session is not null
            && (!session.MatchesSettings(_settings) || !session.MatchesFile(localPath, fileLength, lastWriteTicks)))
        {
            // 文件被改过、或者配置换了：旧 uploadId 已经对不上了，留着只会传出一个内容错乱的对象。
            _store.Delete(id);
            session = null;
        }

        // 续传必须沿用当初的分片大小：边界一变，已传的分片就跟新的切法对不上。
        var partSize = session?.PartSize is > 0 ? session.PartSize : ChoosePartSize(fileLength);

        if (session is null)
        {
            var initiate = await Client.InitiateMultipartUploadAsync(key, contentType, cancellationToken).ConfigureAwait(false);
            if (!initiate.Result.Success || string.IsNullOrEmpty(initiate.UploadId))
            {
                return (initiate.Result, false);
            }

            session = new MultipartSession
            {
                Endpoint = _settings.NormalizedEndpoint,
                Bucket = _settings.Bucket.Trim(),
                Protocol = _settings.Protocol,
                UploadId = initiate.UploadId!,
                Key = key,
                ContentType = contentType,
                FileName = Path.GetFileName(localPath),
                LocalPath = Path.GetFullPath(localPath),
                FileLength = fileLength,
                LastWriteUtcTicks = lastWriteTicks,
                PartSize = partSize,
                CreatedAt = DateTimeOffset.Now,
            };
            _store.Save(session);
        }

        var totalParts = PartCount(fileLength, partSize);
        var done = new Dictionary<int, MultipartPart>();
        foreach (var part in session.Parts)
        {
            if (part.PartNumber < 1 || part.PartNumber > totalParts)
            {
                continue;
            }

            var (_, expectedLength) = PartRange(fileLength, partSize, part.PartNumber);
            if (part.Size == expectedLength)
            {
                done[part.PartNumber] = part;
            }
        }

        var uploadedBytes = done.Values.Sum(p => p.Size);
        progress?.Report(uploadedBytes);

        for (var partNumber = 1; partNumber <= totalParts; partNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (done.ContainsKey(partNumber))
            {
                continue;
            }

            var (offset, length) = PartRange(fileLength, partSize, partNumber);
            var baseBytes = uploadedBytes;
            var partProgress = progress is null ? null : new OffsetProgress(progress, baseBytes);

            var upload = await UploadPartWithRetryAsync(
                    key,
                    session.UploadId,
                    partNumber,
                    localPath,
                    offset,
                    length,
                    partProgress,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!upload.Result.Success || string.IsNullOrEmpty(upload.ETag))
            {
                // 404 / NoSuchUpload 说明服务端上这个会话没了：让上层重开一个（见 UploadAsync）。
                var restart = upload.Result.StatusCode == 404
                              || string.Equals(upload.Result.ErrorCode, "NoSuchUpload", StringComparison.OrdinalIgnoreCase);
                return (upload.Result, restart);
            }

            done[partNumber] = new MultipartPart(partNumber, length, upload.ETag!);
            var ordered = done.Values.OrderBy(p => p.PartNumber).ToList();
            session = session with { Parts = ordered };

            // 每传完一片就落盘：这是"断了能续"的唯一依据，所以宁可多写几次盘。
            LastResumeStateError = _store.Save(session);

            uploadedBytes += length;
            progress?.Report(uploadedBytes);
        }

        var complete = await Client
            .CompleteMultipartUploadAsync(key, session.UploadId, done.Values.OrderBy(p => p.PartNumber).ToList(), cancellationToken)
            .ConfigureAwait(false);

        if (!complete.Success)
        {
            return (complete, false);
        }

        _store.Delete(id);
        return (complete with { Url = BuildObjectUrl(key) }, false);
    }

    private async Task<(StorageResult Result, string? ETag)> UploadPartWithRetryAsync(
        string key,
        string uploadId,
        int partNumber,
        string localPath,
        long offset,
        long length,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        StorageResult last = StorageResult.Fail(0, "NetworkError", "分片没有上传。");
        for (var attempt = 1; attempt <= PartAttempts; attempt++)
        {
            var (result, etag) = await Client
                .UploadPartAsync(key, uploadId, partNumber, localPath, offset, length, progress, cancellationToken)
                .ConfigureAwait(false);

            if (result.Success)
            {
                return (result, etag);
            }

            last = result;

            // 只有"再试一次可能就好了"的错误才重试：网络抖动、5xx、限流。
            // 4xx（签名错、会话没了、文件不对）重试只是白等。
            if (!IsTransient(result) || attempt == PartAttempts)
            {
                return (result, null);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(800 * attempt), cancellationToken).ConfigureAwait(false);
        }

        return (last, null);
    }

    private static bool IsTransient(StorageResult result) =>
        result.StatusCode == 0 || result.StatusCode >= 500 || result.StatusCode is 408 or 429;

    private string BuildObjectUrl(string key) => ObjectUrlBuilder.DirectUrl(_settings, key);

    /// <summary>把"这一片已经写出多少字节"换算成整文件进度。</summary>
    private sealed class OffsetProgress(IProgress<long> inner, long baseBytes) : IProgress<long>
    {
        public void Report(long value) => inner.Report(baseBytes + value);
    }
}
