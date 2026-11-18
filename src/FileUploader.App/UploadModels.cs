using FileUploader.Core;

namespace FileUploader.App;

/// <summary>单个上传任务的状态。</summary>
public enum UploadState
{
    /// <summary>正在把多个文件打成一个 zip（还没有开始上传）。</summary>
    Packing,
    Queued,
    Uploading,
    Succeeded,
    Failed,
    Canceled,
}

/// <summary>上传列表中的一行。</summary>
public sealed record UploadItem(
    Guid Id,
    string FileName,
    string LocalPath,
    long Size,
    string Key,
    UploadState State,
    double Progress,
    string? Url,
    string? Error,
    DateTimeOffset StartedAt,
    bool IsTemporary = false)
{
    public string SizeText => FormatSize(Size);

    public string StateText => State switch
    {
        UploadState.Packing => $"打包中 {Progress:0}%",
        UploadState.Queued => "等待上传",
        UploadState.Uploading => $"上传中 {Progress:0}%",
        UploadState.Succeeded => "已上传",
        UploadState.Failed => "失败",
        UploadState.Canceled => "已取消",
        _ => "",
    };

    public bool IsFinished => State is UploadState.Succeeded or UploadState.Failed or UploadState.Canceled;

    /// <summary>还在处理中（打包 / 排队 / 上传），可以取消。</summary>
    public bool IsActive => State is UploadState.Packing or UploadState.Queued or UploadState.Uploading;

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes;
        var index = -1;
        do
        {
            value /= 1024;
            index++;
        }
        while (value >= 1024 && index < units.Length - 1);

        return $"{value:0.#} {units[index]}";
    }
}

/// <summary>
/// 拖入或选择的一个待上传文件。
/// <paramref name="RootName"/> 是它所属的顶层文件夹名（散落的单个文件为 null）：
/// 打包成 zip 时用它当压缩包内的一级目录。
/// <paramref name="IsTemporary"/> 表示这是程序自己生成的中间文件（临时 zip），用完要删。
/// </summary>
public sealed record DroppedFile(
    string LocalPath,
    string FileName,
    string? RelativeDirectory,
    long Size,
    string? RootName = null,
    bool IsTemporary = false);

/// <summary>界面顶部提示条的内容。</summary>
public sealed record Notice(string Message, bool IsError)
{
    public static Notice Info(string message) => new(message, false);

    public static Notice Error(string message) => new(message, true);
}
