using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace FileUploader.Core.Storage;

/// <summary>一次请求的结果。</summary>
public sealed record StorageResult(bool Success, int StatusCode, string? ErrorCode, string? ErrorMessage, string? Url = null)
{
    public static StorageResult Ok(int statusCode, string? url = null) => new(true, statusCode, null, null, url);

    public static StorageResult Fail(int statusCode, string? errorCode, string? errorMessage) =>
        new(false, statusCode, errorCode, errorMessage);

    /// <summary>面向用户的一行说明。</summary>
    public string Describe()
    {
        if (Success)
        {
            return $"成功（HTTP {StatusCode}）";
        }

        var code = string.IsNullOrWhiteSpace(ErrorCode) ? $"HTTP {StatusCode}" : ErrorCode;
        return string.IsNullOrWhiteSpace(ErrorMessage) ? code : $"{code}: {ErrorMessage}";
    }
}

/// <summary>
/// 请求上的一个查询参数。
/// <para>
/// <see cref="IsSubResource"/> 表示它要参与 OSS V1 的 CanonicalizedResource：
/// OSS 只把"子资源"（<c>uploads</c> / <c>partNumber</c> / <c>uploadId</c> 这类）算进待签名字符串，
/// 普通查询参数（例如 ListParts 的 <c>max-parts</c>）不算 —— 算多了签名对不上，服务端会回 403。
/// </para>
/// </summary>
internal readonly record struct QueryParam(string Name, string? Value, bool IsSubResource)
{
    public static QueryParam SubResource(string name, string? value = null) => new(name, value, true);
}

/// <summary>
/// 极简对象存储客户端：单次 PUT Object 上传 + 分片上传（Initiate / UploadPart / Complete / Abort），
/// 签名支持阿里云 OSS V1 与 AWS SigV4。
/// </summary>
public sealed class ObjectStorageClient
{
    private static readonly Regex XmlErrorRegex = new(
        "<(Code|Message)>(.*?)</\\1>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly AppSettings _settings;

    public ObjectStorageClient(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        if (string.IsNullOrWhiteSpace(settings.NormalizedEndpoint))
        {
            throw new InvalidOperationException("Endpoint 未配置。");
        }

        if (string.IsNullOrWhiteSpace(settings.Bucket))
        {
            throw new InvalidOperationException("桶名未配置。");
        }
    }

    private string Scheme => _settings.UseHttps ? "https" : "http";

    private string Bucket => _settings.Bucket.Trim();

    /// <summary>对象的直链地址（优先使用自定义域名，不含签名）。</summary>
    public string BuildObjectUrl(string key) => ObjectUrlBuilder.DirectUrl(_settings, key);

    /// <summary>上传本地文件到指定对象键。</summary>
    public async Task<StorageResult> PutObjectAsync(
        string localPath,
        string key,
        string contentType,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        FileInfo info = new(localPath);
        if (!info.Exists)
        {
            return StorageResult.Fail(0, "FileNotFound", $"本地文件不存在：{localPath}");
        }

        var (uri, canonicalResource) = BuildObjectRequestUri(key);
        var payloadHash = _settings.Protocol == StorageProtocol.S3Compatible
            ? await ComputeSha256HexAsync(localPath, cancellationToken).ConfigureAwait(false)
            : string.Empty;

        await using var stream = new FileStream(
            localPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 128,
            useAsync: true);

        using var request = new HttpRequestMessage(HttpMethod.Put, uri);
        request.Content = new ProgressStreamContent(stream, info.Length, progress);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        request.Content.Headers.ContentLength = info.Length;

        var extraHeaders = BuildAclHeaders();

        Sign(request, HttpMethod.Put.Method, uri, canonicalResource, contentType, payloadHash, extraHeaders);

        try
        {
            using var response = await StorageHttp.Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return StorageResult.Ok((int)response.StatusCode, BuildObjectUrl(key));
            }

            var (code, message) = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            return StorageResult.Fail((int)response.StatusCode, code, message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return StorageResult.Fail(0, "NetworkError", ex.Message);
        }
    }

    /// <summary>
    /// 初始化分片上传（POST <c>?uploads</c>），返回 UploadId。
    /// <para>
    /// Content-Type 在这里就定下来了（服务端记在对象上），后面传分片时不再单独指定。
    /// </para>
    /// </summary>
    public async Task<(StorageResult Result, string? UploadId)> InitiateMultipartUploadAsync(
        string key,
        string contentType,
        CancellationToken cancellationToken)
    {
        var query = new[] { QueryParam.SubResource("uploads") };
        var (baseUri, canonicalResource) = BuildObjectRequestUri(key);
        var uri = AppendQuery(baseUri, query);

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent([]),
        };
        request.Content.Headers.ContentLength = 0;
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        Sign(
            request,
            HttpMethod.Post.Method,
            uri,
            canonicalResource,
            contentType,
            EmptyPayloadHash(),
            BuildAclHeaders(),
            query);

        try
        {
            using var response = await StorageHttp.Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var uploadId = ReadXmlValue(body, "UploadId");
                if (string.IsNullOrEmpty(uploadId))
                {
                    return (StorageResult.Fail((int)response.StatusCode, "NoUploadId", "初始化分片上传的响应里没有 UploadId。"), null);
                }

                return (StorageResult.Ok((int)response.StatusCode), uploadId);
            }

            var (code, message) = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            return (StorageResult.Fail((int)response.StatusCode, code, message), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return (StorageResult.Fail(0, "NetworkError", ex.Message), null);
        }
    }

