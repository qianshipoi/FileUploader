using Windows.Storage;

namespace FileUploader.App;

/// <summary>把拖入的 StorageItem 展开成待上传文件列表（文件夹递归）。</summary>
public static class DropExpander
{
    private const int MaxFiles = 2000;

    /// <summary>展开拖入项；无法读取的项会被跳过。</summary>
    public static async Task<IReadOnlyList<DroppedFile>> ExpandAsync(IReadOnlyList<IStorageItem> items, CancellationToken cancellationToken = default)
    {
        var files = new List<DroppedFile>();

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (files.Count >= MaxFiles)
            {
                break;
            }

            switch (item)
            {
                case StorageFile file:
                    await AddFileAsync(files, file, relativeDirectory: null, rootName: null).ConfigureAwait(false);
                    break;
                case StorageFolder folder:
                    // 有真实文件系统路径时优先走 System.IO：拖一个几百个文件的文件夹时，
                    // WinRT 的逐个 GetBasicPropertiesAsync 慢得离谱（实测 600 个文件 ≈ 55 秒，
                    // 界面看起来就像卡死了）。只有 MTP/压缩包这种没有路径的项才走 WinRT。
                    if (!string.IsNullOrEmpty(folder.Path) && Directory.Exists(folder.Path))
                    {
                        ExpandWithFileSystem(files, folder.Path, folder.Name, cancellationToken);
                    }
                    else
                    {
                        await ExpandFolderAsync(files, folder, relativeDirectory: null, rootName: folder.Name, cancellationToken).ConfigureAwait(false);
                    }

                    break;
            }
        }

        return files;
    }

    /// <summary>
    /// 用 <see cref="Directory"/> 递归展开（快路径）。
    /// 没权限/中途被删的目录直接跳过；不跟踪 reparse point（junction/符号链接），
    /// 免得互相指向时无限递归，条数上限 <see cref="MaxFiles"/> 也会兜住。
    /// </summary>
    private static void ExpandWithFileSystem(List<DroppedFile> files, string rootPath, string? rootName, CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Path, string? Relative)>();
        pending.Push((rootPath, null));

        while (pending.Count > 0 && files.Count < MaxFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, relative) = pending.Pop();

            try
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                {
                    if (files.Count >= MaxFiles)
                    {
                        return;
                    }

                    FileInfo info;
                    try
                    {
                        info = new FileInfo(child);
                        if ((info.Attributes & System.IO.FileAttributes.Directory) != 0)
                        {
                            if ((info.Attributes & System.IO.FileAttributes.ReparsePoint) == 0)
                            {
                                pending.Push((child, Combine(relative, info.Name)));
                            }

                            continue;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    files.Add(new DroppedFile(child, info.Name, relative, info.Length, rootName));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 枚举到一半目录没了/没权限：已经拿到的照常保留。
            }
        }
    }

    private static async Task ExpandFolderAsync(
        List<DroppedFile> files,
        StorageFolder root,
        string? relativeDirectory,
        string? rootName,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<(StorageFolder Folder, string? Relative)>();
        pending.Push((root, relativeDirectory));

        while (pending.Count > 0 && files.Count < MaxFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (folder, relative) = pending.Pop();

            IReadOnlyList<IStorageItem> children;
            try
            {
                children = await folder.GetItemsAsync().AsTask(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException or IOException)
            {
                continue;
            }

            foreach (var child in children)
            {
                if (files.Count >= MaxFiles)
                {
                    return;
                }

                switch (child)
                {
                    case StorageFile file:
                        await AddFileAsync(files, file, relative, rootName).ConfigureAwait(false);
                        break;
                    case StorageFolder sub:
                        pending.Push((sub, Combine(relative, sub.Name)));
                        break;
                }
            }
        }
    }

    private static async Task AddFileAsync(List<DroppedFile> files, StorageFile file, string? relativeDirectory, string? rootName)
    {
        if (string.IsNullOrEmpty(file.Path))
        {
            return;
        }

        long size = 0;
        try
        {
            var properties = await file.GetBasicPropertiesAsync().AsTask().ConfigureAwait(false);
            size = (long)properties.Size;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException or IOException)
        {
            size = TryGetLength(file.Path);
        }

        files.Add(new DroppedFile(file.Path, file.Name, relativeDirectory, size, rootName));
    }

    private static long TryGetLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static string Combine(string? parent, string name) =>
        string.IsNullOrEmpty(parent) ? name : parent + "/" + name;
}
