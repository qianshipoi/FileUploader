using System.Collections.Immutable;
using PropertyChangedEventArgs = System.ComponentModel.PropertyChangedEventArgs;
using FileUploader.Core;
using FileUploader.Core.Storage;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace FileUploader.App;

/// <summary>
/// 主窗口：上传、历史记录与复制链接。配置在独立的设置窗口里，
/// 上传队列与「简洁模式」气泡共享（<see cref="UploadQueue"/>）。
/// </summary>
public sealed partial class UploadApp : Component
{
    // 每次 Render 时刷新，供事件处理器使用。
    private AppSettings _settings = SettingsState.Current.Value;
    private ImmutableArray<UploadItem> _items = UploadQueue.Current.Items;
    private bool _isDragOver;
    private Action<bool> _setDragOver = _ => { };
    private bool _isHover;
    private Action<bool> _setHover = _ => { };
    private Notice? _notice;
    private Action<Notice?> _setNotice = _ => { };
    private Guid? _copiedId;
    private Action<Guid?> _setCopiedId = _ => { };
    private bool _isPicking;

    // 历史记录的搜索与下载状态（只在本窗口内有效，不落盘）。
    private string _searchText = string.Empty;
    private Action<string> _setSearchText = _ => { };
    private Guid? _downloadingId;
    private Action<Guid?> _setDownloadingId = _ => { };
    private double _downloadProgress;
    private Action<double> _setDownloadProgress = _ => { };

    /// <inheritdoc />
    public override Element Render()
    {
        var (settings, setSettings) = UseState(SettingsState.Current.Value, threadSafe: true);
        var (items, setItems) = UseState(UploadQueue.Current.Items, threadSafe: true);
        var (isDragOver, setDragOver) = UseState(false);
        var (isHover, setHover) = UseState(false);
        var (notice, setNotice) = UseState<Notice?>(null, threadSafe: true);
        var (copiedId, setCopiedId) = UseState<Guid?>(null, threadSafe: true);
        var (searchText, setSearchText) = UseState(string.Empty);
        var (downloadingId, setDownloadingId) = UseState<Guid?>(null, threadSafe: true);
        var (downloadProgress, setDownloadProgress) = UseState(0d, threadSafe: true);

        _settings = settings;
        _items = items;
        _isDragOver = isDragOver;
        _setDragOver = setDragOver;
        _isHover = isHover;
        _setHover = setHover;
        _notice = notice;
        _setNotice = setNotice;
        _copiedId = copiedId;
        _setCopiedId = setCopiedId;
        _searchText = searchText;
        _setSearchText = setSearchText;
        _downloadingId = downloadingId;
        _setDownloadingId = setDownloadingId;
        _downloadProgress = downloadProgress;
        _setDownloadProgress = setDownloadProgress;

        // 配置变化（设置窗口 / 简洁模式里改的）同步到主窗口。
        UseEffect(
            () =>
            {
                void Handler(object? sender, PropertyChangedEventArgs e) => setSettings(SettingsState.Current.Value);
                SettingsState.Current.PropertyChanged += Handler;
                return () => SettingsState.Current.PropertyChanged -= Handler;
            },
            Array.Empty<object>());

        // 上传队列由气泡和主窗口共用，这里跟着刷新并把结果变成提示条。
        UseEffect(
            () =>
            {
                void OnQueueChanged(object? sender, PropertyChangedEventArgs e) => setItems(UploadQueue.Current.Items);

                void OnUploaded(UploadItem item)
                {
                    var current = SettingsState.Current.Value;
                    var suffix = current.UrlMode == UrlMode.Signed
                        ? $"，已复制 {ObjectUrlBuilder.EffectiveExpireDays(current)} 天有效的签名链接。"
                        : "，链接已复制到剪贴板。";
                    setNotice(Notice.Info($"已上传 {item.FileName}" + (current.AutoCopyUrl ? suffix : "。")));
                }

                void OnFailed(UploadItem item) => setNotice(Notice.Error($"{item.FileName} 上传失败：{item.Error}"));

                void OnRejected(string message) => setNotice(Notice.Error(message));

                UploadQueue.Current.PropertyChanged += OnQueueChanged;
                UploadQueue.Current.Uploaded += OnUploaded;
                UploadQueue.Current.Failed += OnFailed;
                UploadQueue.Current.Rejected += OnRejected;

                return () =>
                {
                    UploadQueue.Current.PropertyChanged -= OnQueueChanged;
                    UploadQueue.Current.Uploaded -= OnUploaded;
                    UploadQueue.Current.Failed -= OnFailed;
                    UploadQueue.Current.Rejected -= OnRejected;
                };
            },
            Array.Empty<object>());

        // 关闭窗口时默认收进托盘（托盘菜单里的「退出」才真正结束进程）。
        UseClosingGuard(() =>
        {
            if (AppWindows.IsExiting || !AppWindows.CanMinimizeToTray)
            {
                return true;
            }

            AppWindows.HideToTray();
            return false;
        });

        // 启动流程：--settings 时打开配置 → 导入的配置先自测连接 → 再把命令行文件入队。
        UseEffect(
            () =>
            {
                _ = StartupAsync();
            },
            Array.Empty<object>());

        return Grid(
                columns: [GridSize.Star()],
                rows: [GridSize.Auto, GridSize.Auto, GridSize.Star()],
                children:
                [
                    BuildHeader().Grid(row: 0).Margin(0, 0, 0, 14),
                    BuildBanner().Grid(row: 1).Margin(0, 0, 0, 12),
                    Grid(
                            rows: [GridSize.Px(212), GridSize.Star()],
                            columns: [GridSize.Star()],
                            children:
                            [
                                BuildDropZone().Grid(row: 0),
                                BuildHistoryCard().Grid(row: 1).Margin(0, 12, 0, 0),
                            ])
                        .Grid(row: 2),
                ])
            .Padding(20)
            .Background(Theme.SolidBackground);
    }

