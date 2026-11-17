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
        var pointing = _isHover && !hovered;
        var accent = hovered || pointing;
        var configured = _settings.IsConfigured;
        var protocol = _settings.Protocol == StorageProtocol.AliyunOss ? "阿里云 OSS" : "S3 兼容";
        var target = configured
            ? $"{_settings.Bucket} · {_settings.NormalizedEndpoint} · {protocol}"
            : "尚未配置存储";

        // 虚线描边：空闲用控件描边色，鼠标悬停或拖拽进入时换成强调色并加粗。
        var stroke = ThemeResource.Brush(accent ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush")
                     ?? new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0x86, 0x86, 0x86));

        // 目标桶只是信息展示，别让它挡住「整块区域可点击」。
        var badge = Border(
                TextBlock(target)
                    .FontSize(11)
                    .Foreground(Theme.TertiaryText)
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .MaxWidth(380))
            .Background(Theme.SubtleFill)
            .CornerRadius(10)
            .Padding(10, 3)
            .HAlign(HorizontalAlignment.Center)
            .Set(border => border.IsHitTestVisible = false);

        var title = hovered ? "松手即开始上传" : "点击选择文件，或把文件拖到这里";
        var subtitle = hovered ? "松开鼠标就加入上传队列" : "支持多文件与文件夹 · 上传完成后自动复制链接";

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

                        // 整块区域居中的说明文字（不再放「选择文件」按钮，整个框都是热区）。
                        VStack(
                                10,
                                Icon("Upload")
                                    .Width(hovered ? 30 : 26)
                                    .Height(hovered ? 30 : 26)
                                    .HAlign(HorizontalAlignment.Center)
                                    .Set(icon => icon.Foreground = ThemeResource.Brush(accent ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush") ?? icon.Foreground),
                                TextBlock(title)
                                    .FontSize(17)
                                    .SemiBold()
                                    .HAlign(HorizontalAlignment.Center)
                                    .Set(text => text.TextAlignment = TextAlignment.Center),
                                TextBlock(subtitle)
                                    .FontSize(12)
                                    .Foreground(Theme.SecondaryText)
                                    .HAlign(HorizontalAlignment.Center)
                                    .Set(text => text.TextAlignment = TextAlignment.Center))
                            .Center()
                            .Grid(row: 0, column: 0, rowSpan: 2),
                    ]))
            .Background(accent ? Theme.SubtleFill : Theme.CardBackground)
            .CornerRadius(16)
            .Padding(16)
            .OnMount(border =>
            {
                // 整块区域点击即打开文件选择器（Border 有背景，所以整块都能命中）。
                border.Tapped += (_, _) => OnPickFile();
            })
            .OnPointerEntered((_, _) => _setHover(true))
            .OnPointerExited((_, _) => _setHover(false))
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
    /// 入口是拖拽区的整块点击（见 <see cref="BuildDropZone"/>）。
    /// 不用 Reactor 的 UseFilePickerAsync：那是渲染期钩子，在事件里调用拿不到宿主窗口。
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
