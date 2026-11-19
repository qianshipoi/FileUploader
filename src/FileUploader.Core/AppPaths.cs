namespace FileUploader.Core;

/// <summary>
/// 数据和配置的存放位置。
/// <para>
/// 默认放在 <c>%APPDATA%\FileUploader</c>；如果 exe 同目录存在一个名为 <c>portable.txt</c>
/// 的文件，则改为“便携模式”，配置与历史都写在 exe 旁边的 <c>data</c> 子目录里，
/// 整个文件夹拷走即可换机器使用。
/// </para>
/// </summary>
public static class AppPaths
{
    private const string PortableMarker = "portable.txt";

    static AppPaths()
    {
        // 单文件发布时 AppContext.BaseDirectory 可能是解压目录，优先用真实 exe 路径。
        ExeDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        IsPortable = File.Exists(Path.Combine(ExeDirectory, PortableMarker));
        DataDirectory = IsPortable
            ? Path.Combine(ExeDirectory, "data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileUploader");
    }

    /// <summary>本进程 exe 所在目录（单文件发布时是真实 exe 的目录，不是解压目录）。</summary>
    public static string ExeDirectory { get; }

    /// <summary>是否处于便携模式（exe 旁边有 portable.txt）。</summary>
    public static bool IsPortable { get; }

    /// <summary>配置与历史所在的目录。</summary>
    public static string DataDirectory { get; }

    /// <summary>配置文件路径。</summary>
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    /// <summary>上传历史文件路径。</summary>
    public static string HistoryPath => Path.Combine(DataDirectory, "history.json");

    /// <summary>
    /// 未完成的分片上传会话目录（断点续传用）：每个对象键一个 json，
    /// 记着 uploadId 与已传完的分片；传完或放弃时删掉。
    /// </summary>
    public static string UploadsDirectory => Path.Combine(DataDirectory, "uploads");
}
