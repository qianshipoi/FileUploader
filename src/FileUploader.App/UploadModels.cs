using FileUploader.Core;

namespace FileUploader.App;

/// <summary>单个上传任务的状态。</summary>
public enum UploadState
{
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
    DateTimeOffset StartedAt)
{
    public string SizeText => FormatSize(Size);

    public string StateText => State switch
    {
        UploadState.Queued => "等待上传",
        UploadState.Uploading => $"上传中 {Progress:0}%",
        UploadState.Succeeded => "已上传",
        UploadState.Failed => "失败",
        UploadState.Canceled => "已取消",
        _ => "",
    };

    public bool IsFinished => State is UploadState.Succeeded or UploadState.Failed or UploadState.Canceled;

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

/// <summary>拖入或选择的一个待上传文件。</summary>
public sealed record DroppedFile(string LocalPath, string FileName, string? RelativeDirectory, long Size);

/// <summary>界面顶部提示条的内容。</summary>
public sealed record Notice(string Message, bool IsError)
{
    public static Notice Info(string message) => new(message, false);

    public static Notice Error(string message) => new(message, true);
}
