using PropertyChangedEventArgs = System.ComponentModel.PropertyChangedEventArgs;
using System.Diagnostics;
using System.Globalization;
using FileUploader.Core;
using FileUploader.Core.Storage;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace FileUploader.App;

/// <summary>
/// 独立的设置窗口：左边是分类（连接 / 上传 / 启动 / 关于），右边一页一屏，
/// 排法照 Windows 设置来 —— 页头大标题、卡片里一行一个设置、行间一条细分隔线。
/// <para>
/// 保存刻意分成两类：
/// <b>开关与下拉框</b>（静默启动、开机自启、链接类型、协议……）点了就生效、立即写盘；
/// <b>输入框</b>（Endpoint、桶名、密钥、前缀、域名、有效期）只改内存里的草稿，点「保存」才写盘。
/// 原来每敲一个字符就写一次 settings.json：既没必要（用户可能还在改），
/// 又会让主窗口在上传时读到一个写了一半的 Endpoint。
/// </para>
/// <para>
/// 草稿与已保存值的差异就是「未保存」：底部提示 + 保存按钮点亮；
/// 带着未保存内容点「关闭」或窗口 X 会先问一句（保存并关闭 / 不保存 / 取消）。
/// </para>
/// </summary>
public sealed class SettingsWindow : Component
{
    private static readonly string[] PageTitles = ["连接", "上传", "启动", "关于"];

    private static readonly string[] PageDescriptions =
    [
        "文件传到哪个桶、用哪个账号。",
        "对象键怎么排、复制出来的链接是什么样、上传成功后做什么。",
        "开机、托盘，以及右下角那个拖拽气泡。",
        "版本与检查更新，配置和数据文件放在哪儿。",
    ];

    // Segoe Fluent Icons 的字形：地球 / 上传 / 齿轮 / 问号
    private static readonly string[] PageGlyphs = ["\uE774", "\uE898", "\uE713", "\uE946"];

    /// <summary>界面上正在编辑的配置（含还没保存的输入）。</summary>
    private AppSettings _draft = SettingsState.Current.Value;

    private Action<AppSettings> _setDraft = _ => { };

    /// <summary>已经写进磁盘配置的那一份；与 <see cref="_draft"/> 不等就是「有未保存的修改」。</summary>
    private AppSettings _saved = SettingsState.Current.Value;

    private Action<AppSettings> _setSaved = _ => { };

    private int _page;
    private Action<int> _setPage = _ => { };

    /// <summary>「还有输入没保存」对话框是否打开。</summary>
    private bool _closeAskOpen;

    private Action<bool> _setCloseAskOpen = _ => { };

    /// <summary>用户已经在对话框里选过「保存并关闭 / 不保存」，关闭守卫要放行这一次。</summary>
    private bool _closeConfirmed;

    private bool _testing;
    private Action<bool> _setTesting = _ => { };
    private Notice? _notice;
    private Action<Notice?> _setNotice = _ => { };
    private ReactorWindow? _window;

    // 「关于与更新」+ 两个对话框（详情 / 下载进度）
    private UpdateCheckResult? _update;
    private Action<UpdateCheckResult?> _setUpdate = _ => { };
    private bool _checkingUpdate;
    private Action<bool> _setCheckingUpdate = _ => { };
    private bool _updateDetailOpen;
    private Action<bool> _setUpdateDetailOpen = _ => { };
    private bool _updateProgressOpen;
    private Action<bool> _setUpdateProgressOpen = _ => { };
    private bool _applyingUpdate;
    private Action<bool> _setApplyingUpdate = _ => { };
    private double _updateProgress;
    private Action<double> _setUpdateProgress = _ => { };
    private string? _updateError;
    private Action<string?> _setUpdateError = _ => { };
    private string? _downloadedPath;
    private Action<string?> _setDownloadedPath = _ => { };
    private string? _downloadedSha;
    private Action<string?> _setDownloadedSha = _ => { };
    private CancellationTokenSource? _updateCancellation;

    /// <summary>这一轮是安装版还是便携版（每次 Render 时读一次，决定按钮文案与更新方式）。</summary>
    private InstallKind _installKind = InstallKind.Portable;

    /// <summary>Render 已经跑过一次，说明 <c>_setXxx</c> 都接好了，可以直接触发检查。</summary>
    private bool _rendered;

    /// <summary>托盘点「检查更新」时置位：设置窗口还没渲染时先记着，挂载后自动查一次。</summary>
    private static bool _pendingUpdateCheck;

    /// <summary>从外面（托盘菜单）请求检查更新。</summary>
    internal static void RequestUpdateCheck() => _pendingUpdateCheck = true;

    /// <summary>窗口已经开着时，直接在这个实例上开始检查。</summary>
    internal void StartUpdateCheck()
    {
        _pendingUpdateCheck = false;
        if (_rendered)
        {
            _ = CheckUpdatesAsync();
            return;
        }

        _pendingUpdateCheck = true;
    }

