using System.Collections.Immutable;
using FileUploader.Core;
using FileUploader.Core.Storage;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;
using static Microsoft.UI.Reactor.Factories;

namespace FileUploader.App;

public sealed partial class UploadApp
{
    private Element BuildHistoryCard()
    {
        var all = _items;
        var query = _searchText.Trim();
        var searching = query.Length > 0;

        // 搜索只匹配文件名：对象键里含前缀和日期目录，拿它匹配会让人以为搜到了没搜到的东西。
        var items = searching
            ? all.Where(i => i.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToImmutableArray()
            : all;

        var succeeded = items.Count(i => i.State == UploadState.Succeeded);
        var finished = items.Count(i => i.IsFinished);
        var running = items.Count(i => i.IsActive);

        var summary = items.Length == 0
            ? searching ? $"0 / {all.Length} 条匹配" : "还没有记录"
            : searching
                ? $"{items.Length} / {all.Length} 条匹配"
                : running > 0
                    ? $"{items.Length} 条 · {running} 个进行中"
                    : $"{items.Length} 条 · {succeeded} 条已上传";

        var header = VStack(
            10,
            Grid(
                columns: [GridSize.Star(), GridSize.Auto],
                rows: [GridSize.Auto],
                children:
                [
                    HStack(
                            8,
                            TextBlock("上传历史").FontSize(15).SemiBold(),
                            TextBlock(summary).FontSize(12).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center))
                        .Grid(column: 0),
                    HStack(
                            8,
                            Button("复制全部链接", () => CopyAllUrls(items)).SubtleButton().IsEnabled(succeeded > 0),
                            Button("清空记录", ClearHistory)
                                .SubtleButton()
                                // 搜索时清空会把看不见的记录一起删掉，太容易误伤：先让用户清除搜索。
                                .IsEnabled(finished > 0 && !searching)
                                .ToolTip(searching ? "先清除搜索，再清空记录" : "把已结束（成功/失败/取消）的记录全部删掉"))
                        .Grid(column: 1),
                ]),
            all.Length == 0
                ? Empty()
                : Grid(
                    columns: [GridSize.Star(), GridSize.Auto],
                    rows: [GridSize.Auto],
                    children:
                    [
                        TextBox(_searchText, value => _setSearchText(value ?? string.Empty))
                            .PlaceholderText("按文件名搜索")
                            .AutomationName("按文件名搜索历史记录")
                            .Grid(column: 0),
                        searching
                            ? Button("清除", () => _setSearchText(string.Empty))
                                .SubtleButton()
                                .AutomationName("清除搜索")
                                .Margin(8, 0, 0, 0)
                                .VAlign(VerticalAlignment.Center)
                                .Grid(column: 1)
                            : Empty(),
                    ]));

        Element body = items.Length > 0
            ? ScrollView(VStack(8, items.Select(BuildHistoryRow).ToArray()))
            : searching
                ? VStack(
                        6,
                        TextBlock($"没有文件名包含「{query}」的记录").FontSize(13).Foreground(Theme.SecondaryText),
                        TextBlock("搜索只匹配文件名；换个关键字，或者点上面的「清除」回到全部记录").FontSize(12).Foreground(Theme.TertiaryText))
                    .Center()
                : VStack(
                        6,
                        TextBlock("还没有上传记录").FontSize(13).Foreground(Theme.SecondaryText),
                        TextBlock("把文件拖入上方区域，记录会留在这里，重启后也还在；点「复制链接」随时生成新的下载地址").FontSize(12).Foreground(Theme.TertiaryText))
                    .Center();

        return Border(
                Grid(
                    rows: [GridSize.Auto, GridSize.Star()],
                    columns: [GridSize.Star()],
                    children:
                    [
                        header.Grid(row: 0).Margin(0, 0, 0, 12),
                        body.Grid(row: 1),
                    ]))
            .Background(Theme.CardBackground)
            .BorderBrush(Theme.CardStroke)
            .BorderThickness(1)
            .CornerRadius(12)
            .Padding(16);
    }