    private Element BuildHeader() =>
        Grid(
            columns: [GridSize.Star(), GridSize.Auto],
            rows: [GridSize.Auto],
            children:
            [
                VStack(
                        2,
                        TextBlock("OSS 拖拽上传").FontSize(22).SemiBold(),
                        TextBlock("拖进来就上传，链接自动进剪贴板")
                            .FontSize(12)
                            .Foreground(Theme.SecondaryText))
                    .Grid(column: 0),
                HStack(
                        8,
                        Button("简洁模式", AppWindows.EnterCompact).SubtleButton().ToolTip("收成右下角的小气泡"),
                        BuildSettingsIconButton())
                    .Grid(column: 1),
            ]);

    /// <summary>
    /// 图标字体：Win11 用 Segoe Fluent Icons，Win10（1809+）用 Segoe MDL2 Assets。
    /// 两个字体里 <c>\uE713</c> 都是齿轮，字形一致。
    /// </summary>
    private static string IconFontFamily =>
        Environment.OSVersion.Version.Build >= 22000 ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    /// <summary>设置按钮：只有齿轮图标（文字按钮太占头部空间），靠 tooltip 和自动化名说明用途。</summary>
    private static Element BuildSettingsIconButton() =>
        Button("\uE713", AppWindows.OpenSettings)
            .SubtleButton()
            .FontFamily(IconFontFamily)
            .FontSize(16)
            .ToolTip("设置")
            .AutomationName("设置")
            // WinUI 按钮默认 MinWidth=120，图标按钮必须清掉，否则会撑成一条长条。
            .MinWidth(0)
            .MinHeight(0)
            .Padding(11, 7, 11, 7);

    /// <summary>未配置时给出一次性引导；有临时提示时优先显示提示。</summary>
    private Element BuildBanner()
    {
        if (_notice is { } notice)
        {
            return InfoBar()
                .Set(bar =>
                {
                    bar.Message = notice.Message;
                    bar.Severity = notice.IsError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
                    bar.IsOpen = true;
                })
                .Closed(() => _setNotice(null));
        }

        if (_settings.IsConfigured)
        {
            return Empty();
        }

        return Border(
                Grid(
                    columns: [GridSize.Star(), GridSize.Auto],
                    rows: [GridSize.Auto],
                    children:
                    [
                        TextBlock("还没有配置存储。先填好 Endpoint、桶名和密钥，再拖文件进来。")
                            .FontSize(13)
                            .VAlign(VerticalAlignment.Center)
                            .Grid(column: 0),
                        Button("打开设置", AppWindows.OpenSettings).AccentButton().Grid(column: 1),
                    ]))
            .Background(Theme.SystemCautionBackground)
            .BorderBrush(Theme.SystemCaution)
            .BorderThickness(1)
            .CornerRadius(8)
            .Padding(12, 10);
    }
}