    /// <inheritdoc />
    public override Element Render()
    {
        var (draft, setDraft) = UseState(SettingsState.Current.Value, threadSafe: true);
        var (saved, setSaved) = UseState(SettingsState.Current.Value, threadSafe: true);
        var (page, setPage) = UseState(0);
        var (closeAskOpen, setCloseAskOpen) = UseState(false);
        var (testing, setTesting) = UseState(false, threadSafe: true);
        var (notice, setNotice) = UseState<Notice?>(SettingsState.LoadWarning is { } warning ? Notice.Error(warning) : null, threadSafe: true);

        // 「关于与更新」的状态
        var (update, setUpdate) = UseState<UpdateCheckResult?>(null, threadSafe: true);
        var (checkingUpdate, setCheckingUpdate) = UseState(false, threadSafe: true);
        var (applyingUpdate, setApplyingUpdate) = UseState(false, threadSafe: true);
        var (updateProgress, setUpdateProgress) = UseState(0d, threadSafe: true);
        var (updateDetailOpen, setUpdateDetailOpen) = UseState(false, threadSafe: true);
        var (updateProgressOpen, setUpdateProgressOpen) = UseState(false, threadSafe: true);
        var (updateError, setUpdateError) = UseState<string?>(null, threadSafe: true);
        var (downloadedPath, setDownloadedPath) = UseState<string?>(null, threadSafe: true);
        var (downloadedSha, setDownloadedSha) = UseState<string?>(null, threadSafe: true);

        _draft = draft;
        _setDraft = setDraft;
        _saved = saved;
        _setSaved = setSaved;
        _page = page;
        _setPage = setPage;
        _closeAskOpen = closeAskOpen;
        _setCloseAskOpen = setCloseAskOpen;
        _testing = testing;
        _setTesting = setTesting;
        _notice = notice;
        _setNotice = setNotice;
        _window = UseWindow();
        _update = update;
        _setUpdate = setUpdate;
        _checkingUpdate = checkingUpdate;
        _setCheckingUpdate = setCheckingUpdate;
        _applyingUpdate = applyingUpdate;
        _setApplyingUpdate = setApplyingUpdate;
        _updateProgress = updateProgress;
        _setUpdateProgress = setUpdateProgress;
        _updateDetailOpen = updateDetailOpen;
        _setUpdateDetailOpen = setUpdateDetailOpen;
        _updateProgressOpen = updateProgressOpen;
        _setUpdateProgressOpen = setUpdateProgressOpen;
        _updateError = updateError;
        _setUpdateError = setUpdateError;
        _downloadedPath = downloadedPath;
        _setDownloadedPath = setDownloadedPath;
        _downloadedSha = downloadedSha;
        _setDownloadedSha = setDownloadedSha;
        _installKind = InstallLocation.Kind;
        _rendered = true;

        // 托盘菜单点「检查更新」进来的：窗口挂载后自动查一次。
        UseEffect(
            () =>
            {
                if (_pendingUpdateCheck)
                {
                    _pendingUpdateCheck = false;
                    _ = CheckUpdatesAsync();
                }

                return () => { };
            },
            Array.Empty<object>());

        // 跟随全局状态：主窗口/启动同步改了配置这里也会同步（只在没有草稿改动时覆盖输入框）。
        UseEffect(
            () =>
            {
                void Handler(object? sender, PropertyChangedEventArgs e)
                {
                    var current = SettingsState.Current.Value;
                    setSaved(current);
                    if (_draft == _saved)
                    {
                        setDraft(current);
                    }
                }

                SettingsState.Current.PropertyChanged += Handler;
                return () => SettingsState.Current.PropertyChanged -= Handler;
            },
            Array.Empty<object>());

        // 窗口 X / Alt+F4：有没保存的输入就先拦下来问一句。
        // 注意这是"钩子"：必须无条件地在 Render 里直接调用（放进 UseEffect 会被分析器拦下，且顺序不稳定）。
        UseClosingGuard(CanCloseWindow);

        // 导航栏之外只剩内容区：上面滚动（提示条 + 页头 + 卡片），下面固定一条保存/关闭。
        var content = Grid(
            rows: [GridSize.Star(), GridSize.Auto],
            columns: [GridSize.Star()],
            children:
            [
                BuildContent().Grid(row: 0),
                BuildFooter().Grid(row: 1).Margin(24, 8, 24, 16),
            ])
            .Background(ThemeBrushes.PageBackground);

        return Grid(
                rows: [GridSize.Star()],
                columns: [GridSize.Star()],
                children:
                [
                    BuildNav(content).Grid(row: 0),

                    // 三个模态对话框：挂在窗口树里，由 IsOpen 控制显示。
                    SafeDialog(BuildUpdateDetailDialog).Grid(row: 0, column: 0),
                    SafeDialog(BuildUpdateProgressDialog).Grid(row: 0, column: 0),
                    SafeDialog(BuildCloseAskDialog).Grid(row: 0, column: 0),
                ])
            .Background(ThemeBrushes.PageBackground);
    }

    /// <summary>
    /// 对话框构建失败时不要连累整个设置页：记一条 crash.log，界面退回空元素。
    /// （Reactor 的 ContentDialog 一旦构造/挂载失败，整棵 Render 都会失败，页面会全空。）
    /// </summary>
    private static Element SafeDialog(Func<Element> factory)
    {
        try
        {
            return factory();
        }
        catch (Exception ex)
        {
            CrashLog.Write("构建设置页对话框失败", ex);
            return Empty();
        }
    }

    /// <summary>
    /// 顶部只放提示条（保存出错、导入提示之类），没有提示时返回 null：
    /// 窗口标题栏已经写着「设置」，页头交给每一页自己的大标题。
    /// </summary>
    private Element? BuildNoticeBlock()
    {
        var rows = new List<Element>();
        if (_notice is not null)
        {
            rows.Add(InfoBar()
                .Set(bar =>
                {
                    bar.Message = _notice!.Message;
                    bar.Severity = _notice.IsError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
                    bar.IsOpen = true;
                })
                .Closed(() => _setNotice(null)));
        }

        if (SettingsExchange.ImportFileExists)
        {
            rows.Add(HStack(
                8,
                TextBlock($"exe 同目录存在 {SettingsExchange.FileName}（加密导出文件，启动时会导入）。")
                    .FontSize(12)
                    .Foreground(Theme.SystemCaution)
                    .TextWrapping(),
                Button("删除它", DeleteImportFile).SubtleButton()));
        }

        return rows.Count == 0 ? null : VStack(8, rows.ToArray());
    }

    /// <summary>
    /// 左侧导航直接用 WinUI 原生的 <c>NavigationView</c>：选中条、悬停、键盘导航、
    /// 窗口变窄时自动收成图标栏，全都是系统样式（这里自己拼一排按钮怎么调都差一口气）。
    /// </summary>
    private Element BuildNav(Element content)
    {
        var items = new NavigationViewItemData[PageTitles.Length];
        for (var i = 0; i < PageTitles.Length; i++)
        {
            items[i] = NavItem(PageTitles[i], PageGlyphs[i], i.ToString(CultureInfo.InvariantCulture));
        }

        return NavigationView(items, content) with
        {
            SelectedTag = _page.ToString(CultureInfo.InvariantCulture),
            OnSelectedTagChanged = OnNavTagChanged,
            PaneTitle = "设置",
            IsSettingsVisible = false,
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsPaneToggleButtonVisible = false,
            AlwaysShowHeader = false,
            OpenPaneLength = 180,
            CompactModeThresholdWidth = 640,
            ExpandedModeThresholdWidth = 900,
        };
    }

