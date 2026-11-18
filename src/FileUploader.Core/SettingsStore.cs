using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileUploader.Core;

/// <summary>
/// 把 <see cref="AppSettings"/> 读写到 <c>%APPDATA%\FileUploader\settings.json</c>。
/// AccessKeySecret 使用 DPAPI 加密后以 <c>secret</c> 字段保存。
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly object Gate = new();

    /// <summary>配置文件完整路径（便携模式在 exe 旁的 data 目录）。</summary>
    public static string FilePath => AppPaths.SettingsPath;

    /// <summary>读取配置；文件不存在或损坏时返回默认配置。</summary>
    /// <param name="warning">读取过程中的非致命问题说明。</param>
    public static AppSettings Load(out string? warning) => LoadFrom(FilePath, out warning);

    /// <summary>从指定路径读取配置（便于测试）。</summary>
    public static AppSettings LoadFrom(string path, out string? warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<SettingsDto>(json, JsonOptions);
            if (dto is null)
            {
                warning = "配置文件为空，已使用默认配置。";
                return new AppSettings();
            }

            var secret = Dpapi.Unprotect(dto.Secret);
            if (secret is null)
            {
                warning = "密钥无法用当前 Windows 账户解密，请重新填写 AccessKey Secret。";
                secret = string.Empty;
            }

            return dto.ToSettings(secret);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            warning = $"读取配置失败：{ex.Message}";
            return new AppSettings();
        }
    }

    /// <summary>读取配置（不需要诊断信息时使用）。</summary>
    public static AppSettings Load() => Load(out _);

    /// <summary>写入配置。</summary>
    public static void Save(AppSettings settings) => SaveTo(FilePath, settings);

    /// <summary>写入指定路径（便于测试）。</summary>
    public static void SaveTo(string path, AppSettings settings)
    {
        lock (Gate)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var dto = SettingsDto.FromSettings(settings);
            var json = JsonSerializer.Serialize(dto, JsonOptions);

            // 先写临时文件再替换，避免写一半留下损坏的配置。
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
    }

    private sealed class SettingsDto
    {
        public int Protocol { get; set; }

        public string Endpoint { get; set; } = "";

        public string Bucket { get; set; } = "";

        public string AccessKeyId { get; set; } = "";

        /// <summary>DPAPI 加密后的 AccessKeySecret（Base64）。</summary>
        public string Secret { get; set; } = "";

        public string Region { get; set; } = "us-east-1";

        public string KeyPrefix { get; set; } = "";

        public string PublicBaseUrl { get; set; } = "";

        public bool UseDateFolder { get; set; } = true;

        public bool PathStyle { get; set; }

        public bool UseHttps { get; set; } = true;

        /// <summary>链接类型：0 = 预签名，1 = 直链。</summary>
        public int UrlMode { get; set; }

        public int SignedUrlExpireDays { get; set; } = 7;

        public bool PublicReadObject { get; set; }

        public bool AutoCopyUrl { get; set; } = true;

        /// <summary>多个文件打包成 ZIP 上传（旧配置文件里没有这个字段时按关闭处理）。</summary>
        public bool ZipBatchUpload { get; set; }

        /// <summary>右下角拖拽浮现上传气泡（旧配置文件里没有这个字段时按开启处理）。</summary>
        public bool FlashDropEnabled { get; set; } = true;

        /// <summary>静默启动：不弹主窗口、只留托盘（旧配置文件里没有这个字段时按关闭处理）。</summary>
        public bool StartSilently { get; set; }

        /// <summary>开机自启：写当前用户 Run 启动项（旧配置文件里没有这个字段时按关闭处理）。</summary>
        public bool StartWithWindows { get; set; }

        public static SettingsDto FromSettings(AppSettings s) => new()
        {
            Protocol = (int)s.Protocol,
            Endpoint = s.Endpoint,
            Bucket = s.Bucket,
            AccessKeyId = s.AccessKeyId,
            Secret = Dpapi.Protect(s.AccessKeySecret) ?? string.Empty,
            Region = s.Region,
            KeyPrefix = s.KeyPrefix,
            PublicBaseUrl = s.PublicBaseUrl,
            UseDateFolder = s.UseDateFolder,
            PathStyle = s.PathStyle,
            UseHttps = s.UseHttps,
            UrlMode = (int)s.UrlMode,
            SignedUrlExpireDays = s.SignedUrlExpireDays,
            PublicReadObject = s.PublicReadObject,
            AutoCopyUrl = s.AutoCopyUrl,
            ZipBatchUpload = s.ZipBatchUpload,
            FlashDropEnabled = s.FlashDropEnabled,
            StartSilently = s.StartSilently,
            StartWithWindows = s.StartWithWindows,
        };

        public AppSettings ToSettings(string secret) => new()
        {
            Protocol = Protocol == (int)StorageProtocol.S3Compatible ? StorageProtocol.S3Compatible : StorageProtocol.AliyunOss,
            Endpoint = Endpoint,
            Bucket = Bucket,
            AccessKeyId = AccessKeyId,
            AccessKeySecret = secret,
            Region = string.IsNullOrWhiteSpace(Region) ? "us-east-1" : Region,
            KeyPrefix = KeyPrefix,
            PublicBaseUrl = PublicBaseUrl,
            UseDateFolder = UseDateFolder,
            PathStyle = PathStyle,
            UseHttps = UseHttps,
            UrlMode = UrlMode == (int)FileUploader.Core.UrlMode.Direct ? FileUploader.Core.UrlMode.Direct : FileUploader.Core.UrlMode.Signed,
            SignedUrlExpireDays = SignedUrlExpireDays <= 0 ? 7 : SignedUrlExpireDays,
            PublicReadObject = PublicReadObject,
            AutoCopyUrl = AutoCopyUrl,
            ZipBatchUpload = ZipBatchUpload,
            FlashDropEnabled = FlashDropEnabled,
            StartSilently = StartSilently,
            StartWithWindows = StartWithWindows,
        };
    }
}
