using System.Reflection;

namespace FileUploader.App;

/// <summary>
/// 当前程序版本号。
/// 来源是 csproj 的 <c>&lt;Version&gt;</c>（本地构建）或发版流水线的 <c>-p:Version=&lt;tag&gt;</c>（Release 构建），
/// 所以设置页显示的版本号和 GitHub 上的 tag 是一一对应的。
/// </summary>
internal static class AppVersion
{
    /// <summary>形如 <c>0.2.0</c> 或 <c>0.3.0-beta.1</c>。</summary>
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var assembly = typeof(AppVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // SDK / SourceLink 会在后面挂 "+<commit>"，版本比较用不到，去掉。
            var plus = informational.IndexOf('+');
            return (plus > 0 ? informational[..plus] : informational).Trim();
        }

        var version = assembly.GetName().Version;
        return version is null ? "0.0.0" : version.ToString(3);
    }
}
