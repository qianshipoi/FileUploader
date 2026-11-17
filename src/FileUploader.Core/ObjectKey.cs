using System.Text;

namespace FileUploader.Core;

/// <summary>
/// 对象键（Object Key）与 URL 编码相关的纯函数。
/// </summary>
public static class ObjectKey
{
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// 生成对象键：<c>prefix/yyyy/MM/dd/相对路径/文件名</c>。
    /// </summary>
    /// <param name="settings">当前配置（使用前缀与日期分组开关）。</param>
    /// <param name="fileName">文件名（不含目录）。</param>
    /// <param name="relativeDirectory">拖入文件夹时的相对子目录，可为 null。</param>
    /// <param name="timestamp">日期分组使用的时间。</param>
    public static string Build(AppSettings settings, string fileName, string? relativeDirectory, DateTimeOffset timestamp)
    {
        var parts = new List<string>();

        var prefix = NormalizePrefix(settings.KeyPrefix);
        if (prefix.Length > 0)
        {
            parts.Add(prefix);
        }

        if (settings.UseDateFolder)
        {
            parts.Add(timestamp.ToString("yyyy/MM/dd"));
        }

        if (!string.IsNullOrWhiteSpace(relativeDirectory))
        {
            foreach (var segment in relativeDirectory.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                parts.Add(SanitizeSegment(segment));
            }
        }

        parts.Add(SanitizeSegment(fileName));
        return string.Join('/', parts);
    }

    /// <summary>去掉首尾斜杠，并把中间的反斜杠统一为斜杠（返回结果不带结尾斜杠）。</summary>
    public static string NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return string.Empty;
        }

        var trimmed = prefix.Trim().Replace('\\', '/').Trim('/');
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizeSegment);
        return string.Join('/', segments);
    }

    /// <summary>清理单个路径片段中的非法字符。</summary>
    public static string SanitizeSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return "unnamed";
        }

        var builder = new StringBuilder(segment.Length);
        foreach (var ch in segment.Trim())
        {
            builder.Append(Array.IndexOf(InvalidNameChars, ch) >= 0 || ch == '/' || ch == '\\' ? '_' : ch);
        }

        var value = builder.ToString().Trim();
        return value.Length == 0 ? "unnamed" : value;
    }

    /// <summary>
    /// 按 RFC 3986 编码。斜杠会被保留，因此可以直接对整段对象键使用。
    /// </summary>
    public static string EncodePath(string path) => Encode(path, keepSlash: true);

    /// <summary>按 RFC 3986 编码（不含斜杠的特殊编码，用于查询参数）。</summary>
    public static string EncodeComponent(string value) => Encode(value, keepSlash: false);

    private static string Encode(string value, bool keepSlash)
    {
        var builder = new StringBuilder(value.Length + 16);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var isUnreserved = (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9')
                || b == '-' || b == '_' || b == '.' || b == '~';
            if (isUnreserved)
            {
                builder.Append((char)b);
            }
            else if (keepSlash && b == '/')
            {
                builder.Append('/');
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2"));
            }
        }

        return builder.ToString();
    }
}