    /// <summary>
    /// 上传一个分片（PUT <c>?partNumber=N&amp;uploadId=...</c>）。
    /// 返回的 ETag 必须在 CompleteMultipartUpload 里原样回填，所以这里单独带出来。
    /// </summary>
    public async Task<(StorageResult Result, string? ETag)> UploadPartAsync(
        string key,
        string uploadId,
        int partNumber,
        string localPath,
        long offset,
        long length,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        if (length <= 0)
        {
            return (StorageResult.Fail(0, "EmptyPart", "分片长度为 0。"), null);
        }

        var query = new[]
        {
            QueryParam.SubResource("partNumber", partNumber.ToString(CultureInfo.InvariantCulture)),
            QueryParam.SubResource("uploadId", uploadId),
        };
        var (baseUri, canonicalResource) = BuildObjectRequestUri(key);
        var uri = AppendQuery(baseUri, query);

        // SigV4 要求 x-amz-content-sha256 是这一片内容的真实哈希，所以先流式算一遍再发一遍
        // （不用 UNSIGNED-PAYLOAD：各家 S3 网关对它的支持不一致，宁可多读一次本地文件）。
        var payloadHash = _settings.Protocol == StorageProtocol.S3Compatible
            ? await ComputeSha256HexAsync(localPath, offset, length, cancellationToken).ConfigureAwait(false)
            : string.Empty;

        await using var stream = new FileStream(
            localPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 128,
            useAsync: true);

        if (stream.Length < offset + length)
        {
            // 读哈希和发出去之间文件被截短了：这一片发出去必然对不上，直接报错。
            return (StorageResult.Fail(0, "FileChanged", "本地文件在上传过程中变短了，请重新上传。"), null);
        }

        stream.Seek(offset, SeekOrigin.Begin);

        using var request = new HttpRequestMessage(HttpMethod.Put, uri);
        request.Content = new ProgressStreamContent(stream, length, progress);
        request.Content.Headers.ContentLength = length;

        Sign(request, HttpMethod.Put.Method, uri, canonicalResource, string.Empty, payloadHash, null, query);

        try
        {
            using var response = await StorageHttp.Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var etag = response.Headers.ETag?.ToString();
                if (string.IsNullOrWhiteSpace(etag))
                {
                    return (StorageResult.Fail((int)response.StatusCode, "NoETag", $"第 {partNumber} 片上传成功但响应里没有 ETag。"), null);
                }

                return (StorageResult.Ok((int)response.StatusCode), etag);
            }

            var (code, message) = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            return (StorageResult.Fail((int)response.StatusCode, code, message), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return (StorageResult.Fail(0, "NetworkError", ex.Message), null);
        }
    }

