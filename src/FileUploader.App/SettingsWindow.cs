using PropertyChangedEventArgs = System.ComponentModel.PropertyChangedEventArgs;
using System.Diagnostics;
using FileUploader.Core;
using FileUploader.Core.Storage;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace FileUploader.App;

/// <summary>
/// 独立的「存储配置」窗口。修改立即生效并自动保存，主窗口实时同步。
/// </summary>
public sealed class SettingsWindow : Component
{
    private AppSettings _settings = SettingsState.Current.Value;
    private bool _testing;
    private Action<bool> _setTesting = _ => { };
    private Notice? _notice;
    private Action<Notice?> _setNotice = _ => { };
    private ReactorWindow? _window;

    /// <inheritdoc />
    public override Element Render()
    {
        var (settings, setSettings) = UseState(SettingsState.Current.Value, threadSafe: true);
        var (saveState, setSaveState) = UseState((SettingsState.Current.IsDirty, SettingsState.Current.LastSavedAt), threadSafe: true);
        var (testing, setTesting) = UseState(false, threadSafe: true);
        var (notice, setNotice) = UseState<Notice?>(SettingsState.LoadWarning is { } warning ? Notice.Error(warning) : null, threadSafe: true);

        _settings = settings;
        _testing = testing;
        _setTesting = setTesting;
        _notice = notice;
        _setNotice = setNotice;
        _window = UseWindow();

        // 跟随全局状态：主窗口改了配置这里也会同步；保存状态变化也会刷新。
        UseEffect(
            () =>
            {
                void Handler(object? sender, PropertyChangedEventArgs e)
                {
                    setSettings(SettingsState.Current.Value);
                    setSaveState((SettingsState.Current.IsDirty, SettingsState.Current.LastSavedAt));
                }

                SettingsState.Current.PropertyChanged += Handler;
                return () => SettingsState.Current.PropertyChanged -= Handler;
            },
            Array.Empty<object>());

        return Grid(
                rows: [GridSize.Auto, GridSize.Star(), GridSize.Auto],
                columns: [GridSize.Star()],
                children:
                [
                    BuildHeader().Grid(row: 0).Margin(0, 0, 0, 12),
                    ScrollView(VStack(12, BuildForm())).Grid(row: 1),
                    BuildFooter(saveState).Grid(row: 2).Margin(0, 12, 0, 0),
                ])
            .Padding(20)
            .Background(Theme.SolidBackground);
    }

    private Element BuildHeader()
    {
        var subtitle = new[]
        {
            _settings.Bucket,
            _settings.NormalizedEndpoint,
            _settings.Protocol == StorageProtocol.AliyunOss ? "阿里云 OSS" : "S3 兼容",
        }.Where(s => !string.IsNullOrWhiteSpace(s));

        return VStack(
            4,
            TextBlock("存储配置").FontSize(22).SemiBold(),
            TextBlock(_settings.IsConfigured ? string.Join(" · ", subtitle) : "填写 Endpoint、桶名和密钥后即可拖拽上传")
                .FontSize(12)
                .Foreground(Theme.SecondaryText),
            _notice is null
                ? Empty()
                : InfoBar()
                    .Set(bar =>
                    {
                        bar.Message = _notice!.Message;
                        bar.Severity = _notice.IsError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
                        bar.IsOpen = true;
                    })
                    .Closed(() => _setNotice(null))
                    .Margin(0, 6, 0, 0),
            SettingsExchange.ImportFileExists
                ? HStack(
                        8,
                        TextBlock($"exe 同目录存在 {SettingsExchange.FileName}（加密导出文件，启动时会导入）。")
                            .FontSize(11)
                            .Foreground(Theme.SystemCaution)
                            .TextWrapping(),
                        Button("删除它", DeleteImportFile).SubtleButton())
                    .Margin(0, 6, 0, 0)
                : Empty());
    }

