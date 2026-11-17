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
                    await AddFileAsync(files, file, relativeDirectory: null).ConfigureAwait(false);
                    break;
                case StorageFolder folder:
                    await ExpandFolderAsync(files, folder, relativeDirectory: null, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        return files;
    }

    private static async Task ExpandFolderAsync(
        List<DroppedFile> files,
        StorageFolder root,
        string? relativeDirectory,
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
                        await AddFileAsync(files, file, relative).ConfigureAwait(false);
                        break;
                    case StorageFolder sub:
                        pending.Push((sub, Combine(relative, sub.Name)));
                        break;
                }
            }
        }
    }

    private static async Task AddFileAsync(List<DroppedFile> files, StorageFile file, string? relativeDirectory)
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

        files.Add(new DroppedFile(file.Path, file.Name, relativeDirectory, size));
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
