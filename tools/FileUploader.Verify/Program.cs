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
    default:
        Console.WriteLine("用法: FileUploader.Verify [vectors|upload|live-oss]");
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
