using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FileUploader.Core;
using FileUploader.Core.Storage;

var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "vectors";
var failures = 0;

void Check(string name, string actual, string expected)
{
    var ok = string.Equals(actual, expected, StringComparison.Ordinal);
    if (!ok)
    {
        failures++;
    }

    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
    if (!ok)
    {
        Console.WriteLine($"      expected: {expected}");
        Console.WriteLine($"      actual  : {actual}");
    }
}

switch (mode)
{
    case "vectors":
        RunVectors();
        break;
    case "upload":
        await RunUploadAsync();
        break;
    case "live-oss":
        await RunLiveOssAsync();
        break;
    case "settings":
        WriteSettingsFile(args.Length > 1 ? args[1] : null);
        break;
    case "delete-object":
        await DeleteObjectUsingSavedSettingsAsync(Required("FU_KEY"));
        break;
    case "exchange":
        RunExchangeChecks();
        break;
    case "presign":
        RunPresignVectors();
        await RunPresignLiveAsync();
        break;
    case "download":
        await RunDownloadAsync();
        break;
    case "zip":
        await RunZipChecksAsync();
        break;
    case "update":
        RunUpdateChecks();
        break;
    default:
        Console.WriteLine("用法: FileUploader.Verify [vectors|exchange|presign|download|zip|upload|live-oss|settings|delete-object]");
        return 2;
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

void RunVectors()
{
    // 阿里云 OSS V1：文档中的 PUT Object 示例（StringToSign 顺序与格式）。
    var ossStringToSign = OssV1Signer.BuildStringToSign(
        "PUT",
        "ODBGOERFMDMzQTczRUY3NUE3NzA5QzdFNUYzMDQxNEMt",
        "text/html",
        "Thu, 17 Nov 2005 18:49:58 GMT",
        new[]
        {
            new KeyValuePair<string, string>("x-oss-meta-author", "foo@bar.com"),
            new KeyValuePair<string, string>("x-oss-magic", "abracadabra"),
        },
        "/oss-example/nelson");

    Check(
        "OSS V1 StringToSign",
        ossStringToSign,
        "PUT\nODBGOERFMDMzQTczRUY3NUE3NzA5QzdFNUYzMDQxNEMt\ntext/html\nThu, 17 Nov 2005 18:49:58 GMT\nx-oss-magic:abracadabra\nx-oss-meta-author:foo@bar.com\n/oss-example/nelson");

    // AWS SigV4 文档示例：GET Object（examplebucket / test.txt）。
    var timestamp = new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero);
    var amzDate = timestamp.ToString("yyyyMMdd'T'HHmmss'Z'");
    var payloadHash = S3SigV4Signer.EmptyPayloadHash;
    var authorization = S3SigV4Signer.ComputeAuthorization(
        "GET",
        "/test.txt",
        string.Empty,
        new[]
        {
            new KeyValuePair<string, string>("host", "examplebucket.s3.amazonaws.com"),
            new KeyValuePair<string, string>("range", "bytes=0-9"),
            new KeyValuePair<string, string>("x-amz-content-sha256", payloadHash),
            new KeyValuePair<string, string>("x-amz-date", amzDate),
        },
        payloadHash,
        "us-east-1",
        "s3",
        "AKIAIOSFODNN7EXAMPLE",
        "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
        timestamp,
        out var generatedAmzDate,
        out var signedHeaders);

    Check("SigV4 x-amz-date", generatedAmzDate, "20130524T000000Z");
    Check("SigV4 signed headers", signedHeaders, "host;range;x-amz-content-sha256;x-amz-date");
    Check(
        "SigV4 Authorization (AWS 文档示例)",
        authorization,
        "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41");

    // 同一密钥、同一请求签名应稳定（防止时区/排序漂移）。
    var repeat = S3SigV4Signer.ComputeAuthorization(
        "GET",
        "/test.txt",
        string.Empty,
        new[]
        {
            new KeyValuePair<string, string>("host", "examplebucket.s3.amazonaws.com"),
            new KeyValuePair<string, string>("range", "bytes=0-9"),
            new KeyValuePair<string, string>("x-amz-content-sha256", payloadHash),
            new KeyValuePair<string, string>("x-amz-date", amzDate),
        },
        payloadHash,
        "us-east-1",
        "s3",
        "AKIAIOSFODNN7EXAMPLE",
        "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
        timestamp,
        out _,
        out _);
    Check("SigV4 幂等", repeat, authorization);

    // 对象键构造
    var settings = new AppSettings { KeyPrefix = "images", UseDateFolder = true };
    var fixedStamp = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.FromHours(8));

    // 防重名段固定成 "120000-a1b2c3d4" 以便断言；真实运行时由时间 + 随机后缀生成。
    Check(
        "Key 构造（前缀 + 日期 + 防重名段 + 文件名）",
        ObjectKey.Build(settings, "示例 图.png", null, fixedStamp, "120000-a1b2c3d4"),
        "images/2026/11/16/120000-a1b2c3d4/示例 图.png");

    Check(
        "Key 构造（中文文件名仍是最后一段，便于下载保留原名）",
        ObjectKey.Build(settings, "示例 图.png", null, fixedStamp, "120000-a1b2c3d4").Split('/')[^1],
        "示例 图.png");

    Check(
        "Key 构造（同一天同名文件两次上传不重名）",
        ObjectKey.Build(settings, "a.png", null, fixedStamp) == ObjectKey.Build(settings, "a.png", null, fixedStamp) ? "same" : "different",
        "different");

    Check(
        "Key 构造（子目录 + 不按日期分组）",
        ObjectKey.Build(settings with { UseDateFolder = false }, "a.png", @"sub\dir", fixedStamp, "120000-a1b2c3d4"),
        "images/120000-a1b2c3d4/sub/dir/a.png");
    Check("URL 编码", ObjectKey.EncodePath("a b/中文.png"), "a%20b/%E4%B8%AD%E6%96%87.png");

    // URL 生成（虚拟主机 / 路径风格 / 自定义域名）
    var virtualHost = new AppSettings { Endpoint = "oss-cn-hangzhou.aliyuncs.com", Bucket = "demo", AccessKeyId = "ak", AccessKeySecret = "sk" };
    Check("URL（虚拟主机）", new ObjectStorageClient(virtualHost).BuildObjectUrl("a/b.png"), "https://demo.oss-cn-hangzhou.aliyuncs.com/a/b.png");
    Check("URL（路径风格）", new ObjectStorageClient(virtualHost with { PathStyle = true }).BuildObjectUrl("a/b.png"), "https://oss-cn-hangzhou.aliyuncs.com/demo/a/b.png");
    Check("URL（自定义域名）", new ObjectStorageClient(virtualHost with { PublicBaseUrl = "https://cdn.example.com/" }).BuildObjectUrl("a/b.png"), "https://cdn.example.com/a/b.png");

    // DPAPI 加解密 + 配置落盘往返
    if (OperatingSystem.IsWindows())
    {
        var directory = Path.Combine(Path.GetTempPath(), "fu-settings-test-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(directory, "settings.json");
        var original = virtualHost with
        {
            AccessKeySecret = "s3cr3t-中文-🔑",
            Bucket = "roundtrip",
            KeyPrefix = "p/",
            UseDateFolder = false,
            PathStyle = true,
            FlashDropEnabled = false,
            StartSilently = true,
            StartWithWindows = true,
            ZipBatchUpload = true,
        };

        SettingsStore.SaveTo(file, original);
        var raw = File.ReadAllText(file);
        var loaded = SettingsStore.LoadFrom(file, out var warning);
        Check("配置往返（除密钥外相等）", (loaded with { AccessKeySecret = "" }).ToString(), (original with { AccessKeySecret = "" }).ToString());
        Check("密钥 DPAPI 往返", loaded.AccessKeySecret, original.AccessKeySecret);
        Console.WriteLine($"INFO  DPAPI LastError={FileUploader.Core.Dpapi.LastError}");
        Check("密钥未明文落盘", raw.Contains("s3cr3t", StringComparison.Ordinal) ? "明文" : "已加密", "已加密");
        Directory.Delete(directory, recursive: true);
        Console.WriteLine($"INFO  默认配置路径: {SettingsStore.FilePath} {warning}");
    }
}

