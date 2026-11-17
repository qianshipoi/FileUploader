using FileUploader.Core;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using static Microsoft.UI.Reactor.Factories;

namespace FileUploader.App;

public sealed partial class UploadApp
{
    private Element BuildDropZone()
    {
        var hovered = _isDragOver;
        var configured = _settings.IsConfigured;
        var protocol = _settings.Protocol == StorageProtocol.AliyunOss ? "阿里云 OSS" : "S3 兼容";
        var target = configured
            ? $"{_settings.Bucket} · {_settings.NormalizedEndpoint} · {protocol}"
            : "尚未配置存储";

        // 虚线描边：空闲用控件描边色，拖拽进入时换成强调色并加粗。
        var stroke = ThemeResource.Brush(hovered ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush")
                     ?? new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0x86, 0x86, 0x86));

        var badge = Border(
                TextBlock(target)
                    .FontSize(11)
                    .Foreground(Theme.TertiaryText)
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .MaxWidth(380))
            .Background(Theme.SubtleFill)
            .CornerRadius(10)
            .Padding(10, 3)
            .HAlign(HorizontalAlignment.Center);

        return Border(
                Grid(
                    columns: [GridSize.Star()],
                    rows: [GridSize.Auto, GridSize.Star()],
                    children:
                    [
                        Rectangle()
                            .Set(rect =>
                            {
                                rect.RadiusX = 16;
                                rect.RadiusY = 16;
                                rect.Stroke = stroke;
                                rect.StrokeThickness = hovered ? 2 : 1.5;
                                rect.StrokeDashArray = new DoubleCollection { 6, 4 };
                                rect.Stretch = Stretch.Fill;
                            })
                            .Grid(row: 0, column: 0, rowSpan: 2),
                        badge.Grid(row: 0, column: 0).HAlign(HorizontalAlignment.Right).Margin(0, 0, 2, 0),
                        VStack(
                                8,
                                Icon("Upload")
                                    .Width(hovered ? 30 : 26)
                                    .Height(hovered ? 30 : 26)
                                    .Set(icon => icon.Foreground = ThemeResource.Brush(hovered ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush") ?? icon.Foreground)
                                    .Margin(0, 0, 0, 2),
                                TextBlock(hovered ? "松手即开始上传" : "把文件拖到这里")
                                    .FontSize(17)
                                    .SemiBold(),
                                TextBlock("支持多文件与文件夹，上传完成后自动复制链接")
                                    .FontSize(12)
                                    .Foreground(Theme.SecondaryText),
                                Button("选择文件…", OnPickFile)
                                    .AccentButton()
                                    .Margin(0, 6, 0, 0))
                            .Center()
                            .Grid(row: 1, column: 0),
                    ]))
            .Background(hovered ? Theme.SubtleFill : Theme.CardBackground)
            .CornerRadius(16)
            .Padding(16)
            .OnDragEnter(args =>
            {
                args.UIOverride.Caption = configured ? "上传到 OSS" : "请先在设置中配置存储";
                args.UIOverride.IsCaptionVisible = true;
                _setDragOver(true);
            })
            .OnDragOver(args => args.AcceptedOperation = DragOperations.Copy)
            .OnDragLeave(_ => _setDragOver(false))
            .OnDrop(OnDrop);
    }

    private void OnDrop(DragTargetArgs args)
    {
        _setDragOver(false);
        _ = HandleDropAsync(args);
    }

    private async Task HandleDropAsync(DragTargetArgs args)
    {
        var storageItems = await DropHelper.GetItemsAsync(args);
        if (storageItems.Count == 0)
        {
            return;
        }

        await UploadQueue.Current.EnqueueAsync(storageItems);
    }

    /// <summary>
    /// 用 WinUI 的 FileOpenPicker 选文件（支持多选）。
    /// 不用 Reactor 的 UseFilePickerAsync：那是渲染期钩子，在按钮事件里调用拿不到宿主窗口。
    /// </summary>
    private async void OnPickFile()
    {
        // 防止连点打开多个模态对话框。
        if (_isPicking)
        {
            return;
        }

        _isPicking = true;
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List,
                CommitButtonText = "上传",
            };
            picker.FileTypeFilter.Add("*");

            var nativeWindow = ReactorApp.PrimaryWindow?.NativeWindow;
            if (nativeWindow is null)
            {
                _setNotice(Notice.Error("找不到主窗口，无法打开文件选择器。"));
                return;
            }

            // 非打包应用必须把 picker 绑定到窗口句柄，否则会直接抛异常。
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow));

            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0)
            {
                return;
            }

            await UploadQueue.Current.EnqueueAsync(files.Cast<IStorageItem>().ToArray());
        }
        catch (Exception ex)
        {
            _setNotice(Notice.Error($"选择文件失败：{ex.Message}"));
        }
        finally
        {
            _isPicking = false;
        }
    }
}