    private Element BuildHistoryRow(UploadItem item)
    {
        var stateColor = item.State switch
        {
            UploadState.Succeeded => Theme.SystemSuccess,
            UploadState.Failed => Theme.SystemCritical,
            UploadState.Canceled => Theme.TertiaryText,
            // 「已中断」是还能继续的，用提醒色而不是失败红，别让人以为白传了。
            UploadState.Interrupted => Theme.SystemCaution,
            _ => Theme.SecondaryText,
        };

        var isDownloading = _downloadingId == item.Id;
        var downloadBusy = _downloadingId is not null;

        var actions = new List<Element>();
        switch (item.State)
        {
            case UploadState.Succeeded:
#pragma warning disable REACTOR_A11Y_001 // 文案是动态的（已复制 / 下载中 xx%），由 AutomationName 兜底
                actions.Add(Button(_copiedId == item.Id ? "已复制" : "复制链接", () => CopyItemUrl(item)).SubtleButton());
                actions.Add(
                    Button(isDownloading ? $"下载中 {_downloadProgress:0}%" : "下载", () => DownloadItem(item))
                        .SubtleButton()
                        .AutomationName($"下载 {item.FileName}")
                        // 一次只下一个（进度只有一个地方显示），其余按钮先禁掉。
                        .IsEnabled(!downloadBusy || isDownloading));
#pragma warning restore REACTOR_A11Y_001
                break;
            case UploadState.Packing:
            case UploadState.Uploading:
            case UploadState.Queued:
                actions.Add(Button("取消", () => UploadQueue.Current.Cancel(item.Id)).SubtleButton());
                break;
            default:
                // 打包失败/临时 zip 已删的条目没有本地源文件，重试跑不起来，只给「移除」。
                if (!string.IsNullOrEmpty(item.LocalPath))
                {
                    // 「已中断」是上次没传完的分片上传：同样的动作，只是文案要说清是"继续"。
                    actions.Add(Button(
                            item.State == UploadState.Interrupted ? "继续" : "重试",
                            () => UploadQueue.Current.Retry(item))
                        .SubtleButton()
                        .AutomationName(item.State == UploadState.Interrupted
                            ? $"继续上传 {item.FileName}"
                            : $"重试上传 {item.FileName}"));
                }

                break;
        }

        actions.Add(
            Button("移除", () => UploadQueue.Current.Remove(item.Id))
                .SubtleButton()
                .IsEnabled(!item.IsActive));

        var meta = item.State switch
        {
            UploadState.Succeeded => $"{item.SizeText} · 已上传 · {item.StartedAt:MM-dd HH:mm}",
            // 打包中/打包阶段就被取消的条目还没有确定的文件大小，别再显示一个 "0 B" 让人以为文件是空的。
            UploadState.Packing => item.StateText,
            _ when item.Size <= 0 => item.StateText,
            _ => $"{item.SizeText} · {item.StateText}",
        };

        Element detail = item.State switch
        {
            UploadState.Packing => Progress(item.Progress).Height(4).AutomationName($"{item.FileName} 打包进度"),
            UploadState.Uploading or UploadState.Queued => Progress(item.Progress).Height(4).AutomationName($"{item.FileName} 上传进度"),
            // 中断的条目也把进度条留着：看一眼就知道续传是从几成开始的。
            UploadState.Interrupted => Progress(item.Progress).Height(4).AutomationName($"{item.FileName} 已传进度"),
            UploadState.Failed => TextBlock(item.Error ?? "上传失败")
                .FontSize(12)
                .Foreground(Theme.SystemCritical)
                .TextWrapping(),

            // 签名链接每次复制都会重新生成，列表里显示它没有意义（还会误导人以为那个地址长期有效）；
            // 直链模式下的地址是稳定的，仍然展示出来方便直接取用。
            _ => item.Url is null || _settings.UrlMode != UrlMode.Direct
                ? Empty()
                : TextBlock(item.Url)
                    .FontSize(12)
                    .Foreground(Theme.Accent)
                    .IsTextSelectionEnabled(true)
                    .TextTrimming(TextTrimming.CharacterEllipsis),
        };

        // 下载进度条顶替掉链接/说明那一行：下载时最想知道的就是"还有多少"。
        if (isDownloading)
        {
            detail = Progress(_downloadProgress).Height(4).AutomationName($"{item.FileName} 下载进度");
        }

        return Border(
                VStack(
                    6,
                    Grid(
                        columns: [GridSize.Star(), GridSize.Auto],
                        rows: [GridSize.Auto],
                        children:
                        [
                            VStack(
                                    2,
                                    TextBlock(item.FileName).SemiBold().TextTrimming(TextTrimming.CharacterEllipsis),
                                    HStack(
                                        6,
                                        TextBlock(meta).FontSize(11).Foreground(stateColor),
                                        TextBlock(item.Key)
                                            .FontSize(11)
                                            .Foreground(Theme.TertiaryText)
                                            .TextTrimming(TextTrimming.CharacterEllipsis)))
                                .Grid(column: 0),
                            HStack(6, actions.ToArray()).Grid(column: 1),
                        ]),
                    detail))
            .Padding(12)
            .CornerRadius(8)
            .Background(Theme.SubtleFill)
            .WithKey(item.Id.ToString());
    }