async Task RunUploadAsync()
{
    var settings = new AppSettings
    {
        Protocol = ReadEnv("FU_PROTOCOL") == "oss" ? StorageProtocol.AliyunOss : StorageProtocol.S3Compatible,
        Endpoint = Required("FU_ENDPOINT"),
        Bucket = Required("FU_BUCKET"),
        AccessKeyId = Required("FU_AK"),
        AccessKeySecret = Required("FU_SK"),
        Region = ReadEnv("FU_REGION") is { Length: > 0 } region ? region : "us-east-1",
        PathStyle = ReadEnv("FU_PATHSYLE") != "false",
        UseHttps = ReadEnv("FU_HTTPS") == "true",
        KeyPrefix = ReadEnv("FU_PREFIX") ?? "verify",
        UseDateFolder = ReadEnv("FU_DATEFOLDER") != "false",
    };

    Console.WriteLine($"INFO  {settings.Protocol} endpoint={settings.Endpoint} bucket={settings.Bucket} pathStyle={settings.PathStyle} https={settings.UseHttps}");

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var client = new ObjectStorageClient(settings);

    var head = await client.HeadBucketAsync(cts.Token);
    Console.WriteLine($"INFO  HEAD bucket → {head.Describe()}");

    var ensure = await client.EnsureBucketAsync(cts.Token);
    Console.WriteLine($"INFO  Ensure bucket → {ensure.Describe()}");
    if (!ensure.Success)
    {
        failures++;
        return;
    }

    var temp = Path.Combine(Path.GetTempPath(), "fu-upload-" + Guid.NewGuid().ToString("N") + ".txt");
    var content = "hello from FileUploader " + DateTimeOffset.Now.ToString("O") + " 中文内容";
    await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false));

    var key = ObjectKey.Build(settings, Path.GetFileName(temp), null, DateTimeOffset.Now);
    Console.WriteLine($"INFO  上传 key={key} size={new FileInfo(temp).Length}");

    var lastReported = 0L;
    var progress = new Progress<long>(bytes =>
    {
        lastReported = bytes;
    });

    var result = await client.PutObjectAsync(temp, key, ContentTypes.ForFileName(temp), progress, cts.Token);
    Console.WriteLine($"INFO  PUT → {result.Describe()}");
    if (!result.Success)
    {
        failures++;
        return;
    }

    Console.WriteLine($"PASS  上传成功，进度回调最后值={lastReported}");
    Console.WriteLine($"URL   {result.Url}");
    Console.WriteLine($"KEY   {key}");
    Console.WriteLine($"LOCAL {temp}");
}

