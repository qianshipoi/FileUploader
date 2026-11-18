using System.Net;

namespace FileUploader.Core.Storage;

/// <summary>一次下载的结果。</summary>
public sealed record DownloadResult(bool Success, long Bytes, string? Error)
{
    public static DownloadResult Ok(long bytes) => new(true, bytes, null);

    public static DownloadResult Fail(string error) => new(false, 0, error);

    /// <summary>面向用户的一行说明。</summary>
    public string Describe() => Success ? $"成功（{Bytes} 字节）" : Error ?? "下载失败";
}

/// <summary>
/// 对象下载：把 GET 响应的字节流写到本地文件。
/// </summary>
/// <remarks>
/// 只认链接（预签名链接或直链），不额外加 Authorization：预签名链接的签名在查询参数里，
/// 再往请求头塞签名会被服务端当成"签名之外又多出来一段"而拒掉（403 SignatureDoesNotMatch）。
/// </remarks>
public static class ObjectDownloader
{
    private const int BufferSize = 1024 * 128;

    /// <summary>
    /// 下载 <paramref name="url"/> 到 <paramref name="destinationPath"/>。
    /// 先写 <c>.part</c> 临时文件再改名，中途失败不会在用户选的位置留下半个文件。
    /// </summary>
    public static async Task<DownloadResult> DownloadAsync(
        string url,
        string destinationPath,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return DownloadResult.Fail("链接无效。");
        }

        var temporaryPath = destinationPath + ".part";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await StorageHttp.Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return DownloadResult.Fail(DescribeFailure((int)response.StatusCode));
            }

            long total = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                useAsync: true))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    total += read;
                    progress?.Report(total);
                }
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
            return DownloadResult.Ok(total);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DeleteQuietly(temporaryPath);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(temporaryPath);
            return DownloadResult.Fail(ex.Message);
        }
    }

    /// <summary>把状态码翻成用户能看懂的一句话（403 最常见：签名过期或对象不可读）。</summary>
    private static string DescribeFailure(int statusCode) => statusCode switch
    {
        403 => "HTTP 403：链接已过期、对象不可读，请检查密钥与链接类型。",
        404 => "HTTP 404：对象不存在，可能已在桶里被删除。",
        416 => "HTTP 416：请求范围无效。",
        _ => $"HTTP {statusCode}：{ReasonPhrase(statusCode)}",
    };

    private static string ReasonPhrase(int statusCode) =>
        Enum.IsDefined(typeof(HttpStatusCode), statusCode) ? ((HttpStatusCode)statusCode).ToString() : "请求失败";

    private static void DeleteQuietly(string path)
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
            // 临时文件删不掉不重要，下次下载会用 .part 覆盖它。
        }
    }
}