    /// <summary>完成分片上传（POST <c>?uploadId=...</c> + 分片清单 XML）。</summary>
    public async Task<StorageResult> CompleteMultipartUploadAsync(
        string key,
        string uploadId,
        IReadOnlyList<MultipartPart> parts,
        CancellationToken cancellationToken)
    {
        if (parts.Count == 0)
        {
            return StorageResult.Fail(0, "NoParts", "没有可提交的分片。");
        }

        var query = new[] { QueryParam.SubResource("uploadId", uploadId) };
        var (baseUri, canonicalResource) = BuildObjectRequestUri(key);
        var uri = AppendQuery(baseUri, query);

        var xml = BuildCompleteXml(parts);
        var payloadHash = _settings.Protocol == StorageProtocol.S3Compatible
            ? Convert.ToHexString(SHA256.HashData(xml)).ToLowerInvariant()
            : string.Empty;

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent(xml),
        };
        request.Content.Headers.ContentLength = xml.Length;
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/xml");

        Sign(request, HttpMethod.Post.Method, uri, canonicalResource, "application/xml", payloadHash, null, query);

        try
        {
            using var response = await StorageHttp.Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                // 坑：OSS 的 CompleteMultipartUpload 出错时也会回 200，错误放在响应体里（<Error>…）。
                // 只看状态码会把"没合并成功"当成成功，链接打开是 404。
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (body.Contains("<Error", StringComparison.OrdinalIgnoreCase))
                {
                    return StorageResult.Fail(
                        (int)response.StatusCode,
                        Match(body, "Code") ?? "CompleteFailed",
                        Match(body, "Message"));
                }

                return StorageResult.Ok((int)response.StatusCode);
            }

