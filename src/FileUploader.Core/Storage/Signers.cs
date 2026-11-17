using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FileUploader.Core.Storage;

/// <summary>阿里云 OSS 原生 V1 签名。</summary>
public static class OssV1Signer
{
    /// <summary>
    /// 组装待签名字符串：
    /// <c>VERB\nContent-MD5\nContent-Type\nDate\nCanonicalizedOSSHeaders + CanonicalizedResource</c>。
    /// </summary>
    /// <param name="method">HTTP 方法，例如 <c>PUT</c>。</param>
    /// <param name="contentMd5">Content-MD5（可为空字符串）。</param>
    /// <param name="contentType">Content-Type（可为空字符串）。</param>
    /// <param name="date">RFC 1123 格式的 GMT 时间。</param>
    /// <param name="ossHeaders">参与签名的 <c>x-oss-*</c> 头。</param>
    /// <param name="canonicalizedResource"><c>/bucket/object</c> 形式（不带主机）。</param>
    public static string BuildStringToSign(
        string method,
        string contentMd5,
        string contentType,
        string date,
        IEnumerable<KeyValuePair<string, string>>? ossHeaders,
        string canonicalizedResource)
    {
        var builder = new StringBuilder();
        builder.Append(method.ToUpperInvariant()).Append('\n');
        builder.Append(contentMd5).Append('\n');
        builder.Append(contentType).Append('\n');
        builder.Append(date).Append('\n');

        if (ossHeaders is not null)
        {
            foreach (var header in ossHeaders
                         .Select(h => new KeyValuePair<string, string>(h.Key.ToLowerInvariant(), h.Value.Trim()))
                         .OrderBy(h => h.Key, StringComparer.Ordinal))
            {
                builder.Append(header.Key).Append(':').Append(header.Value).Append('\n');
            }
        }

        builder.Append(canonicalizedResource);
        return builder.ToString();
    }

    /// <summary>计算 <c>Authorization: OSS {AccessKeyId}:{Signature}</c>。</summary>
    public static string Sign(string stringToSign, string accessKeyId, string accessKeySecret)
    {
        var key = Encoding.UTF8.GetBytes(accessKeySecret);
        var data = Encoding.UTF8.GetBytes(stringToSign);
        var hash = HMACSHA1.HashData(key, data);
        return $"OSS {accessKeyId}:{Convert.ToBase64String(hash)}";
    }

    /// <summary>生成 OSS 要求的日期头格式（GMT）。</summary>
    public static string FormatDate(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("r", CultureInfo.InvariantCulture);

    /// <summary>
    /// 生成 OSS V1 预签名 GET 链接（私有桶可直接下载）。
    /// 待签名串为 <c>GET\n\n\n{Expires}\n/bucket/object</c>。
    /// </summary>
    public static string BuildPresignedUrl(
        Uri objectUri,
        string canonicalizedResource,
        string accessKeyId,
        string accessKeySecret,
        DateTimeOffset expiresAt)
    {
        var expires = expiresAt.ToUnixTimeSeconds();
        var stringToSign = $"GET\n\n\n{expires}\n{canonicalizedResource}";
        var signature = Convert.ToBase64String(
            System.Security.Cryptography.HMACSHA1.HashData(
                Encoding.UTF8.GetBytes(accessKeySecret),
                Encoding.UTF8.GetBytes(stringToSign)));

        var query = $"OSSAccessKeyId={ObjectKey.EncodeComponent(accessKeyId)}" +
                    $"&Expires={expires}" +
                    $"&Signature={ObjectKey.EncodeComponent(signature)}";

        return $"{objectUri.GetLeftPart(UriPartial.Path)}?{query}";
    }
}

/// <summary>AWS SigV4 签名（S3 兼容服务使用）。</summary>
public static class S3SigV4Signer
{
    /// <summary>空请求体的 SHA-256。</summary>
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>
    /// 计算 SigV4 的 Authorization 头。
    /// </summary>
    /// <param name="method">HTTP 方法。</param>
    /// <param name="canonicalUri">已按 RFC 3986 编码的绝对路径（保留分隔斜杠）。</param>
    /// <param name="canonicalQuery">已排序并编码的查询串，无参数时为空字符串。</param>
    /// <param name="headers">参与签名的头，必须包含 <c>host</c> 与 <c>x-amz-date</c>。</param>
    /// <param name="payloadHash">请求体 SHA-256 的十六进制小写。</param>
    /// <param name="region">区域，例如 <c>us-east-1</c>。</param>
    /// <param name="service">服务名，S3 固定为 <c>s3</c>。</param>
    /// <param name="amzDate">输出的 <c>x-amz-date</c> 值。</param>
    /// <param name="signedHeaders">输出的 SignedHeaders 列表（分号分隔）。</param>
    public static string ComputeAuthorization(
        string method,
        string canonicalUri,
        string canonicalQuery,
        IEnumerable<KeyValuePair<string, string>> headers,
        string payloadHash,
        string region,
        string service,
        string accessKeyId,
        string accessKeySecret,
        DateTimeOffset timestamp,
        out string amzDate,
        out string signedHeaders)
    {
        var utc = timestamp.ToUniversalTime();
        amzDate = utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var ordered = headers
            .Select(h => new KeyValuePair<string, string>(h.Key.ToLowerInvariant(), CollapseWhitespace(h.Value)))
            .OrderBy(h => h.Key, StringComparer.Ordinal)
            .ToArray();

        var canonicalHeaders = new StringBuilder();
        foreach (var header in ordered)
        {
            canonicalHeaders.Append(header.Key).Append(':').Append(header.Value).Append('\n');
        }

        signedHeaders = string.Join(';', ordered.Select(h => h.Key));

        var canonicalRequest = string.Join(
            '\n',
            method.ToUpperInvariant(),
            canonicalUri,
            canonicalQuery,
            canonicalHeaders.ToString(),
            signedHeaders,
            payloadHash);

        var scope = $"{dateStamp}/{region}/{service}/aws4_request";
        var stringToSign = string.Join(
            '\n',
            "AWS4-HMAC-SHA256",
            amzDate,
            scope,
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var signingKey = DeriveSigningKey(accessKeySecret, dateStamp, region, service);
        var signature = Hex(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign)));

