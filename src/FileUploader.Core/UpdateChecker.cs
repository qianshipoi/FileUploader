using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FileUploader.Core.Storage;

namespace FileUploader.Core;

/// <summary>一次「检查更新」的结果。</summary>
public enum UpdateCheckStatus
{
    /// <summary>已经是最新版本。</summary>
    UpToDate,

    /// <summary>有更新可用。</summary>
    UpdateAvailable,

    /// <summary>没查成（网络、限流、还没有发布版本…）。</summary>
    Failed,
}

/// <summary>
/// 检查更新的结果。<see cref="Changes"/> 是 Release 说明里「本次变更」那一小节的条目（已去掉 <c>- </c> 前缀），
/// 界面上直接列出来就是"更新了哪些地方"。
/// </summary>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string CurrentVersion,
    string? LatestVersion = null,
    string? TagName = null,
    IReadOnlyList<string>? Changes = null,
    string? ReleaseNotes = null,
    string? ReleasePageUrl = null,
    string? AssetName = null,
    string? AssetUrl = null,
    string? AssetSha256 = null,
    long AssetSize = 0,
    DateTimeOffset? PublishedAt = null,
    string? Error = null)
{
    public bool HasUpdate => Status == UpdateCheckStatus.UpdateAvailable;

    public static UpdateCheckResult Fail(string currentVersion, string error) =>
        new(UpdateCheckStatus.Failed, currentVersion, Error: error);
}

/// <summary>
/// 用 GitHub Release 做更新检查：读 <c>/releases/latest</c>，比较版本号，取「本次变更」清单与 win-x64 资产。
/// 只做"查"和"解析"，下载与替换见 <c>Updater</c>（App 层）。
/// </summary>
public static class UpdateChecker
{
    /// <summary>仓库地址（改仓库时只改这里）。</summary>
    public const string RepositorySlug = "qianshipoi/FileUploader";

    /// <summary>单文件便携版的资产后缀。</summary>
    public const string PortableAssetSuffix = "-win-x64.exe";

    private static readonly Regex Sha256Regex = new(
        @"SHA256\s*\((?<name>[^)]*)\)\s*=\s*(?<hash>[0-9A-Fa-f]{64})",
        RegexOptions.Compiled);

    /// <summary>GitHub 最新 release 的 API 地址。</summary>
    public static Uri LatestReleaseApi { get; } = new($"https://api.github.com/repos/{RepositorySlug}/releases/latest");

    /// <summary>发布页（没有 API 数据时的兜底链接）。</summary>
    public static string ReleasesPageUrl => $"https://github.com/{RepositorySlug}/releases";

