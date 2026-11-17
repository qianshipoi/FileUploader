using System.Web;
using Aliyun.OSS;
using FileUploader.Core;
using FileUploader.Core.Storage;

// 用阿里云官方 SDK 生成预签名 URL，与本项目的实现逐字段对比。
const string Bucket = "demo";
const string Key = "images/a.png";
const string Ak = "testak";
const string Sk = "testsecret";

var expiresAt = DateTimeOffset.FromUnixTimeSeconds(1893456000);
var client = new OssClient("https://oss-cn-hangzhou.aliyuncs.com", Ak, Sk);
var sdkUri = client.GeneratePresignedUri(Bucket, Key, expiresAt.UtcDateTime, SignHttpMethod.Get);

var settings = new AppSettings
{
    Protocol = StorageProtocol.AliyunOss,
    Endpoint = "oss-cn-hangzhou.aliyuncs.com",
    Bucket = Bucket,
    AccessKeyId = Ak,
    AccessKeySecret = Sk,
    UrlMode = UrlMode.Signed,
    SignedUrlExpireDays = 7,
};

var mineUrl = ObjectUrlBuilder.SignedUrl(settings, Key, expiresAt.AddDays(-7));

Console.WriteLine($"sdk : {sdkUri}");
Console.WriteLine($"mine: {mineUrl}");

static Dictionary<string, string> ParseQuery(string url)
{
    var query = new Uri(url).Query.TrimStart('?');
    return query
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2))
        .ToDictionary(p => p[0], p => p.Length > 1 ? HttpUtility.UrlDecode(p[1]) : string.Empty);
}

var sdk = ParseQuery(sdkUri.ToString());
var del = ParseQuery(mineUrl);

var failures = 0;
void Check(string name, string? a, string? b)
{
    var ok = string.Equals(a, b, StringComparison.Ordinal);
    if (!ok)
    {
        failures++;
    }

    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}\n      sdk={a}\n     mine={b}");
}

Check("Expires", sdk.GetValueOrDefault("Expires"), del.GetValueOrDefault("Expires"));
Check("OSSAccessKeyId", sdk.GetValueOrDefault("OSSAccessKeyId"), del.GetValueOrDefault("OSSAccessKeyId"));
Check("Signature", sdk.GetValueOrDefault("Signature"), del.GetValueOrDefault("Signature"));
Check("路径", new Uri(sdkUri.ToString()).AbsolutePath, new Uri(mineUrl).AbsolutePath);

Console.WriteLine(failures == 0 ? "ALL PASS（与官方 SDK 完全一致）" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