async Task RunLiveOssAsync()
{
    // 用真实 OSS 公开读的示例桶做一次不写数据的探活：仅验证签名可被服务端接受。
    // （写操作会落到用户桶，需显式通过 upload 子命令触发。）
    Console.WriteLine("live-oss 需要真实凭证，请使用 upload 子命令并通过环境变量传入。");
    await Task.CompletedTask;
}

async Task DeleteObjectUsingSavedSettingsAsync(string key)
{
    var settings = SettingsStore.Load(out _);
    Console.WriteLine($"INFO  删除 {settings.Bucket}/{key} @ {settings.NormalizedEndpoint}");
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var client = new ObjectStorageClient(settings);
    var result = await client.DeleteObjectAsync(key, cts.Token);
    Console.WriteLine($"INFO  DELETE → {result.Describe()}");
    if (!result.Success)
    {
        failures++;
    }
}
void RunExchangeChecks()
{
    var settings = new AppSettings
    {
        Protocol = StorageProtocol.S3Compatible,
        Endpoint = "127.0.0.1:9100",
        Bucket = "exchange-bucket",
        AccessKeyId = "exchange-ak",
        AccessKeySecret = "exchange-secret-明文",
        Region = "us-east-1",
        KeyPrefix = "images",
        PathStyle = true,
        UseHttps = false,
    };

    var dir = Path.Combine(Path.GetTempPath(), "fu-exchange-" + Guid.NewGuid().ToString("N"));
    var binPath = Path.Combine(dir, SettingsExchange.FileName);
    try
    {
        var export = SettingsExchange.Export(settings, binPath);
        Check("导出成功", export.Success ? "ok" : export.Error ?? "fail", "ok");

        var bytes = File.ReadAllBytes(binPath);
        var asText = System.Text.Encoding.UTF8.GetString(bytes);
        Check("不是可读文本（无 JSON 字段名）", asText.Contains("AccessKeySecret", StringComparison.Ordinal) ? "plain" : "binary", "binary");
        Check("不含明文密钥", asText.Contains("exchange-secret", StringComparison.Ordinal) ? "plain" : "encrypted", "encrypted");
        Check("不含桶名明文", asText.Contains("exchange-bucket", StringComparison.Ordinal) ? "plain" : "encrypted", "encrypted");
        Check("二进制魔数", bytes.Length > 8 && bytes[0] == (byte)'F' && bytes[1] == (byte)'U' && bytes[2] == (byte)'X' ? "ok" : "bad", "ok");

        var import = SettingsExchange.Import(binPath);
        Check("导入成功", import.Imported ? "ok" : import.Error ?? "fail", "ok");
        Check("密钥往返一致", import.Settings?.AccessKeySecret ?? "", settings.AccessKeySecret);
        Check("桶名往返一致", import.Settings?.Bucket ?? "", settings.Bucket);
        Check("其他字段往返一致", (import.Settings?.ToString() ?? "").Replace(settings.AccessKeySecret, "***"), settings.ToString().Replace(settings.AccessKeySecret, "***"));


        // 明文 JSON 不再被接受（导出格式只有二进制）。
        var tampered = (byte[])bytes.Clone();
        tampered[tampered.Length - 1] ^= 0xFF;
        var badPath = Path.Combine(dir, "tampered.export.bin");
        File.WriteAllBytes(badPath, tampered);
        var bad = SettingsExchange.Import(badPath);
        Check("篡改文件被拒绝", bad.Imported ? "imported" : "rejected", "rejected");

        var plainJsonPath = Path.Combine(dir, "plain.json");
        File.WriteAllText(plainJsonPath, "{\"Schema\":1,\"Bucket\":\"x\"}");
        var plainJson = SettingsExchange.Import(plainJsonPath);
        Check("拒绝明文 JSON（无兼容分支）", plainJson.Imported ? "imported" : "rejected", "rejected");
    }
    finally
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
void RunPresignVectors()
{
    // OSS V1 预签名：签名期望值由独立的 Python 实现算出（hmac-sha1 + base64 + URL 编码）。
    var oss = new AppSettings
    {
        Protocol = StorageProtocol.AliyunOss,
        Endpoint = "oss-cn-hangzhou.aliyuncs.com",
        Bucket = "demo",
        AccessKeyId = "testak",
        AccessKeySecret = "testsecret",
        UrlMode = UrlMode.Signed,
        SignedUrlExpireDays = 7,
    };

    var issuedAt = DateTimeOffset.FromUnixTimeSeconds(1893456000 - (7 * 86400));
    Check(
        "OSS V1 预签名 URL（7 天）",
        ObjectUrlBuilder.SignedUrl(oss, "images/a.png", issuedAt),
        "https://demo.oss-cn-hangzhou.aliyuncs.com/images/a.png?OSSAccessKeyId=testak&Expires=1893456000&Signature=9URSUem2juM9QUHYPHvE68DvIAQ%3D");

    // S3 SigV4 预签名：形状 + 有效期上限收敛到 7 天。
    var s3 = oss with { Protocol = StorageProtocol.S3Compatible, Endpoint = "127.0.0.1:9100", PathStyle = true, UseHttps = false, SignedUrlExpireDays = 30 };
    var signed = ObjectUrlBuilder.SignedUrl(s3, "images/a.png", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    Check("SigV4 签名参数齐全", signed.Contains("X-Amz-Algorithm=AWS4-HMAC-SHA256") && signed.Contains("X-Amz-Signature=") ? "ok" : "missing", "ok");
    Check("SigV4 有效期收敛到 7 天", signed.Contains("X-Amz-Expires=604800") ? "ok" : "bad", "ok");
    Check("SigV4 路径风格地址", signed.StartsWith("http://127.0.0.1:9100/demo/images/a.png?", StringComparison.Ordinal) ? "ok" : "bad", "ok");

    // 直链模式仍用自定义域名
    var direct = oss with { UrlMode = UrlMode.Direct, PublicBaseUrl = "https://cdn.example.com" };
    Check("直链 URL（自定义域名）", ObjectUrlBuilder.Build(direct, "images/a.png", DateTimeOffset.UtcNow), "https://cdn.example.com/images/a.png");

    // 程序内下载用的地址：只要还握着密钥就现签一个链接，与「链接类型」无关 ——
    // 直链模式下桶可能是私有的，拿旧直链去 GET 会 403。
    var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    Check("下载地址（签名模式=现签）", ObjectUrlBuilder.DownloadUrl(oss, "images/a.png", "https://stale.example.com/x.png", now)!, ObjectUrlBuilder.SignedUrl(oss, "images/a.png", now));
    Check("下载地址（直链模式也现签）", ObjectUrlBuilder.DownloadUrl(direct, "images/a.png", "https://cdn.example.com/images/a.png", now)!, ObjectUrlBuilder.SignedUrl(direct, "images/a.png", now));
    var unconfigured = oss with { AccessKeyId = string.Empty };
    Check("下载地址（未配置退回旧链接）", ObjectUrlBuilder.DownloadUrl(unconfigured, "images/a.png", "https://stale.example.com/x.png", now)!, "https://stale.example.com/x.png");
    Check("下载地址（未配置且无旧链接）", ObjectUrlBuilder.DownloadUrl(unconfigured, "images/a.png", null, now) ?? "<null>", "<null>");
}

async Task RunPresignLiveAsync()
{
    if (ReadEnv("FU_ENDPOINT") is null)
    {
        Console.WriteLine("SKIP  预签名实链验证（未设置 FU_ENDPOINT）");
        return;
    }

    var settings = new AppSettings
    {
        Protocol = ReadEnv("FU_PROTOCOL") == "oss" ? StorageProtocol.AliyunOss : StorageProtocol.S3Compatible,
        Endpoint = Required("FU_ENDPOINT"),
        Bucket = Required("FU_BUCKET"),
        AccessKeyId = Required("FU_AK"),
        AccessKeySecret = Required("FU_SK"),
        Region = ReadEnv("FU_REGION") is { Length: > 0 } region ? region : "us-east-1",
        PathStyle = ReadEnv("FU_PATHSYLE") != "false",
        UseHttps = ReadEnv("FU_HTTPS") == "true",
        KeyPrefix = "verify",
        PublicReadObject = false,
        UrlMode = UrlMode.Signed,
        SignedUrlExpireDays = 7,
    };

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var client = new ObjectStorageClient(settings);
    var ensure = await client.EnsureBucketAsync(cts.Token);
    if (!ensure.Success)
    {
        failures++;
        Console.WriteLine($"FAIL  建桶失败：{ensure.Describe()}");
        return;
    }

    var temp = Path.Combine(Path.GetTempPath(), "fu-presign-" + Guid.NewGuid().ToString("N") + ".txt");
    var content = "presigned url check " + Guid.NewGuid();
    await File.WriteAllTextAsync(temp, content);

    var key = ObjectKey.Build(settings, Path.GetFileName(temp), null, DateTimeOffset.Now);
    var put = await client.PutObjectAsync(temp, key, ContentTypes.ForFileName(temp), null, cts.Token);
    if (!put.Success)
    {
        failures++;
        Console.WriteLine($"FAIL  上传失败：{put.Describe()}");
        return;
    }

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    var signedUrl = ObjectUrlBuilder.SignedUrl(settings, key, DateTimeOffset.Now);
    var signedResponse = await http.GetAsync(signedUrl, cts.Token);
    var body = await signedResponse.Content.ReadAsStringAsync(cts.Token);
    Check("签名链接 GET 返回 200", ((int)signedResponse.StatusCode).ToString(), "200");
    Check("签名链接内容一致", body, content);

    var directResponse = await http.GetAsync(put.Url, cts.Token);
    Check("直链在私有桶上被拒绝（403）", ((int)directResponse.StatusCode).ToString(), "403");

    var expiredUrl = ObjectUrlBuilder.SignedUrl(settings, key, DateTimeOffset.UtcNow.AddDays(-(settings.SignedUrlExpireDays + 1)));
    var expiredResponse = await http.GetAsync(expiredUrl, cts.Token);
    Check("过期签名链接被拒绝（403）", ((int)expiredResponse.StatusCode).ToString(), "403");
}
/// <summary>
/// 程序内下载的离线回归：用本机 TcpListener 假装对象存储，验证
/// 「字节落盘 / 进度回调 / 临时文件清理 / 403·404 的说明 / 取消」。
/// 不连真实存储，所以不会碰到用户桶里的数据。
/// </summary>
async Task RunDownloadAsync()
{
    var payload = Encoding.UTF8.GetBytes("hello download " + Guid.NewGuid().ToString("N") + " 中文内容");
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var prefix = $"http://127.0.0.1:{port}";
    using var stop = new CancellationTokenSource();

    var server = Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var requestLine = await ReadRequestLineAsync(stream);
                    var path = requestLine.Split(' ').Skip(1).FirstOrDefault() ?? "/";
                    var (status, reason, body) = path switch
                    {
                        "/ok" => (200, "OK", payload),
                        "/forbidden" => (403, "Forbidden", Array.Empty<byte>()),
                        _ => (404, "Not Found", Array.Empty<byte>()),
                    };

                    var head = $"HTTP/1.1 {status} {reason}\r\nContent-Length: {body.Length}\r\n" +
                               "Content-Type: application/octet-stream\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                    if (body.Length > 0)
                    {
                        await stream.WriteAsync(body);
                    }

                    await stream.FlushAsync();

                    // 发完就半关：直接 Dispose 会在对端还有未读数据时发 RST，HttpClient 会报
                    // "远程主机强迫关闭了一个现有的连接"。
                    client.Client.Shutdown(SocketShutdown.Send);
                }
            });
        }
    });

    var dir = Path.Combine(Path.GetTempPath(), "fu-download-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var destination = Path.Combine(dir, "out.txt");
        long reported = 0;
        var ok = await ObjectDownloader.DownloadAsync(prefix + "/ok", destination, new InlineProgress<long>(bytes => reported = bytes), CancellationToken.None);
        Check("下载成功", ok.Success ? "ok" : ok.Error ?? "fail", "ok");
        Check("下载内容一致", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(destination)), Encoding.UTF8.GetString(payload));
        Check("下载字节数", ok.Bytes.ToString(), payload.Length.ToString());
        Check("进度回调到最终字节数", reported.ToString(), payload.Length.ToString());
        Check("没有残留 .part", File.Exists(destination + ".part") ? "left" : "clean", "clean");

        var forbidden = Path.Combine(dir, "forbidden.txt");
        var denied = await ObjectDownloader.DownloadAsync(prefix + "/forbidden", forbidden, null, CancellationToken.None);
        Check("403 判为失败", denied.Success ? "ok" : "failed", "failed");
        Check("403 的说明里带状态码", denied.Error?.Contains("403", StringComparison.Ordinal) == true ? "ok" : denied.Error ?? "<null>", "ok");
        Check("失败时不留文件", File.Exists(forbidden) || File.Exists(forbidden + ".part") ? "left" : "clean", "clean");

        var missing = await ObjectDownloader.DownloadAsync(prefix + "/missing", Path.Combine(dir, "missing.txt"), null, CancellationToken.None);
        Check("404 的说明里提示对象不存在", missing.Error?.Contains("不存在", StringComparison.Ordinal) == true ? "ok" : missing.Error ?? "<null>", "ok");

        var invalid = await ObjectDownloader.DownloadAsync("不是链接", Path.Combine(dir, "x.txt"), null, CancellationToken.None);
        Check("无效链接直接失败", invalid.Success ? "ok" : "failed", "failed");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await ObjectDownloader.DownloadAsync(prefix + "/ok", Path.Combine(dir, "canceled.txt"), null, canceled.Token);
            Check("取消后抛 OperationCanceledException", "no throw", "throw");
        }
        catch (OperationCanceledException)
        {
            Check("取消后抛 OperationCanceledException", "throw", "throw");
        }
    }
    finally
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    stop.Cancel();
    listener.Stop();
    await server;
}

