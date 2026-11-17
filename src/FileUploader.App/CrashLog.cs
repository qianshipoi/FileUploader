using System.Text;

namespace FileUploader.App;

/// <summary>把未处理异常写到 <c>%APPDATA%\FileUploader\crash.log</c>，方便排查闪退。</summary>
internal static class CrashLog
{
    private static readonly object Gate = new();

    /// <summary>日志路径（便携模式下跟随 data 目录）。</summary>
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FileUploader",
        "crash.log");

    /// <summary>挂上全局未处理异常钩子（进程级 + 任务级）。</summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>记录一条异常。</summary>
    public static void Write(string source, Exception? exception)
    {
        try
        {
            var builder = new StringBuilder();
            builder.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}");
            builder.AppendLine(exception?.ToString() ?? "(no exception object)");

            lock (Gate)
            {
                var directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(Path, builder.ToString() + Environment.NewLine);
            }
        }
        catch (Exception)
        {
            // 记不下来就算了，别再抛。
        }
    }
}
