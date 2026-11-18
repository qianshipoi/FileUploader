namespace FileUploader.Core;

/// <summary>
/// 目标存储使用的签名协议。
/// </summary>
public enum StorageProtocol
{
    /// <summary>阿里云 OSS 原生 V1 签名（Authorization: OSS AK:签名）。</summary>
    AliyunOss = 0,

    /// <summary>AWS S3 兼容的 SigV4 签名（MinIO / RustFS / 其他 S3 网关）。</summary>
    S3Compatible = 1,
}

/// <summary>生成的链接类型。</summary>
public enum UrlMode
{
    /// <summary>预签名链接：私有桶也能直接下载，到期后失效。</summary>
    Signed = 0,

    /// <summary>直链：要求桶或对象是公共读。</summary>
    Direct = 1,
}

/// <summary>
/// 应用配置。不可变记录，UI 侧通过 <c>with</c> 表达式产生新实例。
/// </summary>
public sealed record AppSettings
{
    public StorageProtocol Protocol { get; init; } = StorageProtocol.AliyunOss;

    /// <summary>OSS Endpoint，例如 <c>oss-cn-hangzhou.aliyuncs.com</c>。可带 https:// 前缀。</summary>
    public string Endpoint { get; init; } = "oss-cn-hangzhou.aliyuncs.com";

    public string Bucket { get; init; } = "";

    public string AccessKeyId { get; init; } = "";

    public string AccessKeySecret { get; init; } = "";

    /// <summary>SigV4 签名区域（阿里云 OSS V1 签名不使用）。</summary>
    public string Region { get; init; } = "us-east-1";

    /// <summary>对象键前缀，例如 <c>images</c>；首尾斜杠会被自动整理。</summary>
    public string KeyPrefix { get; init; } = "";

    /// <summary>可选的自定义/加速域名，仅影响生成的访问链接。</summary>
    public string PublicBaseUrl { get; init; } = "";

    /// <summary>对象键中插入 yyyy/MM/dd 目录。</summary>
    public bool UseDateFolder { get; init; } = true;

    /// <summary>路径风格访问：endpoint/bucket/object（S3 兼容服务通常需要）。</summary>
    public bool PathStyle { get; init; }

    public bool UseHttps { get; init; } = true;

    /// <summary>链接类型：默认预签名（私有桶可直接下载）。</summary>
    public UrlMode UrlMode { get; init; } = UrlMode.Signed;

    /// <summary>预签名链接的有效期（天）。S3 SigV4 上限 7 天，超出会自动收敛到 7。</summary>
    public int SignedUrlExpireDays { get; init; } = 7;

    /// <summary>上传时把对象 ACL 设置为 public-read（仅直链模式需要）。</summary>
    public bool PublicReadObject { get; init; }

    /// <summary>上传成功后自动把链接写入剪贴板。</summary>
    public bool AutoCopyUrl { get; init; } = true;

    /// <summary>
    /// 屏幕右下角的「拖拽浮现」上传气泡：一开始拖文件就浮起来，拖上去松手即上传。
    /// 关掉后不再创建那个隐形窗口，也不会装拖拽探测器。
    /// </summary>
    public bool FlashDropEnabled { get; init; } = true;

    /// <summary>
    /// 静默启动：启动时不弹出主窗口，只在托盘里待命（点托盘图标随时叫回来）。
    /// 托盘图标建不出来、还没配置存储、或命令行带了要上传的文件时仍会显示主窗口，
    /// 否则用户会以为程序根本没启动。
    /// </summary>
    public bool StartSilently { get; init; }

    /// <summary>去掉协议前缀与结尾斜杠后的 Endpoint。</summary>
    public string NormalizedEndpoint
    {
        get
        {
            var value = (Endpoint ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                return string.Empty;
            }

            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                value = value[7..];
            }
            else if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                value = value[8..];
            }

            return value.Trim().TrimEnd('/');
        }
    }

    /// <summary>返回缺失的必填项（用于校验提示）。</summary>
    public IReadOnlyList<string> Validate()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(NormalizedEndpoint))
        {
            missing.Add("Endpoint");
        }

        if (string.IsNullOrWhiteSpace(Bucket))
        {
            missing.Add("桶名");
        }

        if (string.IsNullOrWhiteSpace(AccessKeyId))
        {
            missing.Add("AccessKey ID");
        }

        if (string.IsNullOrWhiteSpace(AccessKeySecret))
        {
            missing.Add("AccessKey Secret");
        }

        return missing;
    }

    public bool IsConfigured => Validate().Count == 0;
}
