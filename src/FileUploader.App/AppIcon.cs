using FileUploader.Core;
using Microsoft.UI.Reactor;

namespace FileUploader.App;

/// <summary>
/// 应用图标。exe 内嵌图标由 csproj 的 <c>ApplicationIcon</c> 编进 PE 资源（资源管理器、任务栏、快捷方式都读它）；
/// 这里负责运行时给窗口设置同一枚图标（标题栏、Alt+Tab、任务栏分组图标）。
/// 托盘图标不用这个文件，而是运行时按同一套设计画出来（见 <see cref="AppWindows"/>），
/// 因为托盘走的是原生 HICON 通道，不吃 .ico 容器。
/// </summary>
internal static class AppIcon
{
    /// <summary>内嵌资源名（csproj 里 <c>LogicalName</c> 指定）。</summary>
    private const string ResourceName = "app.ico";

    /// <summary>随输出目录复制的图标文件路径。</summary>
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");

    /// <summary>
    /// 加载窗口图标；拿不到图标时返回 <c>null</c>，让 Reactor 用默认图标，不影响启动。
    /// </summary>
    public static WindowIcon? Load()
    {
        try
        {
            var path = ResolvePath();
            return string.IsNullOrEmpty(path) ? null : WindowIcon.FromPath(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 优先用 exe 旁边的 <c>Assets\app.ico</c>；单文件便携版没有这个目录，
    /// 就把它从内嵌资源释放到数据目录再按路径加载，避免窗口图标退化成空白。
    /// </summary>
    private static string? ResolvePath()
    {
        var beside = FilePath;
        if (File.Exists(beside))
        {
            return beside;
        }

        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var cached = Path.Combine(AppPaths.DataDirectory, "app.ico");
            using var file = File.Create(cached);
            stream.CopyTo(file);
            return cached;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