    private Element BuildForm()
    {
        var protocolBox = VStack(
            4,
            TextBlock("协议").FontSize(12).Foreground(Theme.SecondaryText),
            ComboBox(["阿里云 OSS（V1 签名）", "S3 兼容（SigV4）"], (int)_settings.Protocol, OnProtocolChanged));

        var urlModeBox = VStack(
            4,
            TextBlock("链接类型").FontSize(12).Foreground(Theme.SecondaryText),
            ComboBox(
                ["签名链接（私有桶可直接下载，会过期）", "直链（需桶或对象公共读）"],
                (int)_settings.UrlMode,
                OnUrlModeChanged),
            TextBlock("签名有效期最长 7 天（阿里云与 S3 的一致限制），到期自动失效；每次复制都会重新生成。")
                .FontSize(11)
                .Foreground(Theme.TertiaryText)
                .TextWrapping());

        var expireBox = _settings.UrlMode == UrlMode.Signed
#pragma warning disable REACTOR_A11Y_003 // 标题通过工厂参数传入，分析器识别不到
            ? NumberBox(_settings.SignedUrlExpireDays, OnExpireDaysChanged, "签名有效期（天）")
                .Range(1, 7)
                .SpinButtons()
#pragma warning restore REACTOR_A11Y_003
            : Empty();

        return VStack(
            12,
            protocolBox,
            TextBox(_settings.Endpoint, value => SetField(s => s with { Endpoint = value }))
                .Header("Endpoint")
                .PlaceholderText("oss-cn-hangzhou.aliyuncs.com"),
            TextBox(_settings.Bucket, value => SetField(s => s with { Bucket = value }))
                .Header("桶名（Bucket）")
                .PlaceholderText("my-bucket"),
            TextBox(_settings.AccessKeyId, value => SetField(s => s with { AccessKeyId = value }))
                .Header("AccessKey ID")
                .PlaceholderText("LTAI..."),
            PasswordBox(_settings.AccessKeySecret, value => SetField(s => s with { AccessKeySecret = value }), "AccessKey Secret")
                .Header("AccessKey Secret"),
            urlModeBox,
            expireBox,
            Expander(
                    "高级设置",
                VStack(
                    10,
                    TextBox(_settings.KeyPrefix, value => SetField(s => s with { KeyPrefix = value }))
                        .Header("对象键前缀（可选）")
                        .PlaceholderText("images"),
                    TextBox(_settings.PublicBaseUrl, value => SetField(s => s with { PublicBaseUrl = value }))
                        .Header("自定义域名（可选，仅直链生效）")
                        .PlaceholderText("https://cdn.example.com"),
                    TextBox(_settings.Region, value => SetField(s => s with { Region = value }))
                        .Header("Region（SigV4 用）")
                        .PlaceholderText("us-east-1"),
                    CheckBox((bool?)_settings.UseDateFolder, value => SetField(s => s with { UseDateFolder = value }), "对象键按日期分组（yyyy/MM/dd）"),
                    CheckBox((bool?)_settings.PathStyle, value => SetField(s => s with { PathStyle = value }), "路径风格访问（endpoint/bucket/key）"),
                    CheckBox((bool?)_settings.UseHttps, value => SetField(s => s with { UseHttps = value }), "使用 HTTPS"),
                    _settings.UrlMode == UrlMode.Direct
                        ? CheckBox((bool?)_settings.PublicReadObject, value => SetField(s => s with { PublicReadObject = value }), "上传时把对象设为公共读（直链需要）")
                        : Empty(),
                    CheckBox((bool?)_settings.AutoCopyUrl, value => SetField(s => s with { AutoCopyUrl = value }), "上传成功后自动复制链接"),
                    CheckBox((bool?)_settings.FlashDropEnabled, value => SetField(s => s with { FlashDropEnabled = value }), "右下角拖拽浮现上传气泡（拖文件到屏幕右下角即上传）")))
                .HAlign(HorizontalAlignment.Stretch)
                .HorizontalContentAlignment(HorizontalAlignment.Stretch),
            TextBlock($"密钥用 Windows DPAPI 加密后保存在 {SettingsStore.FilePath}，仅当前 Windows 账户可解密。" +
                      (AppPaths.IsPortable ? "（便携模式：配置随 exe 所在目录）" : string.Empty))
                .FontSize(11)
                .Foreground(Theme.TertiaryText)
                .TextWrapping());
    }

