using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileUploader.Core;

/// <summary>
/// 配置的导出 / 导入。
/// <para>
/// 导出的文件与 exe 放在同一目录、文件名固定为 <see cref="FileName"/>，内容是
/// AES-GCM 加密后的二进制（不是可读文本）；新程序启动时如果发现它，会自动导入并写入
/// 本机的普通配置（密钥重新用 DPAPI 加密）。
/// </para>
/// <para>
/// 导入成功不会立即删除文件：由界面先自动测试连接，测通了才删除，避免明文长期留在磁盘上。
/// </para>
/// </summary>
public static class SettingsExchange
{
    /// <summary>导出文件名（二进制加密，放在 exe 同目录）。</summary>
    public const string FileName = "FileUploader.export.bin";

    private const int CurrentSchema = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>默认导出路径：exe 同目录的二进制文件。</summary>
    public static string DefaultPath => Path.Combine(ExecutableDirectory, FileName);

    /// <summary>exe 同目录是否存在待导入的导出文件。</summary>
    public static bool ImportFileExists => File.Exists(DefaultPath);

    private static string ExecutableDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>把配置导出成可跨机器使用的二进制文件。</summary>
    public static ExportResult Export(AppSettings settings, string? path = null)
    {
        var target = path ?? DefaultPath;
        try
        {
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(ToDto(settings), JsonOptions);
            var payload = PortableConfigCodec.Encrypt(Encoding.UTF8.GetBytes(json));

            var temp = target + ".tmp";
            File.WriteAllBytes(temp, payload);
            File.Move(temp, target, overwrite: true);
            return new ExportResult(true, target, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new ExportResult(false, target, ex.Message);
        }
    }

    /// <summary>
    /// 读取 exe 同目录（或指定路径）的导出文件。
    /// 文件不存在时返回 <c>Imported = false</c> 且无错误。
    /// </summary>
    public static ImportResult Import(string? path = null) => ImportFile(path ?? DefaultPath);

    /// <summary>导入成功后删除导出文件。</summary>
    public static void DeleteImportFile(string? path = null) => DeleteFile(path ?? DefaultPath);

    private static ImportResult ImportFile(string source)
    {
        try
        {
            if (!File.Exists(source))
            {
                return new ImportResult(false, null, null);
            }

            var bytes = File.ReadAllBytes(source);
            if (!PortableConfigCodec.LooksLikePortableConfig(bytes))
            {
                return new ImportResult(false, null, "不是本程序导出的配置文件");
            }

            var plain = PortableConfigCodec.TryDecrypt(bytes);
            if (plain is null)
            {
                return new ImportResult(false, null, "配置解密失败（文件损坏或版本不匹配）");
            }

            var json = Encoding.UTF8.GetString(plain);
            var dto = JsonSerializer.Deserialize<ExportDto>(json, JsonOptions);
            if (dto is null)
            {
                return new ImportResult(false, null, "配置文件为空");
            }

            if (dto.Schema is < 1 or > CurrentSchema)
            {
                return new ImportResult(false, null, $"不支持的配置版本：{dto.Schema}");
            }

            var settings = dto.ToSettings();
            var missing = settings.Validate();
            if (missing.Count > 0)
            {
                return new ImportResult(false, null, "缺少必填项：" + string.Join("、", missing));
            }

            return new ImportResult(true, settings, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException)
        {
            return new ImportResult(false, null, ex.Message);
        }
    }

    private static void DeleteFile(string path)
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
            // 删除失败不影响使用，下次启动会再导入一次同样的内容。
        }
    }

    private static ExportDto ToDto(AppSettings settings) => new()
    {
        Schema = CurrentSchema,
        ExportedAt = DateTimeOffset.Now,
        Protocol = (int)settings.Protocol,
        Endpoint = settings.Endpoint,
        Bucket = settings.Bucket,
        AccessKeyId = settings.AccessKeyId,
        AccessKeySecret = settings.AccessKeySecret,
        Region = settings.Region,
        KeyPrefix = settings.KeyPrefix,
        PublicBaseUrl = settings.PublicBaseUrl,
        UseDateFolder = settings.UseDateFolder,
        PathStyle = settings.PathStyle,
        UseHttps = settings.UseHttps,
        UrlMode = (int)settings.UrlMode,
        SignedUrlExpireDays = settings.SignedUrlExpireDays,
        PublicReadObject = settings.PublicReadObject,
        AutoCopyUrl = settings.AutoCopyUrl,
        FlashDropEnabled = settings.FlashDropEnabled,
        StartSilently = settings.StartSilently,
    };

    private sealed class ExportDto
    {
        public int Schema { get; set; } = CurrentSchema;

        public DateTimeOffset ExportedAt { get; set; }

        public int Protocol { get; set; }

        public string Endpoint { get; set; } = "";

        public string Bucket { get; set; } = "";

        public string AccessKeyId { get; set; } = "";

        public string AccessKeySecret { get; set; } = "";

        public string Region { get; set; } = "us-east-1";

        public string KeyPrefix { get; set; } = "";

        public string PublicBaseUrl { get; set; } = "";

        public bool UseDateFolder { get; set; } = true;

        public bool PathStyle { get; set; }

        public bool UseHttps { get; set; } = true;

        public int UrlMode { get; set; }

        public int SignedUrlExpireDays { get; set; } = 7;

        public bool PublicReadObject { get; set; }

        public bool AutoCopyUrl { get; set; } = true;

        /// <summary>右下角拖拽浮现上传气泡（旧导出文件没有这个字段时按开启处理）。</summary>
        public bool FlashDropEnabled { get; set; } = true;

        /// <summary>静默启动：不弹主窗口、只留托盘（旧导出文件里没有这个字段时按关闭处理）。</summary>
        public bool StartSilently { get; set; }

        public AppSettings ToSettings() => new()
        {
            Protocol = Protocol == (int)StorageProtocol.S3Compatible ? StorageProtocol.S3Compatible : StorageProtocol.AliyunOss,
            Endpoint = Endpoint,
            Bucket = Bucket,
            AccessKeyId = AccessKeyId,
            AccessKeySecret = AccessKeySecret,
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
            FlashDropEnabled = FlashDropEnabled,
            StartSilently = StartSilently,
        };
    }
}

/// <summary>导出结果。</summary>
public sealed record ExportResult(bool Success, string Path, string? Error);

/// <summary>导入结果：<c>Imported</c> 为 false 且 <c>Error</c> 为 null 表示没有导出文件。</summary>
public sealed record ImportResult(bool Imported, AppSettings? Settings, string? Error);