    private void CopyItemUrl(UploadItem item)
    {
        if (!UploadQueue.Current.CopyLink(item))
        {
            return;
        }

        var settings = SettingsState.Current.Value;
        if (settings.UrlMode == UrlMode.Signed && settings.IsConfigured)
        {
            _setNotice(Notice.Info($"已复制签名链接（{ObjectUrlBuilder.EffectiveExpireDays(settings)} 天有效）。"));
        }

        _setCopiedId(item.Id);
        var id = item.Id;
        _ = Task.Delay(1600).ContinueWith(
            _ =>
            {
                if (_copiedId == id)
                {
                    _setCopiedId(null);
                }
            },
            TaskScheduler.Default);
    }

    private void CopyAllUrls(ImmutableArray<UploadItem> items)
    {
        var count = UploadQueue.Current.CopyLinks(items);
        if (count == 0)
        {
            return;
        }

        var settings = SettingsState.Current.Value;
        _setNotice(Notice.Info(settings.UrlMode == UrlMode.Signed
            ? $"已复制 {count} 条签名链接（{ObjectUrlBuilder.EffectiveExpireDays(settings)} 天有效）。"
            : $"已复制 {count} 条链接。"));
    }

    /// <summary>
    /// 把历史里的对象下载到本地（不经过浏览器）：先选保存位置，再流式写文件。
    /// 链接是现签的（见 <see cref="UploadQueue.ResolveDownloadUrl"/>），所以私有桶、签名过期都没关系。
    /// </summary>
    private async void DownloadItem(UploadItem item)
    {
        if (_downloadingId is not null)
        {
            return;
        }

        var url = UploadQueue.Current.ResolveDownloadUrl(item);
        if (string.IsNullOrEmpty(url))
        {
            _setNotice(Notice.Error("没有可用的下载地址：请先在设置里填好存储配置（需要密钥才能重新生成链接）。"));
            return;
        }

        var nativeWindow = ReactorApp.PrimaryWindow?.NativeWindow;
        if (nativeWindow is null)
        {
            _setNotice(Notice.Error("找不到主窗口，无法打开保存对话框。"));
            return;
        }

        StorageFile? target;
        try
        {
            target = await PickDownloadTargetAsync(nativeWindow, item.FileName);
        }
        catch (Exception ex)
        {
            _setNotice(Notice.Error($"打开保存对话框失败：{ex.Message}"));
            return;
        }

        if (target is null)
        {
            // 用户取消了保存对话框。
            return;
        }

        _setDownloadingId(item.Id);
        _setDownloadProgress(0);
        try
        {
            var progress = new Progress<long>(bytes =>
                _setDownloadProgress(item.Size <= 0 ? 0 : Math.Min(100, bytes * 100d / item.Size)));

            var result = await ObjectDownloader.DownloadAsync(url, target.Path, progress, CancellationToken.None);
            if (result.Success)
            {
                _setNotice(Notice.Info($"已下载 {item.FileName}（{UploadItem.FormatSize(result.Bytes)}）到 {target.Path}"));
                return;
            }

            // 保存对话框已经建了一个空文件，失败时删掉，免得用户以为下载好了。
            DeleteQuietly(target.Path);
            _setNotice(Notice.Error($"{item.FileName} 下载失败：{result.Error}"));
        }
        catch (Exception ex)
        {
            DeleteQuietly(target.Path);
            _setNotice(Notice.Error($"{item.FileName} 下载失败：{ex.Message}"));
        }
        finally
        {
            _setDownloadingId(null);
            _setDownloadProgress(0);
        }
    }

    /// <summary>
    /// 保存对话框：默认文件名取历史里的原名，默认位置「下载」。
    /// FileSavePicker 的类型列表不能为空、也不接受通配符，所以没扩展名的文件补一个 .bin 占位。
    /// </summary>
    private static async Task<StorageFile?> PickDownloadTargetAsync(Window nativeWindow, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            SuggestedFileName = string.IsNullOrEmpty(stem) ? fileName : stem,
            CommitButtonText = "下载",
        };
        picker.FileTypeChoices.Add("原文件类型", new List<string> { DownloadExtension(fileName) });

        // 非打包应用必须把 picker 绑定到窗口句柄，否则会直接抛异常。
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow));
        return await picker.PickSaveFileAsync();
    }

    /// <summary>能直接喂给 FileSavePicker 的扩展名（必须是 ".xxx" 形式），拿不出来时退回 .bin。</summary>
    private static string DownloadExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        var usable = extension.Length is >= 2 and <= 20 && extension.Skip(1).All(char.IsLetterOrDigit);
        return usable ? extension : ".bin";
    }

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
            // 删不掉就留着（用户自己处理），不要把错误盖住真正的下载失败原因。
        }
    }

    private void ClearHistory()
    {
        UploadQueue.Current.ClearFinished();
        _setNotice(Notice.Info("已清空上传记录。"));
    }
}