    private void OnUrlModeChanged(int index) =>
        SetField(s => s with { UrlMode = index == 1 ? UrlMode.Direct : UrlMode.Signed });

    private void OnExpireDaysChanged(double value) =>
        SetField(s => s with { SignedUrlExpireDays = (int)Math.Clamp(Math.Round(value), 1, ObjectUrlBuilder.MaxExpireDays) });

    private Element BuildFooter((bool IsDirty, DateTimeOffset? LastSavedAt) saveState)
    {
        var (isDirty, lastSavedAt) = saveState;
        var saveText = isDirty
            ? "有未保存的更改…"
            : lastSavedAt is { } savedAt
                ? $"已保存 {savedAt:HH:mm:ss}"
                : "配置已加载";
        var saveColor = isDirty ? Theme.SystemCaution : Theme.SecondaryText;

        return Grid(
            columns: [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            rows: [GridSize.Auto],
            children:
            [
                HStack(
                        10,
                        BuildTestButton(),
                        Button("打开配置目录", OpenSettingsFolder).SubtleButton(),
                        Button("导出配置", ExportSettings).SubtleButton())
                    .Grid(column: 0),
                TextBlock(saveText)
                    .FontSize(12)
                    .Foreground(saveColor)
                    .HAlign(HorizontalAlignment.Right)
                    .VAlign(VerticalAlignment.Center)
                    .Margin(0, 0, 12, 0)
                    .Grid(column: 1),
                HStack(
                        8,
                        Button("保存", SaveNow),
                        Button("关闭", CloseWindow).AccentButton())
                    .Grid(column: 2),
            ]);
    }

    /// <summary>
    /// 「测试连接」按钮：图标（心电图）+ 文字的普通按钮。
    /// 测试中图标变成从左到右扫过的心电监护样式、文字变成「测试中…」并禁用按钮；
    /// 测试结束自动恢复。右侧不再单独放 ProgressRing。
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
            .ToolTip(_testing ? "正在连接存储服务…" : "用当前配置测一次连接（桶不存在会自动创建）");

    /// <summary>
    /// 用「当前全局值」做变换：连续输入/勾选时不会互相覆盖（每个字段都基于最新值叠加）。
    /// </summary>
    private void SetField(Func<AppSettings, AppSettings> transform) => SettingsState.Current.Update(transform);

    private void SaveNow()
    {
        SettingsState.Current.SaveNow();
        _setNotice(Notice.Info("配置已保存。"));
    }

    private void OnProtocolChanged(int index)
    {
        var protocol = index == 1 ? StorageProtocol.S3Compatible : StorageProtocol.AliyunOss;
        SetField(s => s with
        {
            Protocol = protocol,
            // S3 兼容服务（MinIO / RustFS）通常使用路径风格地址。
            PathStyle = protocol == StorageProtocol.S3Compatible,
            Region = protocol == StorageProtocol.S3Compatible && string.IsNullOrWhiteSpace(s.Region) ? "us-east-1" : s.Region,
        });
    }

    private async void OnTestConnection()
    {
        if (_testing)
        {
            return;
        }

        var missing = _settings.Validate();
        if (missing.Count > 0)
        {
            _setNotice(Notice.Error("请先填写：" + string.Join("、", missing)));
            return;
        }

        _setTesting(true);
        _setNotice(null);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var client = new ObjectStorageClient(_settings);
            var head = await client.HeadBucketAsync(cts.Token);
            if (head.Success)
            {
                _setNotice(Notice.Info($"连接成功：桶 {_settings.Bucket} 可访问。"));
                return;
            }

            if (head.StatusCode == 404)
            {
                var create = await client.CreateBucketAsync(cts.Token);
                _setNotice(
                    create.Success
                        ? Notice.Info($"桶 {_settings.Bucket} 不存在，已自动创建。")
                        : Notice.Error($"桶 {_settings.Bucket} 不存在，创建失败：{create.Describe()}"));
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

    private void CloseWindow()
    {
        SettingsState.Current.SaveNow();
        _window?.Close();
    }
}
