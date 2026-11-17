using FileUploader.Core;
using Microsoft.UI.Reactor;

namespace FileUploader.App;

/// <summary>应用入口。</summary>
public static class Program
{
    /// <summary>
    /// 启动时随命令行传入的文件（把文件拖到 exe 图标 / 快捷方式上时由此进入）。
    /// </summary>
    public static IReadOnlyList<string> StartupFiles { get; private set; } = Array.Empty<string>();

    /// <summary>带 <c>--settings</c> 启动时直接打开配置窗口。</summary>
    public static bool OpenSettingsOnStartup { get; private set; }

    /// <summary>带 <c>--compact</c> 启动时直接进入简洁模式（圆形气泡）。</summary>
    public static bool OpenCompactOnStartup { get; private set; }

    /// <summary>启动时导入同目录配置的结果说明（给界面提示用）。</summary>
    public static string? ImportNotice { get; private set; }

    /// <summary>导入提示是否为错误。</summary>
    public static bool ImportNoticeIsError { get; private set; }

    /// <summary>本次启动是否导入了同目录配置（需要先自动测试连接）。</summary>
    public static bool AutoVerifyPending { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
        CrashLog.Install();
        ApplyImportedSettings();
        StartupFiles = ParseFiles(args);
        OpenSettingsOnStartup = args.Any(a => string.Equals(a.Trim(), "--settings", StringComparison.OrdinalIgnoreCase));
        OpenCompactOnStartup = args.Any(a => string.Equals(a.Trim(), "--compact", StringComparison.OrdinalIgnoreCase));

        ReactorApp.Run<UploadApp>("OSS 拖拽上传", width: 1040, height: 780);
    }

    /// <summary>
    /// 启动时检查 exe 同目录有没有导出的配置：有就导入到本机配置（密钥重新 DPAPI 加密）。
    /// 导出文件不在这里删除 —— 要先由界面自动测试连接，测通了才删（避免明文密钥留在磁盘上）。
    /// </summary>
    private static void ApplyImportedSettings()
    {
        var import = SettingsExchange.Import();
        if (import.Imported && import.Settings is { } settings)
        {
            try
            {
                SettingsStore.Save(settings);
                AutoVerifyPending = true;
                ImportNotice = $"已从同目录的 {SettingsExchange.FileName} 导入配置（{settings.Bucket} · {settings.NormalizedEndpoint}），正在验证连接…";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ImportNotice = $"已读到同目录配置，但写入本机配置失败：{ex.Message}";
                ImportNoticeIsError = true;
            }

            return;
        }

        if (import.Error is { } error)
        {
            ImportNotice = $"导入同目录配置失败：{error}";
            ImportNoticeIsError = true;
        }
    }

    private static IReadOnlyList<string> ParseFiles(string[] args)
    {
        var files = new List<string>();
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg) || arg.StartsWith('-'))
            {
                continue;
            }

            var path = arg.Trim().Trim('"');
            if (File.Exists(path))
            {
                files.Add(path);
            }
        }

        return files;
    }
}