        return $"AWS4-HMAC-SHA256 Credential={accessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
    }

    /// <summary>派生 SigV4 签名密钥。</summary>
    public static byte[] DeriveSigningKey(string accessKeySecret, string dateStamp, string region, string service)
    {
        var kDate = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + accessKeySecret), Encoding.UTF8.GetBytes(dateStamp));
        var kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(region));
        var kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes(service));
        return HMACSHA256.HashData(kService, Encoding.UTF8.GetBytes("aws4_request"));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>
    /// 生成 SigV4 预签名 GET 链接（私有桶可直接下载）。
    /// SigV4 预签名有效期上限 7 天（604800 秒），超出会自动收敛。
    /// </summary>
    public static string BuildPresignedUrl(
        Uri objectUri,
        string accessKeyId,
        string accessKeySecret,
        string region,
        TimeSpan lifetime,
        DateTimeOffset timestamp)
    {
        var expiresSeconds = (long)Math.Clamp(lifetime.TotalSeconds, 1, 604800);
        var utc = timestamp.ToUniversalTime();
        var amzDate = utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var scope = $"{dateStamp}/{region}/s3/aws4_request";

        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256",
            ["X-Amz-Credential"] = $"{accessKeyId}/{scope}",
            ["X-Amz-Date"] = amzDate,
            ["X-Amz-Expires"] = expiresSeconds.ToString(CultureInfo.InvariantCulture),
            ["X-Amz-SignedHeaders"] = "host",
        };

        var canonicalQuery = string.Join(
            '&',
            parameters.Select(p => $"{ObjectKey.EncodeComponent(p.Key)}={ObjectKey.EncodeComponent(p.Value)}"));

        var canonicalUri = objectUri.AbsolutePath.Length == 0 ? "/" : objectUri.AbsolutePath;
        var canonicalRequest = string.Join(
            '\n',
            "GET",
            canonicalUri,
            canonicalQuery,
            $"host:{objectUri.Authority}",
            string.Empty,
            "host",
            "UNSIGNED-PAYLOAD");

        var stringToSign = string.Join(
            '\n',
            "AWS4-HMAC-SHA256",
            amzDate,
            scope,
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var signingKey = DeriveSigningKey(accessKeySecret, dateStamp, region, "s3");
        var signature = Hex(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign)));

        return $"{objectUri.GetLeftPart(UriPartial.Path)}?{canonicalQuery}&X-Amz-Signature={signature}";
    }

    private static string CollapseWhitespace(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.Contains("  ", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var builder = new StringBuilder(trimmed.Length);
        var lastWasSpace = false;
        foreach (var ch in trimmed)
        {
            var isSpace = ch == ' ' || ch == '\t';
            if (isSpace)
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                }
            }
            else
            {
                builder.Append(ch);
            }

            lastWasSpace = isSpace;
        }

        return builder.ToString();
    }
}
