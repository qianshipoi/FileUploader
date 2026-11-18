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

    private readonly object _gate = new();
    private readonly SemaphoreSlim _uploadGate = new(MaxConcurrentUploads);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellations = new();
    private ImmutableArray<UploadItem> _items = SeedFromHistory();

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
    public bool IsBusy => Items.Any(i => i.State is UploadState.Uploading or UploadState.Queued);

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

        var now = DateTimeOffset.Now;
        var created = new List<UploadItem>(dropped.Count);
        foreach (var file in dropped)
        {
            created.Add(new UploadItem(
                Guid.NewGuid(),
                file.FileName,
                file.LocalPath,
                file.Size,
                ObjectKey.Build(settings, file.FileName, file.RelativeDirectory, now),
                UploadState.Queued,
                0,
                null,
                null,
                now));
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

    /// <summary>取消某个条目（排队中或上传中）。</summary>
    public void Cancel(Guid id)
    {
        if (_cancellations.TryGetValue(id, out var cts))
        {
            cts.Cancel();
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

        var reset = item with { State = UploadState.Queued, Progress = 0, Error = null };
        UpdateItem(item.Id, _ => reset);
        StartUpload(reset, current);
    }

    /// <summary>从列表移除（同时删掉历史里的记录）。</summary>
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

        if (removed?.Url is { Length: > 0 } url)
        {
            HistoryStore.Remove(url);
        }
    }

    /// <summary>清空已结束（成功/失败/取消）的记录。</summary>
    public void ClearFinished()
    {
        lock (_gate)
        {
            _items = _items.RemoveAll(i => i.IsFinished);
        }

        HistoryStore.Clear();
        RaiseChanged();
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

    private static ImmutableArray<UploadItem> SeedFromHistory()
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

        return builder.ToImmutable();
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

            var result = await client
                .PutObjectAsync(item.LocalPath, item.Key, ContentTypes.ForFileName(item.FileName), progress, cts.Token)
                .ConfigureAwait(false);

            // 桶还不存在（第一次使用、或桶名刚改过）时自动建桶再传一次。
            if (!result.Success && string.Equals(result.ErrorCode, "NoSuchBucket", StringComparison.OrdinalIgnoreCase))
            {
                var ensure = await client.EnsureBucketAsync(cts.Token).ConfigureAwait(false);
                if (ensure.Success)
                {
                    result = await client
                        .PutObjectAsync(item.LocalPath, item.Key, ContentTypes.ForFileName(item.FileName), progress, cts.Token)
                        .ConfigureAwait(false);
                }
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