    /// <summary>
    /// 测试/内网用的 feed 覆盖：环境变量 <c>FU_UPDATE_FEED</c> 指向一份与 GitHub release 同形状的 JSON。
    /// 回归脚本靠它跑真下载 + 真替换，不用真的发一个版本。
    /// </summary>
    public static Uri? FeedOverride
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("FU_UPDATE_FEED");
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ? uri : null;
        }
    }

    /// <summary>查一次最新版本。</summary>
    public static async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        var feed = FeedOverride ?? LatestReleaseApi;
        using var request = new HttpRequestMessage(HttpMethod.Get, feed);
        request.Headers.TryAddWithoutValidation("User-Agent", $"FileUploader/{currentVersion}");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

        try
        {
            using var response = await StorageHttp.Client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Fail(currentVersion, DescribeHttpFailure((int)response.StatusCode));
            }

            return Parse(currentVersion, payload);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return UpdateCheckResult.Fail(currentVersion, "检查更新超时了，请检查网络或代理设置。");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return UpdateCheckResult.Fail(currentVersion, $"连不上 GitHub：{ex.Message}");
        }
        catch (JsonException ex)
        {
            return UpdateCheckResult.Fail(currentVersion, $"返回内容看不懂：{ex.Message}");
        }
    }

    /// <summary>把 GitHub 的 release JSON 解析成结果（单独拆出来便于离线自测）。</summary>
    public static UpdateCheckResult Parse(string currentVersion, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        var tag = GetString(root, "tag_name");
        var latest = NormalizeVersion(tag);
        if (string.IsNullOrEmpty(latest))
        {
            return UpdateCheckResult.Fail(currentVersion, "Release 里没有版本号（tag_name 为空）。");
        }

        string? assetName = null;
        string? assetUrl = null;
        string? assetSha = null;
        long assetSize = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = GetString(asset, "name");
                if (name is null || !name.EndsWith(PortableAssetSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                assetName = name;
                assetUrl = GetString(asset, "browser_download_url");
                assetSha = NormalizeDigest(GetString(asset, "digest"));
                assetSize = asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0;
                break;
            }
        }

        var notes = GetString(root, "body");
        // 资产没带 digest（旧接口）时退回 Release 说明里写的 SHA256。
        assetSha ??= FindSha256(notes, assetName);

        DateTimeOffset? publishedAt = DateTimeOffset.TryParse(GetString(root, "published_at"), out var published)
            ? published
            : null;

        return new UpdateCheckResult(
            IsNewer(latest, currentVersion) ? UpdateCheckStatus.UpdateAvailable : UpdateCheckStatus.UpToDate,
            currentVersion,
            latest,
            tag,
            ExtractChanges(notes),
            notes,
            GetString(root, "html_url") ?? ReleasesPageUrl,
            assetName,
            assetUrl,
            assetSha,
            assetSize,
            publishedAt);
    }

    /// <summary>从 Release 说明里抽「本次变更」小节：匹配标题含"变更"的章节，收集其下的列表项。</summary>
    public static IReadOnlyList<string> ExtractChanges(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return Array.Empty<string>();
        }

        var changes = new List<string>();
        var inSection = false;
        foreach (var raw in notes.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var isHeading = line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("### ", StringComparison.Ordinal);
            if (isHeading)
            {
                if (inSection)
                {
                    break;   // 下一节了，收工
                }

                inSection = line.Contains("变更", StringComparison.Ordinal) || line.Contains("更新内容", StringComparison.Ordinal);
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                changes.Add(trimmed[2..].Trim());
            }
        }

        return changes;
    }

    /// <summary>从说明里找资产的 SHA256（优先匹配资产名，找不到就用第一个）。</summary>
    public static string? FindSha256(string? notes, string? assetName)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        string? fallback = null;
        foreach (Match match in Sha256Regex.Matches(notes))
        {
            var hash = match.Groups["hash"].Value.ToLowerInvariant();
            var name = match.Groups["name"].Value.Trim();
            if (!string.IsNullOrEmpty(assetName) && string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
            {
                return hash;
            }

            fallback ??= hash;
        }

        return fallback;
    }

    /// <summary>去掉 v 前缀与 <c>+commit</c> 元数据。</summary>
    public static string NormalizeVersion(string? version)
    {
        var value = (version ?? string.Empty).Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var plus = value.IndexOf('+');
        return plus >= 0 ? value[..plus].Trim() : value;
    }

    /// <summary>
    /// <paramref name="latest"/> 是否比 <paramref name="current"/> 新。
    /// 数字段逐段比；数字相同则正式版 &gt; 预发布版；都带预发布标记时按字符串比较（beta.2 &gt; beta.1）。
    /// </summary>
    public static bool IsNewer(string? latest, string? current)
    {
        if (!TryParseVersion(latest, out var candidate) || !TryParseVersion(current, out var baseline))
        {
            return false;
        }

        for (var i = 0; i < candidate.Numbers.Length; i++)
        {
            if (candidate.Numbers[i] != baseline.Numbers[i])
            {
                return candidate.Numbers[i] > baseline.Numbers[i];
            }
        }

        if (candidate.Pre is null && baseline.Pre is null)
        {
            return false;
        }

        if (candidate.Pre is null)
        {
            return true;
        }

        return baseline.Pre is not null && ComparePrerelease(candidate.Pre, baseline.Pre) > 0;
    }

    /// <summary>
    /// 预发布段比较：逐段比，数字段按数值（避免 beta.10 &lt; beta.9 的字典序陷阱），
    /// 段少的小于段多的（1.0.0-alpha &lt; 1.0.0-alpha.1）。
    /// </summary>
    private static int ComparePrerelease(string candidate, string baseline)
    {
        var left = candidate.Split('.');
        var right = baseline.Split('.');
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            if (i >= left.Length)
            {
                return -1;
            }

            if (i >= right.Length)
            {
                return 1;
            }

            if (int.TryParse(left[i], out var leftNumber) && int.TryParse(right[i], out var rightNumber))
            {
                if (leftNumber != rightNumber)
                {
                    return leftNumber.CompareTo(rightNumber);
                }

                continue;
            }

            var comparison = string.CompareOrdinal(left[i], right[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static bool TryParseVersion(string? version, out (int[] Numbers, string? Pre) parsed)
    {
        parsed = (new int[4], null);
        var value = NormalizeVersion(version);
        if (value.Length == 0)
        {
            return false;
        }

        var dash = value.IndexOf('-');
        var numericPart = dash >= 0 ? value[..dash] : value;
        var pre = dash >= 0 ? value[(dash + 1)..] : null;

        var pieces = numericPart.Split('.');
        if (pieces.Length == 0 || pieces.Length > 4)
        {
            return false;
        }

        var numbers = new int[4];
        for (var i = 0; i < pieces.Length; i++)
        {
            if (!int.TryParse(pieces[i], out var number) || number < 0)
            {
                return false;
            }

            numbers[i] = number;
        }

        parsed = (numbers, string.IsNullOrWhiteSpace(pre) ? null : pre);
        return true;
    }

    private static string? NormalizeDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        var value = digest.Trim();
        if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["sha256:".Length..];
        }

        value = value.ToLowerInvariant();
        return value.Length == 64 && value.All(Uri.IsHexDigit) ? value : null;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string DescribeHttpFailure(int statusCode) => statusCode switch
    {
        404 => "仓库还没有发布任何 Release。",
        403 or 429 => "GitHub API 限流了（未登录每小时 60 次），过一会儿再试。",
        _ => $"GitHub 返回 HTTP {statusCode}（{(HttpStatusCode)statusCode}）。",
    };
}