/// <summary>
/// 读完整个请求头并返回第一行（请求行）。必须读到空行为止：
/// 留下没读完的请求数据就关 socket 会触发 RST，客户端看到的是"连接被强制关闭"。
/// </summary>
async Task<string> ReadRequestLineAsync(NetworkStream stream)
{
    var bytes = new List<byte>();
    var one = new byte[1];
    while (true)
    {
        var read = await stream.ReadAsync(one);
        if (read == 0)
        {
            break;
        }

        bytes.Add(one[0]);
        if (bytes.Count >= 4 && bytes[^4] == (byte)'\r' && bytes[^3] == (byte)'\n' && bytes[^2] == (byte)'\r' && bytes[^1] == (byte)'\n')
        {
            break;
        }
    }

    var text = Encoding.ASCII.GetString(bytes.ToArray());
    return text.Split("\r\n")[0];
}

/// <summary>
/// 「多个文件打包成 ZIP 上传」的离线回归：真实文件 → 真 zip → 逐条读回来比对。
/// 覆盖压缩包内路径、跳过读不到的文件、已压缩格式走 stored、取消与全失败不留半成品。
/// </summary>
async Task RunZipChecksAsync()
{
    var dir = Path.Combine(Path.GetTempPath(), "fu-zip-" + Guid.NewGuid().ToString("N"));
    var source = Path.Combine(dir, "src");
    Directory.CreateDirectory(Path.Combine(source, "sub"));
    try
    {
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "AAA 中文", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(source, "sub", "b.txt"), "BBB", new UTF8Encoding(false));

        // 已经是压缩格式的扩展名：应当 stored（压缩前后等长），避免白烧 CPU。
        var jpgPath = Path.Combine(source, "c.jpg");
        await File.WriteAllBytesAsync(jpgPath, Enumerable.Repeat((byte)'x', 8192).ToArray());

        var entries = new[]
        {
            new ZipEntry(Path.Combine(source, "a.txt"), ZipPackager.CombineEntryName("A", null, "a.txt")),
            new ZipEntry(Path.Combine(source, "sub", "b.txt"), ZipPackager.CombineEntryName("A", "sub", "b.txt")),
            new ZipEntry(jpgPath, ZipPackager.CombineEntryName("A", null, "c.jpg")),
            new ZipEntry(Path.Combine(source, "missing.txt"), ZipPackager.CombineEntryName("A", null, "missing.txt")),
        };

        var zipPath = Path.Combine(dir, "A.zip");
        var reports = new List<int>();
        var result = await ZipPackager.CreateAsync(zipPath, entries, new InlineProgress<int>(reports.Add), CancellationToken.None);
        Check("打包成功", result.Success ? "ok" : result.Error ?? "fail", "ok");
        Check("打包跳过读不到的文件", result.Skipped.ToString(), "1");
        Check("打包条目数", result.Entries.ToString(), "3");
        Check("进度逐个上报", string.Join(",", reports), "1,2,3");
        Check("结果字节数与文件一致", result.Bytes.ToString(), new FileInfo(zipPath).Length.ToString());

        using (var archive = ZipFile.OpenRead(zipPath))
        {
            Check(
                "压缩包内路径",
                string.Join("|", archive.Entries.Select(e => e.FullName).OrderBy(x => x, StringComparer.Ordinal)),
                "A/a.txt|A/c.jpg|A/sub/b.txt");

            using var reader = new StreamReader(archive.GetEntry("A/sub/b.txt")!.Open(), Encoding.UTF8);
            Check("压缩包内内容一致", await reader.ReadToEndAsync(), "BBB");

            var jpg = archive.GetEntry("A/c.jpg")!;
            Check("已压缩格式走 stored", (jpg.CompressedLength == jpg.Length).ToString(), "True");
        }

        Check("zip 名（有文件夹）", ZipPackager.ChooseFileName(new[] { "照片", null }), "照片.zip");
        Check("zip 名（没有文件夹）", ZipPackager.ChooseFileName(new[] { (string?)null }), "打包.zip");
        Check("zip 名（已带 .zip 不重复）", ZipPackager.ChooseFileName(new[] { "备份.zip" }), "备份.zip");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var canceledPath = Path.Combine(dir, "canceled.zip");
        try
        {
            await ZipPackager.CreateAsync(canceledPath, entries, null, canceled.Token);
            Check("打包取消抛 OperationCanceledException", "no throw", "throw");
        }
        catch (OperationCanceledException)
        {
            Check("打包取消抛 OperationCanceledException", "throw", "throw");
        }

        Check("取消后不留半成品", File.Exists(canceledPath) ? "left" : "clean", "clean");

        var nothingPath = Path.Combine(dir, "nothing.zip");
        var nothing = await ZipPackager.CreateAsync(
            nothingPath,
            new[] { new ZipEntry(Path.Combine(dir, "nope.txt"), "nope.txt") },
            null,
            CancellationToken.None);
        Check("所有文件都读不到时失败", nothing.Success ? "ok" : "failed", "failed");
        Check("失败不留文件", File.Exists(nothingPath) ? "left" : "clean", "clean");
    }
    finally
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>
/// 检查更新的离线回归：版本比较、从 Release 说明里抽「本次变更」、抽 SHA256、解析 GitHub 的 release JSON。
/// 不打网络（真下载见 tools/update-e2e-test.ps1）。
/// </summary>
void RunUpdateChecks()
{
    Check("版本比较：补丁号更高", UpdateChecker.IsNewer("0.2.1", "0.2.0") ? "newer" : "not", "newer");
    Check("版本比较：v 前缀不影响", UpdateChecker.IsNewer("v0.3.0", "0.2.9") ? "newer" : "not", "newer");
    Check("版本比较：同版本算最新", UpdateChecker.IsNewer("0.2.0", "v0.2.0") ? "newer" : "same", "same");
    Check("版本比较：旧版本不算新", UpdateChecker.IsNewer("0.1.9", "0.2.0") ? "newer" : "old", "old");
    Check("版本比较：1.0 比 0.9.9 新", UpdateChecker.IsNewer("1.0", "0.9.9") ? "newer" : "not", "newer");
    Check("版本比较：正式版比预发布新", UpdateChecker.IsNewer("0.3.0", "0.3.0-beta.2") ? "newer" : "not", "newer");
    Check("版本比较：预发布不比正式版新", UpdateChecker.IsNewer("0.3.0-beta.2", "0.3.0") ? "newer" : "not", "not");
    Check("版本比较：beta.10 比 beta.9 新（字典序陷阱）", UpdateChecker.IsNewer("0.3.0-beta.10", "0.3.0-beta.9") ? "newer" : "not", "newer");
    Check("版本比较：非法版本号不误判", UpdateChecker.IsNewer("不是版本", "0.2.0") ? "newer" : "not", "not");
    Check("版本号归一化", UpdateChecker.NormalizeVersion("v0.2.0+abc1234"), "0.2.0");

    // Release 说明：真实结构（含「本次变更」小节 + SHA256 校验块）
    var notes = string.Join("\n", new[]
    {
        "## OSS 拖拽上传",
        "",
        "### 下载",
        "",
        "| 文件 | 说明 |",
        "|---|---|",
        "| `FileUploader-0.3.0-win-x64.exe` | 单文件便携版 |",
        "",
        "### 校验",
        "",
        "```",
        "SHA256 (FileUploader-0.3.0-win-x64.exe) = " + new string('a', 64),
        "SHA256 (FileUploader-0.3.0-win-x64.zip) = " + new string('b', 64),
        "```",
        "",
        "### 本次变更（v0.2.0..v0.3.0）",
        "",
        "- feat(ui): 新增检查更新 (abc1234)",
        "- fix(core): 修个 bug (def5678)",
        "",
        "### 感谢",
        "",
        "- 不应该被当成变更收集",
    });

    var changes = UpdateChecker.ExtractChanges(notes);
    Check("提取变更条数", changes.Count.ToString(), "2");
    Check("提取变更第一条", changes[0], "feat(ui): 新增检查更新 (abc1234)");
    Check("变更只取本节（后面的小节不算）", string.Join("|", changes), "feat(ui): 新增检查更新 (abc1234)|fix(core): 修个 bug (def5678)");

    Check("SHA 按资产名优先匹配", UpdateChecker.FindSha256(notes, "FileUploader-0.3.0-win-x64.zip") ?? "", new string('b', 64));
    Check("SHA 资产名对不上时退回第一个", UpdateChecker.FindSha256(notes, "other.exe") ?? "", new string('a', 64));

    // GitHub release JSON（形状与真实接口一致）
    var json = string.Join("\n", new[]
    {
        "{",
        "  \"tag_name\": \"v0.3.0\",",
        "  \"name\": \"OSS 拖拽上传 v0.3.0\",",
        "  \"html_url\": \"https://github.com/qianshipoi/FileUploader/releases/tag/v0.3.0\",",
        "  \"published_at\": \"2026-11-18T06:00:00Z\",",
        "  \"prerelease\": false,",
        "  \"body\": " + System.Text.Json.JsonSerializer.Serialize(notes) + ",",
        "  \"assets\": [",
        "    { \"name\": \"FileUploader-0.3.0-win-x64.zip\", \"size\": 87074223, \"digest\": \"sha256:" + new string('B', 64) + "\", \"browser_download_url\": \"https://example.com/x.zip\" },",
        "    { \"name\": \"FileUploader-0.3.0-win-x64.exe\", \"size\": 93026337, \"digest\": \"sha256:" + new string('A', 64) + "\", \"browser_download_url\": \"https://example.com/x.exe\" }",
        "  ]",
        "}",
    });

    var parsed = UpdateChecker.Parse("0.2.0", json);
    Check("解析：有更新", parsed.Status.ToString(), "UpdateAvailable");
    Check("解析：最新版本（去掉 v）", parsed.LatestVersion ?? "", "0.3.0");
    Check("解析：tag", parsed.TagName ?? "", "v0.3.0");
    Check("解析：选的是 exe 资产", parsed.AssetName ?? "", "FileUploader-0.3.0-win-x64.exe");
    Check("解析：下载地址", parsed.AssetUrl ?? "", "https://example.com/x.exe");
    Check("解析：资产大小", parsed.AssetSize.ToString(), "93026337");
    Check("解析：digest 归一化成小写", parsed.AssetSha256 ?? "", new string('a', 64));
    Check("解析：变更清单", (parsed.Changes?.Count ?? 0).ToString(), "2");
    Check("解析：发布页链接", parsed.ReleasePageUrl ?? "", "https://github.com/qianshipoi/FileUploader/releases/tag/v0.3.0");
    Check("解析：同版本 → 已是最新", UpdateChecker.Parse("0.3.0", json).Status.ToString(), "UpToDate");
    Check("解析：没有 tag 时判失败", UpdateChecker.Parse("0.2.0", "{\"assets\":[]}").Status.ToString(), "Failed");
}

