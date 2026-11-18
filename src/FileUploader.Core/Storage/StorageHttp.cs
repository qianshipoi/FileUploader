using System.Net;

namespace FileUploader.Core.Storage;

/// <summary>
/// 上传与下载共用的 <see cref="HttpClient"/>。
/// 超时给到 30 分钟：大文件在慢速线路上也能传完，真正的取消由调用方的 CancellationToken 负责。
/// </summary>
internal static class StorageHttp
{
    public static HttpClient Client { get; } = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromMinutes(30),
    };
}
