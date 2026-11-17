using FileUploader.Core;
using FileUploader.Core.Storage;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;

namespace FileUploader.App;

public sealed partial class UploadApp
{
    private Element BuildHistoryCard()
    {
        var items = _items;
        var succeeded = items.Count(i => i.State == UploadState.Succeeded);
        var finished = items.Count(i => i.IsFinished);
        var running = items.Count(i => i.State is UploadState.Uploading or UploadState.Queued);

        var summary = items.Length switch
        {
            0 => "还没有记录",
            _ => running > 0
                ? $"{items.Length} 条 · {running} 个进行中"
                : $"{items.Length} 条 · {succeeded} 条已上传",
        };

        var header = Grid(
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
                        Button("复制全部链接", CopyAllUrls).SubtleButton().IsEnabled(succeeded > 0),
                        Button("清空记录", ClearHistory).SubtleButton().IsEnabled(finished > 0))
                    .Grid(column: 1),
            ]);

        Element body = items.Length == 0
            ? VStack(
                    6,
                    TextBlock("还没有上传记录").FontSize(13).Foreground(Theme.SecondaryText),
                    TextBlock("把文件拖入上方区域，记录会留在这里，重启后也还在；点「复制链接」随时生成新的下载地址").FontSize(12).Foreground(Theme.TertiaryText))
                .Center()
            : ScrollView(VStack(8, items.Select(BuildHistoryRow).ToArray()));

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
            _ => Theme.SecondaryText,
        };

        var actions = new List<Element>();
        switch (item.State)
        {
            case UploadState.Succeeded:
#pragma warning disable REACTOR_A11Y_001 // 文案是动态的，分析器看不到
                actions.Add(Button(_copiedId == item.Id ? "已复制" : "复制链接", () => CopyItemUrl(item)).SubtleButton());
#pragma warning restore REACTOR_A11Y_001
                break;
            case UploadState.Uploading:
            case UploadState.Queued:
                actions.Add(Button("取消", () => UploadQueue.Current.Cancel(item.Id)).SubtleButton());
                break;
            default:
                actions.Add(Button("重试", () => UploadQueue.Current.Retry(item)).SubtleButton());
                break;
        }

        actions.Add(
            Button("移除", () => UploadQueue.Current.Remove(item.Id))
                .SubtleButton()
                .IsEnabled(item.State is not (UploadState.Uploading or UploadState.Queued)));

        var meta = item.State == UploadState.Succeeded
            ? $"{item.SizeText} · 已上传 · {item.StartedAt:MM-dd HH:mm}"
            : $"{item.SizeText} · {item.StateText}";

        Element detail = item.State switch
        {
            UploadState.Uploading or UploadState.Queued => Progress(item.Progress).Height(4).AutomationName($"{item.FileName} 上传进度"),
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

    private void CopyAllUrls()
    {
        var count = UploadQueue.Current.CopyAllLinks();
        if (count == 0)
        {
            return;
        }

        var settings = SettingsState.Current.Value;
        _setNotice(Notice.Info(settings.UrlMode == UrlMode.Signed
            ? $"已复制 {count} 条签名链接（{ObjectUrlBuilder.EffectiveExpireDays(settings)} 天有效）。"
            : $"已复制 {count} 条链接。"));
    }

    private void ClearHistory()
    {
        UploadQueue.Current.ClearFinished();
        _setNotice(Notice.Info("已清空上传记录。"));
    }
}
