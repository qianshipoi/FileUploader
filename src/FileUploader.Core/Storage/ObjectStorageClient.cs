using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

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
/// 极简对象存储客户端：使用 PUT Object 上传，签名支持阿里云 OSS V1 与 AWS SigV4。
/// </summary>
public sealed class ObjectStorageClient
{
    private static readonly Regex XmlErrorRegex = new(
        "<(Code|Message)>(.*?)</\\1>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromMinutes(30),
    };

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

        var extraHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_settings.PublicReadObject)
        {
            // 让生成的链接可以被匿名访问（否则私有桶的链接打开是 403）。
            extraHeaders[_settings.Protocol == StorageProtocol.AliyunOss ? "x-oss-object-acl" : "x-amz-acl"] = "public-read";
        }

        Sign(request, HttpMethod.Put.Method, uri, canonicalResource, contentType, payloadHash, extraHeaders);

        try
        {
            using var response = await Http
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

    /// <summary>删除对象（DELETE Object）。</summary>
    public async Task<StorageResult> DeleteObjectAsync(string key, CancellationToken cancellationToken)
    {
        var (uri, canonicalResource) = BuildObjectRequestUri(key);
        using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
        Sign(request, HttpMethod.Delete.Method, uri, canonicalResource, string.Empty, EmptyPayloadHash());

        try
        {
            using var response = await Http
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
            using var response = await Http
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
            using var response = await Http
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

    private void Sign(
        HttpRequestMessage request,
        string method,
        Uri uri,
        string canonicalResource,
        string contentType,
        string payloadHash,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        var now = DateTimeOffset.UtcNow;
        extraHeaders ??= new Dictionary<string, string>();

        if (_settings.Protocol == StorageProtocol.AliyunOss)
        {
            var date = OssV1Signer.FormatDate(now);
            var stringToSign = OssV1Signer.BuildStringToSign(method, string.Empty, contentType, date, extraHeaders, canonicalResource);
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
            string.Empty,
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

/// <summary>把上传进度作为<see cref="IProgress{T}"/>报告的请求体。</summary>
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
        while ((read = await _stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
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
        while ((read = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
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
