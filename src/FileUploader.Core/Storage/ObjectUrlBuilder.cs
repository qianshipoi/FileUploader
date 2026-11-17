namespace FileUploader.Core.Storage;

/// <summary>
/// 对象访问地址的构造：直链与预签名链接。
/// </summary>
public static class ObjectUrlBuilder
{
    /// <summary>签名有效期上限（天）：阿里云与 S3 都按 7 天封顶。</summary>
    public const int MaxExpireDays = 7;

    /// <summary>SigV4 预签名链接的有效期上限（AWS 规定 7 天）。</summary>
    public const int MaxSigV4ExpireDays = MaxExpireDays;

    /// <summary>默认有效期（天）。</summary>
    public const int DefaultExpireDays = 7;

    /// <summary>构造对象请求地址：路径风格或虚拟主机风格。</summary>
    public static (Uri ObjectUri, string CanonicalResource) BuildObjectTarget(AppSettings settings, string key)
    {
        var scheme = settings.UseHttps ? "https" : "http";
        var bucket = settings.Bucket.Trim();
        var endpoint = settings.NormalizedEndpoint;
        var encodedKey = ObjectKey.EncodePath(key);
        var encodedBucket = ObjectKey.EncodeComponent(bucket);

        return settings.PathStyle
            ? (new Uri($"{scheme}://{endpoint}/{encodedBucket}/{encodedKey}"), $"/{bucket}/{key}")
            : (new Uri($"{scheme}://{bucket}.{endpoint}/{encodedKey}"), $"/{bucket}/{key}");
    }

    /// <summary>实际生效的签名有效期（天）：统一按 7 天封顶。</summary>
    public static int EffectiveExpireDays(AppSettings settings)
    {
        var days = settings.SignedUrlExpireDays <= 0 ? DefaultExpireDays : settings.SignedUrlExpireDays;
        return Math.Clamp(days, 1, MaxExpireDays);
    }

    /// <summary>直链（要求桶或对象公共读）。设置里配了自定义域名时优先用它。</summary>
    public static string DirectUrl(AppSettings settings, string key)
    {
        var baseUrl = (settings.PublicBaseUrl ?? string.Empty).Trim();
        if (baseUrl.Length > 0)
        {
            if (!baseUrl.Contains("://", StringComparison.Ordinal))
            {
                baseUrl = (settings.UseHttps ? "https" : "http") + "://" + baseUrl.TrimStart('/');
            }

            return baseUrl.TrimEnd('/') + "/" + ObjectKey.EncodePath(key);
        }

        var (uri, _) = BuildObjectTarget(settings, key);
        return uri.ToString();
    }

    /// <summary>
    /// 预签名 GET 链接：私有桶也能直接下载，到期后失效。
    /// 自定义域名不参与签名（CDN 需要自己的鉴权），这里始终用桶的 endpoint。
    /// </summary>
    public static string SignedUrl(AppSettings settings, string key, DateTimeOffset now)
    {
        var (uri, canonicalResource) = BuildObjectTarget(settings, key);
        var days = EffectiveExpireDays(settings);
        var expiresAt = now.AddDays(days);

        return settings.Protocol == StorageProtocol.AliyunOss
            ? OssV1Signer.BuildPresignedUrl(uri, canonicalResource, settings.AccessKeyId.Trim(), settings.AccessKeySecret, expiresAt)
            : S3SigV4Signer.BuildPresignedUrl(
                uri,
                settings.AccessKeyId.Trim(),
                settings.AccessKeySecret,
                string.IsNullOrWhiteSpace(settings.Region) ? "us-east-1" : settings.Region.Trim(),
                TimeSpan.FromDays(days),
                now);
    }

    /// <summary>按配置的链接类型生成访问地址。</summary>
    public static string Build(AppSettings settings, string key, DateTimeOffset now) =>
        settings.UrlMode == UrlMode.Signed
            ? SignedUrl(settings, key, now)
            : DirectUrl(settings, key);
}
