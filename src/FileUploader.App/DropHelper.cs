using Microsoft.UI.Reactor.Input;
using Windows.Storage;

namespace FileUploader.App;

/// <summary>拖放事件的公共处理：把拖入项读成 StorageItem 列表。</summary>
internal static class DropHelper
{
    /// <summary>取出拖入的文件/文件夹；失败或无内容时返回空列表。</summary>
    public static async Task<IReadOnlyList<IStorageItem>> GetItemsAsync(DragTargetArgs args)
    {
        try
        {
            if (args.Data.TryGetFiles(out var sync) && sync.Count > 0)
            {
                return sync;
            }

            return await args.Data.GetFilesAsync();
        }
        catch (Exception)
        {
            return Array.Empty<IStorageItem>();
        }
    }
}