    private void OnNavTagChanged(string? tag)
    {
        if (int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var page)
            && page >= 0
            && page < PageTitles.Length
            && page != _page)
        {
            _setPage(page);
        }
    }

    private Element BuildContent()
    {
        var items = new List<Element>();
        if (BuildNoticeBlock() is { } notice)
        {
            items.Add(notice);
        }

        items.Add(VStack(
            4,
            TextBlock(PageTitles[_page]).FontSize(24).SemiBold(),
            TextBlock(PageDescriptions[_page]).FontSize(12).Foreground(Theme.SecondaryText).TextWrapping()));
        items.Add(BuildPageBody());

        return ScrollView(VStack(16, items.ToArray())).Padding(24, 16, 24, 8);
    }

    private Element BuildPageBody() => _page switch
    {
        1 => BuildUploadPage(),
        2 => BuildStartupPage(),
        3 => BuildAboutPage(),
        _ => BuildConnectionPage(),
    };

    private Element BuildConnectionPage()
    {
        var rows = new List<Element>
        {
            Row(
                "协议",
                null,
                ComboBox(["阿里云 OSS（V1 签名）", "S3 兼容（SigV4）"], (int)_draft.Protocol, OnProtocolChanged)
                    .AutomationName("协议")),
            Row(
                "Endpoint",
                "不含桶名",
                TextBox(_draft.Endpoint, value => SetDraft(s => s with { Endpoint = value }))
                    .PlaceholderText("oss-cn-hangzhou.aliyuncs.com")
                    .AutomationName("Endpoint")),
            Row(
                "桶名（Bucket）",
                null,
                TextBox(_draft.Bucket, value => SetDraft(s => s with { Bucket = value }))
                    .PlaceholderText("my-bucket")
                    .AutomationName("桶名（Bucket）")),
        };

        // Region 只对 SigV4 有意义：阿里云 V1 签名用不上，不显示，少一个要理解的字段。
        if (_draft.Protocol == StorageProtocol.S3Compatible)
        {
            rows.Add(Row(
                "Region",
                "SigV4 用；S3 兼容服务一般 us-east-1",
                TextBox(_draft.Region, value => SetDraft(s => s with { Region = value }))
                    .PlaceholderText("us-east-1")
                    .AutomationName("Region")));
        }

        rows.Add(Row(
            "AccessKey ID",
            null,
            TextBox(_draft.AccessKeyId, value => SetDraft(s => s with { AccessKeyId = value }))
                .PlaceholderText("LTAI...")
                .AutomationName("AccessKey ID")));
        rows.Add(Row(
            "AccessKey Secret",
            "DPAPI 加密保存",
            PasswordBox(_draft.AccessKeySecret, value => SetDraft(s => s with { AccessKeySecret = value }), "填写 AccessKey Secret")
                .AutomationName("AccessKey Secret")));
        rows.Add(Toggle(
            "使用 HTTPS",
            "http:// 只适合本机或内网服务",
            _draft.UseHttps,
            value => ApplyLive(s => s with { UseHttps = value })));
        rows.Add(Toggle(
            "路径风格地址",
            "MinIO / RustFS 一般要打开",
            _draft.PathStyle,
            value => ApplyLive(s => s with { PathStyle = value })));
        rows.Add(Row("连接测试", "桶不存在会自动创建", BuildTestButton(), controlWidth: null));

        return SettingsCard("存储服务", "Endpoint 与桶名决定文件传到哪里。", rows.ToArray());
    }

    private Element BuildUploadPage()
    {
        // 用固定时间与固定防重名段生成示例，免得每次重绘都跳一下。
        var keySample = ObjectKey.Build(_draft, "截图.png", null, DateTimeOffset.Now, "153012-a1b2c3d4");

        var keyCard = SettingsCard(
            "对象键",
            "决定文件在桶里叫什么名字。",
            Row(
                "对象键前缀（可选）",
                "例如 images",
                TextBox(_draft.KeyPrefix, value => SetDraft(s => s with { KeyPrefix = value }))
                    .PlaceholderText("images")
                    .AutomationName("对象键前缀")),
            Toggle(
                "按日期分组",
                "插入 yyyy/MM/dd 目录",
                _draft.UseDateFolder,
                value => ApplyLive(s => s with { UseDateFolder = value })),
            TextBlock("例：" + keySample)
                .FontSize(12)
                .Foreground(Theme.TertiaryText)
                .IsTextSelectionEnabled(true)
                .TextWrapping()
                .Padding(0, 0, 0, 12));

        var linkRows = new List<Element>
        {
            Row(
                "链接类型",
                null,
                ComboBox(
                        ["签名链接（私有桶可直接下载，会过期）", "直链（需桶或对象公共读）"],
                        (int)_draft.UrlMode,
                        OnUrlModeChanged)
                    .AutomationName("链接类型")),
        };

        if (_draft.UrlMode == UrlMode.Signed)
        {
#pragma warning disable REACTOR_A11Y_003 // 标题在左边的 Row 里，分析器看不到
            linkRows.Add(Row(
                "签名有效期（天）",
                "最长 7 天，到期失效",
                NumberBox(_draft.SignedUrlExpireDays, OnExpireDaysChanged)
                    .Range(1, ObjectUrlBuilder.MaxExpireDays)
                    .SpinButtons()
                    .AutomationName("签名有效期"),
                controlWidth: 160));
#pragma warning restore REACTOR_A11Y_003
        }
        else
        {
            linkRows.Add(Row(
                "自定义域名（可选）",
                "仅直链生效，例如 https://cdn.example.com",
                TextBox(_draft.PublicBaseUrl, value => SetDraft(s => s with { PublicBaseUrl = value }))
                    .PlaceholderText("https://cdn.example.com")
                    .AutomationName("自定义域名")));
        }

        linkRows.Add(Toggle(
            "自动复制链接",
            "上传成功后把链接写进剪贴板。",
            _draft.AutoCopyUrl,
            value => ApplyLive(s => s with { AutoCopyUrl = value })));
        linkRows.Add(Toggle(
            "多个文件打包成 ZIP 上传",
            "多文件时先打成一个 zip；单文件直传",
            _draft.ZipBatchUpload,
            value => ApplyLive(s => s with { ZipBatchUpload = value })));

        if (_draft.UrlMode == UrlMode.Direct)
        {
            linkRows.Add(Toggle(
                "对象设为公共读",
                "上传时给对象加 public-read ACL；直链要求公共读。",
                _draft.PublicReadObject,
                value => ApplyLive(s => s with { PublicReadObject = value })));
        }

        var linkCard = SettingsCard("链接与上传", "签名链接还是直链，以及上传成功后做什么。", linkRows.ToArray());

        return VStack(16, keyCard, linkCard);
    }

    private Element BuildStartupPage() =>
        SettingsCard(
            "启动与托盘",
            "关掉主窗口只是收进托盘；只有托盘菜单里的「退出」才真正结束进程。",
            Toggle(
                "静默启动",
                "打开程序时不弹主窗口，只在托盘待命；点托盘图标随时叫回来。",
                _draft.StartSilently,
                value => ApplyLive(s => s with { StartSilently = value })),
            Toggle(
                "开机自启",
                "登录后自动启动（当前用户，免管理员）；和「静默启动」一起开就是开机进托盘。",
                _draft.StartWithWindows,
                OnStartWithWindowsChanged),
            Toggle(
                "右下角拖拽浮现上传气泡",
                "拖文件到右下角就浮出气泡，松手即上传；关掉后不再创建隐形窗口。",
                _draft.FlashDropEnabled,
                value => ApplyLive(s => s with { FlashDropEnabled = value })));

    private Element BuildAboutPage() => VStack(16, BuildAboutCard(), BuildDataCard());

    /// <summary>
    /// 「关于与更新」：只留一行当前版本 + 状态摘要 + 按钮。
    /// 更新详情（变更清单）和下载进度都放到对话框里，不把卡片撑长。
    /// </summary>
    private Element BuildAboutCard()
    {
        var update = _update;
        var status = _checkingUpdate
            ? "正在向 GitHub 查询最新版本…"
            : update switch
            {
                null => "点「检查更新」会弹出对话框，列出这次更新了哪些地方。",
                { Status: UpdateCheckStatus.UpToDate } => $"已经是最新版本（{update.CurrentVersion}）。",
                { Status: UpdateCheckStatus.UpdateAvailable } => $"发现新版本 {update.LatestVersion}（当前 {update.CurrentVersion}）。",
                { Status: UpdateCheckStatus.Failed } => $"上次检查失败：{update.Error}",
                _ => string.Empty,
            };

        var statusColor = update?.Status switch
        {
            UpdateCheckStatus.UpdateAvailable => Theme.Accent,
            UpdateCheckStatus.UpToDate => Theme.SystemSuccess,
            UpdateCheckStatus.Failed => Theme.SystemCritical,
            _ => Theme.TertiaryText,
        };

        var actions = new List<Element>();
        if (update is not null && !_checkingUpdate)
        {
            actions.Add(Button("查看详情", () => _setUpdateDetailOpen(true)).SubtleButton().AutomationName("查看更新详情"));
        }

#pragma warning disable REACTOR_A11Y_001 // 文案是动态的（检查中…）
        actions.Add(Button(_checkingUpdate ? "检查中…" : "检查更新", OnCheckUpdates)
            .AutomationName("检查更新")
            .IsEnabled(!_checkingUpdate && !_applyingUpdate));
#pragma warning restore REACTOR_A11Y_001

        var versionRow = Grid(
            columns: [GridSize.Star(), GridSize.Auto],
            rows: [GridSize.Auto],
            children:
            [
                VStack(
                        3,
                        TextBlock($"当前版本 {AppVersion.Current}").FontSize(14),
                        TextBlock(status).FontSize(12).Foreground(statusColor).TextWrapping())
                    .Grid(column: 0)
                    .Margin(0, 0, 20, 0)
                    .VAlign(VerticalAlignment.Center),
                HStack(8, actions.ToArray()).Grid(column: 1).VAlign(VerticalAlignment.Center),
            ]).Padding(0, 10, 0, 10);

        return SettingsCard(
            "关于与更新",
            _installKind == InstallKind.Installed
                ? "安装版：更新会下载安装包、静默替换程序并自动重启，不用手动换文件。"
                : "便携版：更新只把新版 exe 下载到程序旁边，绝不自动替换你手上这份。",
            versionRow);
    }

    /// <summary>配置文件放哪儿、怎么带走。</summary>
    private Element BuildDataCard()
    {
        var pathRow = Grid(
            columns: [GridSize.Star(), GridSize.Auto],
            rows: [GridSize.Auto],
            children:
            [
                VStack(
                        3,
                        TextBlock("配置文件").FontSize(14),
                        TextBlock(SettingsStore.FilePath)
                            .FontSize(12)
                            .Foreground(Theme.SecondaryText)
                            .IsTextSelectionEnabled(true)
                            .TextWrapping())
                    .Grid(column: 0)
                    .Margin(0, 0, 20, 0)
                    .VAlign(VerticalAlignment.Center),
                HStack(
                        8,
                        Button("打开配置目录", OpenSettingsFolder).AutomationName("打开配置目录"),
                        Button("导出配置", ExportSettings).AutomationName("导出配置"))
                    .Grid(column: 1)
                    .VAlign(VerticalAlignment.Center),
            ]).Padding(0, 10, 0, 10);

        return SettingsCard(
            "配置与数据",
            "密钥用 Windows DPAPI 加密后保存，只有当前 Windows 账户能解密。",
            pathRow,
            TextBlock("导出会在 exe 同目录生成 FileUploader.export.bin（AES-GCM 加密，含密钥）；" +
                      "复制到对方机器上，第一次启动会自动导入并删掉这个文件。" +
                      (AppPaths.IsPortable ? "便携模式：配置跟着 exe 所在目录走。" : string.Empty))
                .FontSize(12)
                .Foreground(Theme.TertiaryText)
                .TextWrapping()
                .Padding(0, 0, 0, 12));
    }

    /// <summary>「发现新版本」对话框：版本对照 + 变更清单 + 打开发布页。</summary>
    private Element BuildUpdateDetailDialog()
    {
        var update = _update;
        var title = update?.Status switch
        {
            UpdateCheckStatus.UpdateAvailable => $"发现新版本 {update.LatestVersion}",
            UpdateCheckStatus.UpToDate => "已经是最新版本",
            UpdateCheckStatus.Failed => "检查更新失败",
            _ => "检查更新",
        };

        // 安装版走「下载安装包 → 静默安装 → 重启」，便携版只「下载新版」，按钮文案跟着变。
        var installed = _installKind == InstallKind.Installed;
        var hasAsset = update is { Status: UpdateCheckStatus.UpdateAvailable }
                       && (installed ? update.HasInstaller : update.AssetUrl is { Length: > 0 });
        var primaryText = hasAsset ? (installed ? "立即更新并重启" : "下载新版") : string.Empty;

        var dialog = new ContentDialogElement(title, BuildUpdateDetailContent(update), primaryText)
        {
            IsPrimaryButtonEnabled = primaryText.Length > 0,
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            IsOpen = _updateDetailOpen,
        };
        return dialog.Closed(OnUpdateDetailClosed);
    }

    private Element BuildUpdateDetailContent(UpdateCheckResult? update)
    {
        if (update is null)
        {
            return TextBlock("还没有检查过。").FontSize(12).Foreground(Theme.SecondaryText);
        }

        if (update.Status == UpdateCheckStatus.Failed)
        {
            return VStack(
                10,
                TextBlock(update.Error ?? "未知错误").FontSize(12).Foreground(Theme.SystemCritical).TextWrapping(),
                TextBlock("检查更新要访问 api.github.com；公司网络/墙内可能需要给系统代理放行。")
                    .FontSize(11)
                    .Foreground(Theme.TertiaryText)
                    .TextWrapping(),
                HStack(8, Button("打开发布页", OpenReleasePage).SubtleButton()));
        }

        var hasUpdate = update.Status == UpdateCheckStatus.UpdateAvailable;
        var changes = update.Changes ?? Array.Empty<string>();
        var published = update.PublishedAt is { } at ? at.ToString("yyyy-MM-dd") : null;
        var rows = new List<Element>();

        // 版本对照：有新版本时写"从哪儿到哪儿"，已经最新时写"你就是最新 + 这个版本什么时候发的"。
        rows.Add(TextBlock(hasUpdate
                ? $"当前版本 {update.CurrentVersion}  →  新版本 {update.LatestVersion}" + (published is null ? string.Empty : $"      发布于 {published}")
                : $"当前版本 {update.CurrentVersion} 就是最新版本（{update.LatestVersion}）" + (published is null ? string.Empty : $"      发布于 {published}"))
            .FontSize(12)
            .Foreground(Theme.SecondaryText));

        // 关键：不管有没有新版本，都把该版本的更新内容直接列出来 ——
        // 已经最新时也能看到"我正在用的这个版本改了什么"，不用去点发布页。
        if (changes.Count > 0)
        {
            rows.Add(TextBlock(hasUpdate ? $"本次更新 {changes.Count} 项：" : $"这个版本更新了 {changes.Count} 项：").FontSize(12));
            rows.Add(ScrollView(
                    VStack(4, changes
                        .Select((change, index) => TextBlock("· " + change)
                            .FontSize(12)
                            .Foreground(Theme.SecondaryText)
                            .TextWrapping()
                            .WithKey($"update-change-{index}"))
                        .ToArray()))
                .MaxHeight(260));
        }
        else
        {
            rows.Add(TextBlock("这个 Release 没有写变更清单。")
                .FontSize(11)
                .Foreground(Theme.TertiaryText)
                .TextWrapping());
        }

        if (hasUpdate)
        {
            var installed = _installKind == InstallKind.Installed;
            var assetName = installed ? update.InstallerName : update.AssetName;
            var assetUrl = installed ? update.InstallerUrl : update.AssetUrl;
            var assetSize = installed ? update.InstallerSize : update.AssetSize;

            if (string.IsNullOrEmpty(assetUrl))
            {
                rows.Add(TextBlock(installed
                        ? "这个 Release 没有安装包（…-setup.exe），请到发布页手动下载新版。"
                        : "这个 Release 没有 win-x64 单文件 exe，请到发布页手动下载。")
                    .FontSize(11)
                    .Foreground(Theme.SystemCaution)
                    .TextWrapping());
            }
            else
            {
                var sizeText = assetSize > 0 ? $"（约 {UploadItem.FormatSize(assetSize)}）" : string.Empty;
                rows.Add(TextBlock(installed
                        ? $"点「立即更新并重启」会下载安装包{sizeText}、校验 SHA256，然后静默安装并自动重启；配置与历史不受影响。"
                        : $"点「下载新版」会把新版本下载到程序旁边的 update-{update.LatestVersion}\\ 里{sizeText}、校验 SHA256，" +
                          "**不会**替换你现在这份；关掉程序后把新 exe 覆盖过来即可（想留旧版随时能留）。")
                    .FontSize(11)
                    .Foreground(Theme.TertiaryText)
                    .TextWrapping());

                if (!string.IsNullOrEmpty(assetName))
                {
                    rows.Add(TextBlock($"文件：{assetName}").FontSize(11).Foreground(Theme.TertiaryText).TextWrapping());
                }
            }
        }

        rows.Add(HStack(8, Button("打开发布页", OpenReleasePage).SubtleButton()));
        return VStack(10, rows.ToArray());
    }

    /// <summary>
    /// 下载/安装进度对话框。三种状态共用：
    /// 失败 → 说明原因；便携版下完 → 给出路径与哈希（不再自动替换）；其它 → 进度条。
    /// </summary>
    private Element BuildUpdateProgressDialog()
    {
        var update = _update;
        var failed = !string.IsNullOrEmpty(_updateError);
        var downloaded = !failed && !string.IsNullOrEmpty(_downloadedPath);
        var rows = new List<Element>();

        if (failed)
        {
            rows.Add(TextBlock(_updateError!).FontSize(12).Foreground(Theme.SystemCritical).TextWrapping());
            rows.Add(TextBlock(_installKind == InstallKind.Installed
                    ? $"没有替换任何文件，当前版本仍然是 {AppVersion.Current}。"
                    : $"当前这份程序没有任何改动，仍然是 {AppVersion.Current}。")
                .FontSize(11)
                .Foreground(Theme.TertiaryText)
                .TextWrapping());
        }
        else if (downloaded)
        {
            rows.Add(TextBlock($"新版 {update?.LatestVersion} 已下载并校验通过：").FontSize(12));
            rows.Add(TextBlock(_downloadedPath!)
                .FontSize(11)
                .Foreground(Theme.SecondaryText)
                .IsTextSelectionEnabled(true)
                .TextWrapping());
            if (_downloadedSha is { Length: > 0 } sha)
            {
                rows.Add(TextBlock($"SHA256 {sha}").FontSize(11).Foreground(Theme.TertiaryText).TextWrapping());
            }

            rows.Add(TextBlock($"关掉本程序后，把上面这个文件复制/改名成 {Path.GetFileName(Environment.ProcessPath ?? "FileUploader.exe")} " +
                               "覆盖到程序目录即可；想保留旧版就先把它复制一份。配置与历史不受影响。")
                .FontSize(11)
                .Foreground(Theme.TertiaryText)
                .TextWrapping());
        }
        else
        {
            rows.Add(TextBlock(_installKind == InstallKind.Installed
                    ? $"正在下载安装包 {update?.LatestVersion}（{_updateProgress:P0}）"
                    : $"正在下载新版本 {update?.LatestVersion}（{_updateProgress:P0}）")
                .FontSize(12));
            rows.Add(Progress(_updateProgress * 100).Height(6).AutomationName("更新下载进度"));
            rows.Add(TextBlock(_installKind == InstallKind.Installed
                    ? "下载完成后会校验 SHA256，校验通过才静默安装并自动重启。"
                    : "下载完成后会校验 SHA256；只下载，不会替换你现在这份程序。")
                .FontSize(11)
                .Foreground(Theme.TertiaryText)
                .TextWrapping());
        }

        var dialog = new ContentDialogElement(
            failed ? "更新失败" : downloaded ? "新版已下载" : "正在下载",
            VStack(10, rows.ToArray()),
            downloaded ? "打开所在目录" : string.Empty)
        {
            PrimaryButtonText = downloaded ? "打开所在目录" : string.Empty,
            CloseButtonText = failed || downloaded || !_applyingUpdate ? "关闭" : "取消",
            DefaultButton = ContentDialogButton.Close,
            IsOpen = _updateProgressOpen,
        };
        return dialog.Closed(OnUpdateProgressClosed);
    }

    private void OnCheckUpdates() => _ = CheckUpdatesAsync();

    private async Task CheckUpdatesAsync()
    {
        if (_checkingUpdate || _applyingUpdate)
        {
            return;
        }

        _setUpdate(null);
        _setUpdateError(null);
        _setDownloadedPath(null);
        _setDownloadedSha(null);
        // 上一次更新失败时进度对话框可能还开着（模态），先收掉，否则新结果对话框弹不出来。
        _setUpdateProgressOpen(false);
        _setCheckingUpdate(true);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await UpdateChecker.CheckAsync(AppVersion.Current, cts.Token);
            _setUpdate(result);
        }
        catch (OperationCanceledException)
        {
            _setUpdate(UpdateCheckResult.Fail(AppVersion.Current, "检查更新超时了，请检查网络或代理。"));
        }
        catch (Exception ex)
        {
            _setUpdate(UpdateCheckResult.Fail(AppVersion.Current, ex.Message));
        }
        finally
        {
            _setCheckingUpdate(false);
            // 结果（含失败原因）统一放在对话框里展示，卡片上只留一行摘要。
            _setUpdateDetailOpen(true);
        }
    }

    private void OnUpdateDetailClosed(ContentDialogResult result)
    {
        _setUpdateDetailOpen(false);
        if (result == ContentDialogResult.Primary)
        {
            _ = StartUpdateAsync();
        }
    }

    private void OnUpdateProgressClosed(ContentDialogResult result)
    {
        _setUpdateProgressOpen(false);
        if (_applyingUpdate)
        {
            // 还在下载：关对话框等于取消。
            _updateCancellation?.Cancel();
            _setApplyingUpdate(false);
        }

        if (result == ContentDialogResult.Primary && _downloadedPath is { Length: > 0 } path)
        {
            OpenInExplorer(path);
        }

        _setDownloadedPath(null);
        _setDownloadedSha(null);
    }

    /// <summary>
    /// 下载 → 校验 → （安装版）静默安装并重启 / （便携版）只把新版 exe 放着并告诉用户。
    /// </summary>
    private async Task StartUpdateAsync()
    {
        var update = _update;
        if (update is null || !update.HasUpdate || _applyingUpdate)
        {
            return;
        }

        if (UploadQueue.Current.IsBusy)
        {
            _setNotice(Notice.Error("还有文件在上传/打包，等它传完再更新，别把正在进行的传输打断。"));
            return;
        }

        // 这一轮按哪种方式更新，在开始时就定下来（下载过程中用户不会换配置）。
        var kind = _installKind;

        _setUpdateError(null);
        _setDownloadedPath(null);
        _setDownloadedSha(null);
        _setUpdateProgress(0);
        _setApplyingUpdate(true);
        _setUpdateProgressOpen(true);

        var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        _updateCancellation = cancellation;
        try
        {
            var progress = new Progress<double>(value => _setUpdateProgress(value));
            var download = await Updater.DownloadAsync(update, kind, progress, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            if (!download.Success || download.Path is null)
            {
                _setUpdateError(download.Error ?? "下载失败。");
                return;
            }

            // 动程序之前先把配置落盘，免得刚改的设置丢掉。
            SettingsState.Current.SaveNow();

            if (kind == InstallKind.Installed)
            {
                // 安装版：把活交给安装器（换 exe、快捷方式、卸载项都由它负责），然后自己退出。
                var apply = Updater.RunInstaller(download.Path);
                if (!apply.Success)
                {
                    _setUpdateError(apply.Error ?? "启动安装包失败。");
                    return;
                }

                _setUpdateProgressOpen(false);
                AppWindows.ExitApplication();
                return;
            }

            // 便携版：只下载。程序不动，用户自己决定什么时候换。
            _setDownloadedPath(download.Path);
            _setDownloadedSha(update.AssetSha256);
            _setUpdateProgress(1);
        }
        catch (OperationCanceledException)
        {
            _setUpdateError("已取消更新。");
        }
        catch (Exception ex)
        {
            _setUpdateError($"更新失败：{ex.Message}");
        }
        finally
        {
            _updateCancellation = null;
            cancellation.Dispose();
            _setApplyingUpdate(false);
        }
    }

    /// <summary>在资源管理器里选中刚下载的文件（找不到文件时退回打开所在目录）。</summary>
    private void OpenInExplorer(string path)
    {
        try
        {
            var target = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _setNotice(Notice.Error($"打不开所在目录：{ex.Message}"));
        }
    }

    private void OpenReleasePage()
    {
        var url = _update?.ReleasePageUrl ?? UpdateChecker.ReleasesPageUrl;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _setNotice(Notice.Error($"打不开发布页：{ex.Message}"));
        }
    }
    /// <summary>带着未保存的输入点关闭/点 X 时问一句。</summary>
    private Element BuildCloseAskDialog()
    {
        var dialog = new ContentDialogElement(
            "有输入还没保存",
            VStack(
                8,
                TextBlock("Endpoint、桶名、密钥这些输入框里的改动不会自动保存。")
                    .FontSize(12)
                    .TextWrapping(),
                TextBlock("「保存并关闭」会写进配置文件；「不保存」丢掉这次的输入，已经点过的开关不受影响。")
                    .FontSize(12)
                    .Foreground(Theme.SecondaryText)
                    .TextWrapping()),
            "保存并关闭")
        {
            SecondaryButtonText = "不保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            IsOpen = _closeAskOpen,
        };
        return dialog.Closed(OnCloseAskClosed);
    }

    private void OnCloseAskClosed(ContentDialogResult result)
    {
        _setCloseAskOpen(false);
        if (result == ContentDialogResult.Primary)
        {
            SaveDraft();
            CloseNow();
        }
        else if (result == ContentDialogResult.Secondary)
        {
            // 丢弃这次的输入（已经生效的开关保留）。
            _setDraft(_saved);
            CloseNow();
        }
    }

    /// <summary>
    /// 一张设置卡片：标题 + 一句说明 + 若干行，行之间一条 1px 分隔线。
    /// 这是 Windows 设置页的排法（卡片 8px 圆角、1px 描边由 <see cref="Factories.Card"/> 给）。
    /// </summary>
    private static Element SettingsCard(string title, string? description, params Element[] rows)
    {
        var header = new List<Element> { TextBlock(title).FontSize(14).SemiBold() };
        if (!string.IsNullOrEmpty(description))
        {
            header.Add(TextBlock(description).FontSize(12).Foreground(Theme.SecondaryText).TextWrapping());
        }

        var body = new List<Element>();
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0)
            {
                body.Add(Divider());
            }

            body.Add(rows[i]);
        }

        return Card(
            VStack(
                0,
                VStack(2, header.ToArray()).Margin(0, 0, 0, 4),
                VStack(0, body.ToArray())));
    }

    /// <summary>卡片内行与行之间的 1px 分隔线。</summary>
    private static Element Divider() => Border(null).Height(1).Background(Theme.DividerStroke);

    /// <summary>
    /// 一行设置：左边标题（可选一句灰字说明），右边控件。
    /// <paramref name="controlWidth"/> 为 null 时按控件自身宽度靠右放（开关、按钮用这个）。
    /// </summary>
    private static Element Row(string title, string? description, Element control, double? controlWidth = 340)
    {
        Element right = control.Grid(column: 1).VAlign(VerticalAlignment.Center);
        if (controlWidth is double width)
        {
            right = right.Width(width);
        }

        return Grid(
                columns: [GridSize.Star(), GridSize.Auto],
                rows: [GridSize.Auto],
                children:
                [
                    VStack(
                            2,
                            TextBlock(title).FontSize(14),
                            description is null
                                ? Empty()
                                : TextBlock(description).FontSize(12).Foreground(Theme.SecondaryText).TextWrapping())
                        .Grid(column: 0)
                        .Margin(0, 0, 24, 0)
                        .VAlign(VerticalAlignment.Center),
                    right,
                ])
            .Padding(0, 10, 0, 10);
    }

    /// <summary>开关行：和 <see cref="Row"/> 同一套排法，右边放开关。</summary>
    private static Element Toggle(string title, string description, bool isOn, Action<bool> onChanged) =>
        Row(
            title,
            description,
            ToggleSwitch(isOn, onChanged, string.Empty, string.Empty).AutomationName(title),
            controlWidth: null);

    /// <summary>
    /// 「测试连接」按钮：图标（心电图）+ 文字的普通按钮。
    /// 测试中图标变成从左到右扫过的心电监护样式、文字变成「测试中…」并禁用按钮；
    /// 测试结束自动恢复。
    /// </summary>
    private Element BuildTestButton() =>
        Button(
                HStack(
                        8,
                        Component<TestPulseIcon, bool>(_testing),
#pragma warning disable REACTOR_A11Y_001 // 文案是动态的，分析器看不到
                        TextBlock(_testing ? "测试中…" : "测试连接").FontSize(14))
#pragma warning restore REACTOR_A11Y_001
                    .VAlign(VerticalAlignment.Center),
                OnTestConnection)
            .AutomationName(_testing ? "正在测试连接" : "测试连接")
            .ToolTip(_testing ? "正在连接存储服务…" : "用输入框里的当前配置测一次连接（桶不存在会自动创建）");

    private Element BuildFooter()
    {
        var dirty = _draft != _saved;
        var status = dirty
            ? "有输入还没保存"
            : SettingsState.Current.LastSavedAt is { } savedAt
                ? $"已保存 {savedAt:HH:mm:ss}"
                : "配置已加载";

        return VStack(
            0,
            Divider().Margin(0, 0, 0, 12),
            Grid(
                columns: [GridSize.Star(), GridSize.Auto],
                rows: [GridSize.Auto],
                children:
                [
                    TextBlock(status)
                        .FontSize(12)
                        .Foreground(dirty ? Theme.SystemCaution : Theme.SecondaryText)
                        .VAlign(VerticalAlignment.Center),
                    HStack(
                            8,
                            Button("保存", SaveDraft).AccentButton().IsEnabled(dirty).AutomationName("保存"),
                            Button("关闭", RequestClose).AutomationName("关闭"))
                        .Grid(column: 1),
                ]));
    }

    /// <summary>输入框改动：只写草稿，不落盘（等用户点「保存」）。</summary>
    private void SetDraft(Func<AppSettings, AppSettings> transform)
    {
        // 字段立刻跟上，不等下一次 Render：否则「保存」后马上点关闭，守卫会拿着旧值
        // 误判成"还有未保存的输入"。
        _draft = transform(_draft);
        _setDraft(_draft);
    }

    /// <summary>
    /// 开关 / 下拉框改动：立即生效并写盘（这类选项只有"要"或"不要"，没有输一半的状态）。
    /// 草稿同步跟上，否则「有输入还没保存」会被开关的改动污染。
    /// </summary>
    private void ApplyLive(Func<AppSettings, AppSettings> change)
    {
        SettingsState.Current.Update(change);
        SettingsState.Current.SaveNow();
        _draft = change(_draft);
        _saved = SettingsState.Current.Value;
        _setDraft(_draft);
        _setSaved(_saved);
    }

    private void SaveDraft()
    {
        var draft = _draft;
        SettingsState.Current.Update(_ => draft);
        SettingsState.Current.SaveNow();
        _saved = SettingsState.Current.Value;
        _setDraft(draft);
        _setSaved(_saved);
        _setNotice(Notice.Info("配置已保存。"));
    }

    /// <summary>有没保存的输入就问一句；否则直接关。</summary>
    private void RequestClose()
    {
        if (_draft == _saved || AppWindows.IsExiting)
        {
            CloseNow();
            return;
        }

        _setCloseAskOpen(true);
    }

    private void CloseNow()
    {
        _closeConfirmed = true;
        if (_window is null)
        {
            return;
        }

        // 在按钮/对话框回调里直接调 _window.Close() 不生效（实测窗口不关、Closing 也不触发），
        // 投递到消息循环里再关，走的是和点 X 一样的关闭流程。
        var window = _window;
        window.NativeWindow.DispatcherQueue.TryEnqueue(() => window.Close());
    }

    /// <summary>窗口 X / Alt+F4 的守卫：返回 false 取消这次关闭（先把对话框弹出来）。</summary>
    private bool CanCloseWindow()
    {
        if (_closeConfirmed || AppWindows.IsExiting || _draft == _saved)
        {
            return true;
        }

        _setCloseAskOpen(true);
        return false;
    }

    private void OnProtocolChanged(int index)
    {
        var protocol = index == 1 ? StorageProtocol.S3Compatible : StorageProtocol.AliyunOss;
        ApplyLive(s => s with
        {
            Protocol = protocol,
            // S3 兼容服务（MinIO / RustFS）通常使用路径风格地址。
            PathStyle = protocol == StorageProtocol.S3Compatible,
            Region = protocol == StorageProtocol.S3Compatible && string.IsNullOrWhiteSpace(s.Region) ? "us-east-1" : s.Region,
        });
    }

    private void OnUrlModeChanged(int index) =>
        ApplyLive(s => s with { UrlMode = index == 1 ? UrlMode.Direct : UrlMode.Signed });

    private void OnExpireDaysChanged(double value) =>
        SetDraft(s => s with { SignedUrlExpireDays = (int)Math.Clamp(Math.Round(value), 1, ObjectUrlBuilder.MaxExpireDays) });

    /// <summary>
    /// 开机自启的开关：先写注册表再落配置。
    /// 写不进去（策略/权限）就把开关回滚成原状态并给红色提示 —— 不能让界面显示"已开启"而实际没有。
    /// </summary>
    private void OnStartWithWindowsChanged(bool enabled)
    {
        ApplyLive(s => s with { StartWithWindows = enabled });
        if (StartupRegistration.TrySet(enabled, out var error))
        {
            _setNotice(Notice.Info(enabled
                ? "已加入开机自启（当前用户）；在任务管理器的「启动」页也能看到并关掉。"
                : "已取消开机自启。"));
            return;
        }

        ApplyLive(s => s with { StartWithWindows = !enabled });
        _setNotice(Notice.Error($"{(enabled ? "开启" : "关闭")}开机自启失败：{error}"));
    }

    private async void OnTestConnection()
    {
        if (_testing)
        {
            return;
        }

        // 用输入框里的当前内容测（哪怕还没保存），这样"改完先试一下"就能直接试。
        var candidate = _draft;
        var missing = candidate.Validate();
        if (missing.Count > 0)
        {
            _setNotice(Notice.Error("请先填写：" + string.Join("、", missing)));
            return;
        }

        _setTesting(true);
        _setNotice(null);
        var unsaved = _draft != _saved;
        var savedHint = unsaved ? "（用的是输入框里的内容，点「保存」才会生效）" : string.Empty;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var client = new ObjectStorageClient(candidate);
            var head = await client.HeadBucketAsync(cts.Token);
            if (head.Success)
            {
                _setNotice(Notice.Info($"连接成功：桶 {candidate.Bucket} 可访问。{savedHint}"));
                return;
            }

            if (head.StatusCode == 404)
            {
                var create = await client.CreateBucketAsync(cts.Token);
                _setNotice(
                    create.Success
                        ? Notice.Info($"桶 {candidate.Bucket} 不存在，已自动创建。{savedHint}")
                        : Notice.Error($"桶 {candidate.Bucket} 不存在，创建失败：{create.Describe()}"));
                return;
            }

            _setNotice(Notice.Error($"连接失败：{head.Describe()}"));
        }
        catch (OperationCanceledException)
        {
            _setNotice(Notice.Error("连接超时（30 秒），请检查 Endpoint 与网络。"));
        }
        catch (Exception ex)
        {
            _setNotice(Notice.Error($"连接失败：{ex.Message}"));
        }
        finally
        {
            _setTesting(false);
        }
    }

    /// <summary>手动删除同目录的明文导出文件（自动测试没通过时用）。</summary>
    private void DeleteImportFile()
    {
        SettingsExchange.DeleteImportFile();
        _setNotice(Notice.Info("已删除 exe 同目录的导出文件。"));
    }

    /// <summary>导出可跨机器使用的配置（含明文密钥），放到 exe 同目录。</summary>
    private void ExportSettings()
    {
        var result = SettingsExchange.Export(SettingsState.Current.Value);
        _setNotice(result.Success
            ? Notice.Info($"已导出到 {result.Path}（AES-GCM 加密的二进制文件；对方首次启动会自动导入）。")
            : Notice.Error($"导出失败：{result.Error}"));
    }

    private static void OpenSettingsFolder()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsStore.FilePath)!);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SettingsStore.FilePath}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 打不开资源管理器时忽略。
        }
    }
}