            var (code, message) = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            return StorageResult.Fail((int)response.StatusCode, code, message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return StorageResult.Fail(0, "NetworkError", ex.Message);
        }
    }

    /// <summary>
    /// 放弃分片上传（DELETE <c>?uploadId=...</c>）：把服务端已经收到的分片清掉。
    /// 中断后想续传就不要调它 —— 只有"这个文件不再传了"（移除/清空记录）才清。
    /// </summary>
    public async Task<StorageResult> AbortMultipartUploadAsync(string key, string uploadId, CancellationToken cancellationToken)
    {
        var query = new[] { QueryParam.SubResource("uploadId", uploadId) };
        var (baseUri, canonicalResource) = BuildObjectRequestUri(key);
        var uri = AppendQuery(baseUri, query);

        using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
        Sign(request, HttpMethod.Delete.Method, uri, canonicalResource, string.Empty, EmptyPayloadHash(), null, query);

        try
        {
            using var response = await StorageHttp.Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return StorageResult.Ok((int)response.StatusCode);
            }

            var (code, message) = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            return StorageResult.Fail((int)response.StatusCode, code, message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return StorageResult.Fail(0, "NetworkError", ex.Message);
        }
    }

    /// <summary>删除对象（DELETE Object）。</summary>
    public async Task<StorageResult> DeleteObjectAsync(string key, CancellationToken cancellationToken)
    {
        var (uri, canonicalResource) = BuildObjectRequestUri(key);
        using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
        Sign(request, HttpMethod.Delete.Method, uri, canonicalResource, string.Empty, EmptyPayloadHash());

        try
        {
            using var response = await StorageHttp.Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return StorageResult.Ok((int)response.StatusCode);
            }

            var (code, message) = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            return StorageResult.Fail((int)response.StatusCode, code, message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return StorageResult.Fail(0, "NetworkError", ex.Message);
        }
    }
    /// <summary>检查桶是否存在（HEAD Bucket）。</summary>
    public async Task<StorageResult> HeadBucketAsync(CancellationToken cancellationToken)
    {
        var (uri, canonicalResource) = BuildBucketRequestUri();
        using var request = new HttpRequestMessage(HttpMethod.Head, uri);
        Sign(request, HttpMethod.Head.Method, uri, canonicalResource, string.Empty, EmptyPayloadHash());

        try
        {
            using var response = await StorageHttp.Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return StorageResult.Ok((int)response.StatusCode);
            }

            var (code, message) = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            return StorageResult.Fail((int)response.StatusCode, code, message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return StorageResult.Fail(0, "NetworkError", ex.Message);
        }
    }

    /// <summary>创建桶（PUT Bucket）。</summary>
    public async Task<StorageResult> CreateBucketAsync(CancellationToken cancellationToken)
    {
        var (uri, canonicalResource) = BuildBucketRequestUri();
        using var request = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
        request.Content.Headers.ContentLength = 0;
        Sign(request, HttpMethod.Put.Method, uri, canonicalResource, string.Empty, EmptyPayloadHash());

        try
        {
            using var response = await StorageHttp.Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return StorageResult.Ok((int)response.StatusCode);
            }

            var (code, message) = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            return StorageResult.Fail((int)response.StatusCode, code, message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return StorageResult.Fail(0, "NetworkError", ex.Message);
        }
    }

    /// <summary>检查桶；不存在时自动创建（首次配置最省事）。</summary>
    public async Task<StorageResult> EnsureBucketAsync(CancellationToken cancellationToken)
    {
        var head = await HeadBucketAsync(cancellationToken).ConfigureAwait(false);
        if (head.Success)
        {
            return head;
        }

        if (head.StatusCode is not (404 or 403))
        {
            return head;
        }

        if (head.StatusCode == 403)
        {
            return head;
        }

        var create = await CreateBucketAsync(cancellationToken).ConfigureAwait(false);
        return create.Success ? StorageResult.Ok(create.StatusCode) : create;
    }

    private string EmptyPayloadHash() =>
        _settings.Protocol == StorageProtocol.S3Compatible ? S3SigV4Signer.EmptyPayloadHash : string.Empty;

    /// <summary>直链模式下把对象 ACL 设成公共读所需的头（分片上传在 Initiate 时带同样的头）。</summary>
    private Dictionary<string, string> BuildAclHeaders()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_settings.PublicReadObject)
        {
            // 让生成的链接可以被匿名访问（否则私有桶的链接打开是 403）。
            headers[_settings.Protocol == StorageProtocol.AliyunOss ? "x-oss-object-acl" : "x-amz-acl"] = "public-read";
        }

        return headers;
    }

    private (Uri Uri, string CanonicalResource) BuildObjectRequestUri(string key) =>
        ObjectUrlBuilder.BuildObjectTarget(_settings, key);

    private (Uri Uri, string CanonicalResource) BuildBucketRequestUri()
    {
        var endpoint = _settings.NormalizedEndpoint;
        var encodedBucket = ObjectKey.EncodeComponent(Bucket);

        return _settings.PathStyle
            ? (new Uri($"{Scheme}://{endpoint}/{encodedBucket}/"), $"/{Bucket}/")
            : (new Uri($"{Scheme}://{Bucket}.{endpoint}/"), $"/{Bucket}/");
    }

    /// <summary>把查询参数接到地址后面（值按 RFC 3986 编码；无值的参数只留名字，例如 <c>?uploads</c>）。</summary>
    internal static Uri AppendQuery(Uri baseUri, IReadOnlyList<QueryParam> query)
    {
        if (query.Count == 0)
        {
            return baseUri;
        }

        var builder = new StringBuilder(baseUri.GetLeftPart(UriPartial.Path)).Append('?');
        for (var i = 0; i < query.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('&');
            }

            builder.Append(query[i].Name);
            if (query[i].Value is not null)
            {
                builder.Append('=').Append(ObjectKey.EncodeComponent(query[i].Value!));
            }
        }

        return new Uri(builder.ToString());
    }

    /// <summary>
    /// OSS V1 的 CanonicalizedResource：<c>/bucket/key?uploadId=...</c>，
    /// 只拼子资源、按名字排序，值不编码（与 OSS 官方 SDK 一致）。
    /// </summary>
    internal static string BuildOssCanonicalResource(string canonicalResource, IReadOnlyList<QueryParam>? query)
    {
        var subResources = (query ?? [])
            .Where(q => q.IsSubResource)
            .OrderBy(q => q.Name, StringComparer.Ordinal)
            .ToArray();

        if (subResources.Length == 0)
        {
            return canonicalResource;
        }

        var builder = new StringBuilder(canonicalResource).Append('?');
        for (var i = 0; i < subResources.Length; i++)
        {
            if (i > 0)
            {
                builder.Append('&');
            }

            builder.Append(subResources[i].Name);
            if (subResources[i].Value is not null)
            {
                builder.Append('=').Append(subResources[i].Value);
            }
        }

        return builder.ToString();
    }

    /// <summary>SigV4 的 CanonicalQueryString：全部参数按名字排序、<c>key=编码后的值</c>（无值也要写成 <c>key=</c>）。</summary>
    internal static string BuildSigV4CanonicalQuery(IReadOnlyList<QueryParam>? query)
    {
        if (query is null || query.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            '&',
            query
                .Select(q => (q.Name, Value: ObjectKey.EncodeComponent(q.Value ?? string.Empty)))
                .OrderBy(q => q.Name, StringComparer.Ordinal)
                .Select(q => $"{q.Name}={q.Value}"));
    }

    private void Sign(
        HttpRequestMessage request,
        string method,
        Uri uri,
        string canonicalResource,
        string contentType,
        string payloadHash,
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        IReadOnlyList<QueryParam>? query = null)
    {
        var now = DateTimeOffset.UtcNow;
        extraHeaders ??= new Dictionary<string, string>();

        if (_settings.Protocol == StorageProtocol.AliyunOss)
        {
            var date = OssV1Signer.FormatDate(now);
            var stringToSign = OssV1Signer.BuildStringToSign(
                method,
                string.Empty,
                contentType,
                date,
                extraHeaders,
                BuildOssCanonicalResource(canonicalResource, query));
            var authorization = OssV1Signer.Sign(stringToSign, _settings.AccessKeyId.Trim(), _settings.AccessKeySecret);

            request.Headers.TryAddWithoutValidation("Date", date);
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            foreach (var header in extraHeaders)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return;
        }

        var amzDate = now.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);

        var headersToSign = new List<KeyValuePair<string, string>>
        {
            new("host", uri.Authority),
            new("x-amz-content-sha256", payloadHash),
            new("x-amz-date", amzDate),
        };

        if (!string.IsNullOrEmpty(contentType))
        {
            headersToSign.Add(new("content-type", contentType));
        }

        foreach (var header in extraHeaders)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            headersToSign.Add(new(header.Key.ToLowerInvariant(), header.Value));
        }

        var signatureHeader = S3SigV4Signer.ComputeAuthorization(
            method,
            uri.AbsolutePath.Length == 0 ? "/" : uri.AbsolutePath,
            BuildSigV4CanonicalQuery(query),
            headersToSign,
            payloadHash,
            string.IsNullOrWhiteSpace(_settings.Region) ? "us-east-1" : _settings.Region.Trim(),
            "s3",
            _settings.AccessKeyId.Trim(),
            _settings.AccessKeySecret,
            now,
            out _,
            out _);

        request.Headers.TryAddWithoutValidation("Authorization", signatureHeader);
    }

    /// <summary>算一整个文件的 SHA-256（SigV4 的 payload hash）。</summary>
    private static async Task<string> ComputeSha256HexAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 128,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>只算文件里某一段的 SHA-256（分片上传每一片的 payload hash）。</summary>
    private static async Task<string> ComputeSha256HexAsync(string path, long offset, long length, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 128,
            useAsync: true);
        stream.Seek(offset, SeekOrigin.Begin);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 128];
        long remaining = length;
        while (remaining > 0)
        {
            var wanted = (int)Math.Min(buffer.Length, remaining);
            var read = await stream.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>从 XML 响应里取一个元素的值（忽略命名空间：OSS 与各家 S3 网关的命名空间不一致）。</summary>
    private static string? ReadXmlValue(string body, string localName)
    {
        try
        {
            var document = XDocument.Parse(body);
            return document.Descendants()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
                ?.Value;
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>CompleteMultipartUpload 的请求体：分片号 + 每片的 ETag（必须与 UploadPart 返回的一字不差）。</summary>
    private static byte[] BuildCompleteXml(IReadOnlyList<MultipartPart> parts)
    {
        var builder = new StringBuilder("<CompleteMultipartUpload>");
        foreach (var part in parts.OrderBy(p => p.PartNumber))
        {
            builder.Append("<Part><PartNumber>")
                .Append(part.PartNumber.ToString(CultureInfo.InvariantCulture))
                .Append("</PartNumber><ETag>")
                .Append(EscapeXml(part.ETag))
                .Append("</ETag></Part>");
        }

        builder.Append("</CompleteMultipartUpload>");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static string EscapeXml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    private static async Task<(string? Code, string? Message)> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                return (response.StatusCode.ToString(), null);
            }

            var code = Match(body, "Code");
            var message = Match(body, "Message");
            if (code is null && message is null)
            {
                var trimmed = body.Trim();
                return (response.StatusCode.ToString(), trimmed.Length > 300 ? trimmed[..300] : trimmed);
            }

            return (code ?? response.StatusCode.ToString(), message);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            return (response.StatusCode.ToString(), ex.Message);
        }
    }

    private static string? Match(string body, string group)
    {
        foreach (Match match in XmlErrorRegex.Matches(body))
        {
            if (string.Equals(match.Groups[1].Value, group, StringComparison.OrdinalIgnoreCase))
            {
                return WebUtility.HtmlDecode(match.Groups[2].Value.Trim());
            }
        }

        return null;
    }
}

/// <summary>
/// 把上传进度作为<see cref="IProgress{T}"/>报告的请求体。
/// <para>
/// 只读 <c>length</c> 个字节就停：分片上传时底层是同一个文件的 FileStream，
/// 读到文件末尾会把后面几片的内容一起塞进这一片（Content-Length 对不上，服务端直接断连接）。
/// </para>
/// </summary>
internal sealed class ProgressStreamContent : HttpContent
{
    private readonly Stream _stream;
    private readonly long _length;
    private readonly IProgress<long>? _progress;

    public ProgressStreamContent(Stream stream, long length, IProgress<long>? progress)
    {
        _stream = stream;
        _length = length;
        _progress = progress;
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var buffer = new byte[1024 * 128];
        long total = 0;
        int read;
        while (total < _length
               && (read = await _stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, _length - total))).ConfigureAwait(false)) > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            total += read;
            _progress?.Report(total);
        }
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
        SerializeToStreamAsyncCore(stream, cancellationToken);

    private async Task SerializeToStreamAsyncCore(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 128];
        long total = 0;
        int read;
        while (total < _length
               && (read = await _stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, _length - total)), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
            _progress?.Report(total);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _length;
        return true;
    }
}
