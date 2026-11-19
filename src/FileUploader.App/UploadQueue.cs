using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.ComponentModel;
using FileUploader.Core;
using FileUploader.Core.Storage;
using Windows.Storage;

namespace FileUploader.App;

/// <summary>
/// 全局唯一的上传队列：主窗口和简洁模式（气泡）共用同一份数据，
/// 任何一处上传，两个窗口都能看到进度与结果。
/// </summary>
public sealed class UploadQueue : INotifyPropertyChanged
{
    /// <summary>同时上传的文件数上限。</summary>
    public const int MaxConcurrentUploads = 3;

    /// <summary>
    /// 续传状态超过这么久没动过就当过期：本地文件大概率也不在了，
    /// 服务端那边的分片该清掉了（否则会一直占着桶的容量，界面上还看不见）。
    /// </summary>
    private static readonly TimeSpan StaleSessionAge = TimeSpan.FromDays(7);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _uploadGate = new(MaxConcurrentUploads);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellations = new();
    private ImmutableArray<UploadItem> _items = SeedItems();

    private UploadQueue()
    {
    }

    /// <summary>进程内唯一实例。</summary>
    public static UploadQueue Current { get; } = new();

    /// <summary>当前的上传/历史条目（不可变快照，按时间倒序：最新的在最前面）。</summary>
    public ImmutableArray<UploadItem> Items
    {
        get
        {
            lock (_gate)
            {
                // 历史是从新到旧读进来的，新上传的却是追加在尾部 ——
                // 不在这里统一排序，新记录就会跑到历史下面去（"刚传完的文件找不到"）。
                // OrderByDescending 是稳定排序：同一批一起入队的文件保持原顺序。
                return _items.OrderByDescending(i => i.StartedAt).ToImmutableArray();
            }
        }
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>某个文件上传成功（已写入历史、已按设置复制链接）。</summary>
    public event Action<UploadItem>? Uploaded;

    /// <summary>某个文件上传失败。</summary>
    public event Action<UploadItem>? Failed;

    /// <summary>整批请求被拒绝（未配置、没有文件、读取失败等），参数是给用户看的说明。</summary>
    public event Action<string>? Rejected;

    /// <summary>是否正在上传。</summary>
    public bool IsBusy => Items.Any(i => i.IsActive);

    /// <summary>把一批拖入/选中的项目加入队列。</summary>
    public async Task EnqueueAsync(IReadOnlyList<IStorageItem> storageItems)
    {
        var settings = SettingsState.Current.Value;
        var missing = settings.Validate();
        if (missing.Count > 0)
        {
            Rejected?.Invoke("还没有配置存储，请先在设置里填写：" + string.Join("、", missing));
            return;
        }

        IReadOnlyList<DroppedFile> dropped;
        try
        {
            dropped = await DropExpander.ExpandAsync(storageItems).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Rejected?.Invoke($"读取文件失败：{ex.Message}");
            return;
        }

        if (dropped.Count == 0)
        {
            Rejected?.Invoke("没有找到可上传的文件。");
            return;
        }

        // 「多个文件打包成 ZIP 上传」：展开后多于一个文件就打成一个压缩包再传，
        // 拖一个文件夹不再刷出一长串记录（单个文件仍直接上传，省一次没必要的压缩）。
        if (settings.ZipBatchUpload && dropped.Count > 1)
        {
            await EnqueueZipAsync(storageItems, dropped, settings).ConfigureAwait(false);
            return;
        }

        var now = DateTimeOffset.Now;
        var created = new List<UploadItem>(dropped.Count);
        var busyPaths = Items
            .Where(i => i.IsActive || i.State == UploadState.Interrupted)
            .Select(i => i.LocalPath)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;

        foreach (var file in dropped)
        {
            // 同一个文件已经在列表里（正在进行，或者有可续传的会话）就不要再排一次：
            // 两条记录会抢同一个 uploadId 的分片，白白多传一遍。
            if (!string.IsNullOrEmpty(file.LocalPath) && !busyPaths.Add(file.LocalPath))
            {
                skipped++;
                continue;
            }

            created.Add(new UploadItem(
                Guid.NewGuid(),
                file.FileName,
                file.LocalPath,
                file.Size,
                ResolveKey(settings, file, now),
                UploadState.Queued,
                0,
                null,
                null,
                now));
        }

        if (created.Count == 0)
        {
            if (skipped > 0)
            {
                Rejected?.Invoke($"{skipped} 个文件已经在列表里（正在进行或可续传），点那一行的「继续」接着传就好。");
            }

            return;
        }

        lock (_gate)
        {
            _items = _items.AddRange(created);
        }

        RaiseChanged();

        foreach (var item in created)
        {
            StartUpload(item, settings);
        }
    }

    /// <summary>取消某个条目（打包中、排队中或上传中）。</summary>
    public void Cancel(Guid id)
    {
        if (_cancellations.TryGetValue(id, out var cts))
        {
            cts.Cancel();
        }
    }

    /// <summary>
    /// 把一次拖入的所有文件打成一个 zip，再走同一条上传队列。
    /// 打包期间列表里就已经有一行（状态「打包中 xx%」），取消/移除会中断打包并删掉临时 zip。
    /// </summary>
    private async Task EnqueueZipAsync(
        IReadOnlyList<IStorageItem> storageItems,
        IReadOnlyList<DroppedFile> dropped,
        AppSettings settings)
    {
        var now = DateTimeOffset.Now;
        var fileName = ZipPackager.ChooseFileName(storageItems.OfType<StorageFolder>().Select(f => f.Name));
        var item = new UploadItem(
            Guid.NewGuid(),
            fileName,
            string.Empty,
            0,
            ObjectKey.Build(settings, fileName, null, now),
            UploadState.Packing,
            0,
            null,
            null,
            now,
            IsTemporary: true);

        lock (_gate)
        {
            _items = _items.Add(item);
        }

        RaiseChanged();

        var entries = dropped
            .Select(f => new ZipEntry(
                f.LocalPath,
                ZipPackager.CombineEntryName(f.RootName, f.RelativeDirectory, f.FileName)))
            .ToArray();

        var cancellation = new CancellationTokenSource();
        _cancellations[item.Id] = cancellation;
        var packing = true;
        UploadItem? ready = null;
        try
        {
            // 临时文件放 %TEMP%\FileUploader：打包产物只是中间的，成功/失败/取消都会删掉。
            var zipPath = Path.Combine(Path.GetTempPath(), "FileUploader", item.Id.ToString("N") + ".zip");
            var progress = new Progress<int>(done =>
            {
                if (!packing)
                {
                    return;
                }

                UpdateItem(item.Id, i => i with { Progress = Math.Min(100, done * 100d / Math.Max(1, entries.Length)) });
            });

            var result = await ZipPackager.CreateAsync(zipPath, entries, progress, cancellation.Token).ConfigureAwait(false);
            packing = false;

            if (!result.Success)
            {
                UpdateItem(item.Id, i => i with { State = UploadState.Failed, Error = $"打包失败：{result.Error}" });
                Rejected?.Invoke($"打包失败：{result.Error}");
                return;
            }

            if (result.Skipped > 0)
            {
                Rejected?.Invoke($"{result.Skipped} 个文件读不到（可能被移动或没权限），已从压缩包里跳过。");
            }

            // 只有条目还在「打包中」才转成待上传：否则说明打包这几秒里用户已经取消/移除了它，
            // 这时压缩包没人要，直接删掉（不删就会漏在临时目录里）。
            // 注意：这里不能用 return 跳出 —— try 里 return 会连 finally 后面的 StartUpload 一起跳过，
            // 行会卡在「等待上传」不动（踩过）。
            var updated = UpdateItem(item.Id, i => i.State == UploadState.Packing
                ? i with { LocalPath = zipPath, Size = result.Bytes, State = UploadState.Queued, Progress = 0 }
                : i);

            if (updated is { State: UploadState.Queued })
            {
                ready = updated;
            }
            else
            {
                DeleteQuietly(zipPath);
            }
        }
        catch (OperationCanceledException)
        {
            packing = false;
            UpdateItem(item.Id, i => i with { State = UploadState.Canceled, Progress = 0, Error = null });
        }
        catch (Exception ex)
        {
            packing = false;
            UpdateItem(item.Id, i => i with { State = UploadState.Failed, Error = $"打包失败：{ex.Message}" });
            Rejected?.Invoke($"打包失败：{ex.Message}");
        }
        finally
        {
            packing = false;
            _cancellations.TryRemove(item.Id, out _);
            cancellation.Dispose();
        }

        // 打包用的 CancellationTokenSource 已经摘掉了，这里再交给上传流程自己管；
        // 放在 finally 之后是为了不让上面那句 TryRemove 把上传的令牌一起摘掉。
        if (ready is not null)
        {
            StartUpload(ready, settings);
        }
    }

    /// <summary>重试失败/取消的条目。</summary>
    public void Retry(UploadItem item)
    {
        var current = SettingsState.Current.Value;
        if (!current.IsConfigured)
        {
            Rejected?.Invoke("还没有配置存储，请先打开「设置」。");
            return;
        }

        // 合并成功但本地临时 zip 已经删掉的（或打包本身失败的）没法重试，只能重新拖一次。
        if (string.IsNullOrEmpty(item.LocalPath))
        {
            Rejected?.Invoke($"{item.FileName} 没法重试：本地文件已经不在，请重新拖一次。");
            return;
        }

        var reset = item with { State = UploadState.Queued, Progress = 0, Error = null };
        UpdateItem(item.Id, _ => reset);
        StartUpload(reset, current);
    }

    /// <summary>从列表移除（同时删掉历史里的记录、临时文件与未完成的分片上传）。</summary>
    public void Remove(Guid id)
    {
        Cancel(id);

        UploadItem? removed;
        lock (_gate)
        {
            removed = _items.FirstOrDefault(i => i.Id == id);
            _items = _items.RemoveAll(i => i.Id == id);
        }

        RaiseChanged();

        if (removed is null)
        {
            return;
        }

        if (removed.Url is { Length: > 0 } url)
        {
            HistoryStore.Remove(url);
        }

        if (removed is { IsTemporary: true, LocalPath.Length: > 0 })
        {
            DeleteQuietly(removed.LocalPath);
        }

        // 用户是"这个文件不要了"：服务端已收的分片也清掉，不然会一直挂在桶里占容量。
        DiscardPendingSession(removed.Key, removed.LocalPath);
    }

    /// <summary>清空已结束（成功/失败/取消/中断）的记录。</summary>
    public void ClearFinished()
    {
        ImmutableArray<UploadItem> removed;
        lock (_gate)
        {
            removed = _items.Where(i => i.IsFinished).ToImmutableArray();
            _items = _items.RemoveAll(i => i.IsFinished);
        }

        HistoryStore.Clear();
        RaiseChanged();

        foreach (var item in removed)
        {
            DiscardPendingSession(item.Key, item.LocalPath);
        }
    }

    /// <summary>
    /// 启动后清理：续传状态已经没用了（本地文件没了、被改过，或者放了一周以上）就丢掉，
    /// 并尽力让服务端把分片也扔掉 —— 否则那些分片会一直占着桶的容量，而且任何界面上都看不见。
    /// </summary>
    public void CleanupStaleSessions()
    {
        var settings = SettingsState.Current.Value;
        var store = MultipartSessionStore.Default;
        IReadOnlyList<MultipartSession> sessions;
        try
        {
            sessions = store.LoadAll();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (sessions.Count == 0)
        {
            return;
        }

        // 列表里还在的（正在传 / 待续传）一律不动。
        var activeKeys = Items
            .Where(i => i.IsActive || i.State == UploadState.Interrupted)
            .Select(i => i.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var session in sessions)
        {
            if (activeKeys.Contains(session.Key))
            {
                continue;
            }

            var stale = DateTimeOffset.Now - session.CreatedAt > StaleSessionAge;
            if (!stale)
            {
                try
                {
                    var info = new FileInfo(session.LocalPath);
                    stale = !info.Exists || !session.MatchesFile(session.LocalPath, info.Length, info.LastWriteTimeUtc.Ticks);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    stale = true;
                }
            }

            if (!stale)
            {
                continue;
            }

            store.Delete(MultipartSessionStore.IdFor(session.Endpoint, session.Bucket, session.Protocol, session.Key));
            if (settings.IsConfigured && session.MatchesSettings(settings))
            {
                _ = AbortRemoteAsync(settings, session);
            }
        }
    }

    /// <summary>
    /// 丢掉这个条目对应的续传会话：先删本地状态（界面立刻干净），再让服务端把分片也清掉。
    /// 只处理键与路径都对得上的会话，别的桶 / 别的文件一律不动。
    /// </summary>
    private static void DiscardPendingSession(string key, string localPath)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        MultipartSession? session = null;
        try
        {
            foreach (var candidate in MultipartSessionStore.Default.LoadAll())
            {
                if (!string.Equals(candidate.Key, key, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(localPath)
                    && !string.Equals(candidate.LocalPath, localPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                session = candidate;
                break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (session is null)
        {
            return;
        }

        MultipartSessionStore.Default.Delete(
            MultipartSessionStore.IdFor(session.Endpoint, session.Bucket, session.Protocol, session.Key));

        var settings = SettingsState.Current.Value;
        if (settings.IsConfigured && session.MatchesSettings(settings))
        {
            _ = AbortRemoteAsync(settings, session);
        }
    }

    /// <summary>发 Abort 清掉服务端的分片（尽力而为：清不掉不影响用户移除记录）。</summary>
    private static async Task AbortRemoteAsync(AppSettings settings, MultipartSession session)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await new MultipartUploader(settings).AbortAsync(session, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
        {
            // 离线、权限不足、服务端不支持：本地状态已经删了，剩下的分片交给存储服务的生命周期规则。
        }
    }

    /// <summary>磁盘上的真实文件长度（拿不到时退回条目里记的值）。</summary>
    private static long SafeFileLength(string path, long fallback)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0 ? info.Length : fallback;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// 用当前配置重新生成链接（签名模式下每次都是新签名）；配置不完整时退回历史里的旧链接。
    /// </summary>
    public string? ResolveUrl(UploadItem item)
    {
        var settings = SettingsState.Current.Value;
        if (settings.IsConfigured && !string.IsNullOrEmpty(item.Key))
        {
            try
            {
                return ObjectUrlBuilder.Build(settings, item.Key, DateTimeOffset.Now);
            }
            catch (Exception)
            {
                // 配置异常时退回旧值。
            }
        }

        return string.IsNullOrEmpty(item.Url) ? null : item.Url;
    }

    /// <summary>
    /// 程序内下载用的地址：有密钥就现签一个（私有桶 / 已过期都能下），没密钥时退回历史里的旧链接。
    /// </summary>
    public string? ResolveDownloadUrl(UploadItem item) =>
        ObjectUrlBuilder.DownloadUrl(SettingsState.Current.Value, item.Key, item.Url, DateTimeOffset.Now);

    /// <summary>重新生成链接并写入剪贴板（供「复制链接」使用）。</summary>
    public bool CopyLink(UploadItem item)
    {
        var url = ResolveUrl(item);
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        ClipboardHelper.Copy(url);
        if (!string.Equals(url, item.Url, StringComparison.Ordinal))
        {
            UpdateItem(item.Id, i => i with { Url = url });
        }

        return true;
    }

    /// <summary>复制给定条目里成功那些的链接（主窗口会传筛选后的集合，「全部」按钮也用这个）。</summary>
    public int CopyLinks(IEnumerable<UploadItem> items)
    {
        var urls = items
            .Where(i => i.State == UploadState.Succeeded)
            .Select(ResolveUrl)
            .Where(u => !string.IsNullOrEmpty(u))
            .Select(u => u!)
            .ToArray();

        if (urls.Length == 0)
        {
            return 0;
        }

        ClipboardHelper.Copy(string.Join(Environment.NewLine, urls));
        return urls.Length;
    }

    /// <summary>
    /// 对象键：本地文件有未完成的分片上传时**沿用上次的键**（换一个键就等于换了对象，之前传的分片全白费）。
    /// </summary>
    private static string ResolveKey(AppSettings settings, DroppedFile file, DateTimeOffset now)
    {
        try
        {
            if (new MultipartUploader(settings).FindPending(file.LocalPath) is { } pending)
            {
                return pending.Key;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 找续传会话失败不影响上传：直接生成新键，大不了从头传。
        }

        return ObjectKey.Build(settings, file.FileName, file.RelativeDirectory, now);
    }

    private static ImmutableArray<UploadItem> SeedItems()
    {
        var builder = ImmutableArray.CreateBuilder<UploadItem>();
        foreach (var entry in HistoryStore.Load())
        {
            builder.Add(new UploadItem(
                Guid.NewGuid(),
                entry.FileName,
                string.Empty,
                entry.Size,
                entry.Key,
                UploadState.Succeeded,
                100,
                entry.Url,
                null,
                entry.UploadedAt));
        }

        try
        {
            builder.AddRange(SeedInterrupted());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 续传状态读不出来不该拦住启动：历史照常显示，用户重拖一次就好。
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// 上次没传完的分片上传：重启后直接列进列表（「已中断」），点「继续」只补没传完的分片。
    /// 只列当前配置下、本地文件还在且没被改过的那几个 —— 其余没法续传，不拿出来误导人。
    /// </summary>
    private static IEnumerable<UploadItem> SeedInterrupted()
    {
        var settings = SettingsState.Current.Value;
        if (!settings.IsConfigured)
        {
            yield break;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "FileUploader");
        foreach (var session in MultipartSessionStore.Default.LoadAll())
        {
            if (!session.MatchesSettings(settings))
            {
                continue;
            }

            var resumable = false;
            try
            {
                var info = new FileInfo(session.LocalPath);
                resumable = info.Exists && session.MatchesFile(session.LocalPath, info.Length, info.LastWriteTimeUtc.Ticks);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                resumable = false;
            }

            if (!resumable)
            {
                continue;
            }

            var percent = session.FileLength <= 0
                ? 0
                : Math.Min(100, session.UploadedBytes * 100d / session.FileLength);

            // 打包上传的临时 zip：续传成功后也要像正常流程那样删掉（否则会一直留在 %TEMP% 里）。
            var isTemporary = session.LocalPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase);

            yield return new UploadItem(
                Guid.NewGuid(),
                session.FileName,
                session.LocalPath,
                session.FileLength,
                session.Key,
                UploadState.Interrupted,
                percent,
                null,
                null,
                session.CreatedAt,
                isTemporary);
        }
    }

    private void StartUpload(UploadItem item, AppSettings settings)
    {
        var cts = new CancellationTokenSource();
        _cancellations[item.Id] = cts;
        _ = RunUploadAsync(item, settings, cts);
    }

    private async Task RunUploadAsync(UploadItem item, AppSettings settings, CancellationTokenSource cts)
    {
        try
        {
            await _uploadGate.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _cancellations.TryRemove(item.Id, out _);
            UpdateItem(item.Id, i => i with { State = UploadState.Canceled });
            return;
        }

        try
        {
            UpdateItem(item.Id, i => i with { State = UploadState.Uploading, Progress = 0, Error = null });

            // 签名链接模式下对象保持私有（不设置 public-read ACL）。
            var uploadSettings = settings with
            {
                PublicReadObject = settings.PublicReadObject && settings.UrlMode == UrlMode.Direct,
            };
            var client = new ObjectStorageClient(uploadSettings);
            var progress = new Progress<long>(bytes => UpdateItem(
                item.Id,
                i => i with { Progress = i.Size <= 0 ? 100 : Math.Min(100, bytes * 100d / i.Size) }));

            // 大文件走分片上传（每片 8 MiB 左右，断点续传）；小文件仍然一次 PUT，省三次往返。
            // 用磁盘上的真实长度判断：打包出来的条目在入队那一刻 Size 可能还是 0。
            MultipartUploader? multipart = null;

            async Task<StorageResult> SendAsync()
            {
                var contentType = ContentTypes.ForFileName(item.FileName);
                if (MultipartUploader.ShouldUseMultipart(SafeFileLength(item.LocalPath, item.Size)))
                {
                    multipart = new MultipartUploader(uploadSettings);
                    return await multipart
                        .UploadAsync(item.LocalPath, item.Key, contentType, progress, cts.Token)
                        .ConfigureAwait(false);
                }

                return await client
                    .PutObjectAsync(item.LocalPath, item.Key, contentType, progress, cts.Token)
                    .ConfigureAwait(false);
            }

            var result = await SendAsync().ConfigureAwait(false);

            // 桶还不存在（第一次使用、或桶名刚改过）时自动建桶再传一次。
            if (!result.Success && string.Equals(result.ErrorCode, "NoSuchBucket", StringComparison.OrdinalIgnoreCase))
            {
                var ensure = await client.EnsureBucketAsync(cts.Token).ConfigureAwait(false);
                if (ensure.Success)
                {
                    result = await SendAsync().ConfigureAwait(false);
                }
            }

            // 续传状态写不进盘不拦着这次上传，但得说一声：下次中断就真的得从头再传了。
            if (multipart?.LastResumeStateError is { Length: > 0 } resumeError)
            {
                Rejected?.Invoke($"续传进度没能存下来（{resumeError}）：这次要是中断了，只能从头再传。");
            }

            if (result.Success)
            {
                var uploadedAt = DateTimeOffset.Now;
                var url = ObjectUrlBuilder.Build(settings, item.Key, uploadedAt);
                UpdateItem(item.Id, i => i with { State = UploadState.Succeeded, Progress = 100, Url = url, Error = null, StartedAt = uploadedAt });
                HistoryStore.Add(new HistoryEntry(item.FileName, item.Key, url, item.Size, uploadedAt));

                if (settings.AutoCopyUrl)
                {
                    ClipboardHelper.Copy(url);
                }

                Uploaded?.Invoke(Items.FirstOrDefault(i => i.Id == item.Id) ?? item);
            }
            else
            {
                var failed = UpdateItem(item.Id, i => i with { State = UploadState.Failed, Error = result.Describe() });
                if (failed is { } value)
                {
                    Failed?.Invoke(value);
                }
            }
        }
        catch (OperationCanceledException)
        {
            UpdateItem(item.Id, i => i with { State = UploadState.Canceled, Error = null });
        }
        catch (Exception ex)
        {
            var failed = UpdateItem(item.Id, i => i with { State = UploadState.Failed, Error = ex.Message });
            if (failed is { } value)
            {
                Failed?.Invoke(value);
            }
        }
        finally
        {
            _cancellations.TryRemove(item.Id, out _);
            cts.Dispose();
            _uploadGate.Release();

            // 合并上传的临时 zip：传完（成功或失败）就删，别在临时目录里堆着。
            if (item.IsTemporary)
            {
                DeleteQuietly(item.LocalPath);
            }
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就留在 %TEMP%\FileUploader 里，系统清理时会被带走。
        }
    }

    private UploadItem? UpdateItem(Guid id, Func<UploadItem, UploadItem> transform)
    {
        UploadItem? updated = null;
        lock (_gate)
        {
            for (var i = 0; i < _items.Length; i++)
            {
                if (_items[i].Id == id)
                {
                    updated = transform(_items[i]);
                    _items = _items.SetItem(i, updated);
                    break;
                }
            }
        }

        if (updated is not null)
        {
            RaiseChanged();
        }

        return updated;
    }

    private void RaiseChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Items)));
}