void WriteSettingsFile(string? targetPath = null)
{
    var settings = new AppSettings
    {
        Protocol = ReadEnv("FU_PROTOCOL") == "oss" ? StorageProtocol.AliyunOss : StorageProtocol.S3Compatible,
        Endpoint = Required("FU_ENDPOINT"),
        Bucket = Required("FU_BUCKET"),
        AccessKeyId = Required("FU_AK"),
        AccessKeySecret = Required("FU_SK"),
        Region = ReadEnv("FU_REGION") is { Length: > 0 } region ? region : "us-east-1",
        PathStyle = ReadEnv("FU_PATHSYLE") != "false",
        UseHttps = ReadEnv("FU_HTTPS") == "true",
        KeyPrefix = ReadEnv("FU_PREFIX") ?? "images",
        UseDateFolder = ReadEnv("FU_DATEFOLDER") != "false",
    };

    if (targetPath is null)
    {
        SettingsStore.Save(settings);
    }
    else
    {
        SettingsStore.SaveTo(targetPath, settings);
    }

    var reloaded = targetPath is null ? SettingsStore.Load(out var warning) : SettingsStore.LoadFrom(targetPath, out warning);
    Check("配置写入并可读回", reloaded.IsConfigured ? "ok" : "missing", "ok");
    Console.WriteLine($"INFO  已写入 {(targetPath ?? SettingsStore.FilePath)}");
    if (!string.IsNullOrEmpty(warning))
    {
        Console.WriteLine($"WARN  {warning}");
    }
}

string Required(string name) =>
    ReadEnv(name) ?? throw new InvalidOperationException($"缺少环境变量 {name}");

string? ReadEnv(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    return string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// 同步执行的 <see cref="IProgress{T}"/>：<see cref="Progress{T}"/> 会把回调投递到线程池，
/// 断言时可能还没跑到，所以测试里要就地执行。
/// </summary>
sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
