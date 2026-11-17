namespace FileUploader.Core;

/// <summary>按扩展名推断 Content-Type，用于让浏览器直接预览而不是下载。</summary>
public static class ContentTypes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".jfif"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".ico"] = "image/x-icon",
        [".svg"] = "image/svg+xml",
        [".avif"] = "image/avif",
        [".heic"] = "image/heic",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
        [".txt"] = "text/plain; charset=utf-8",
        [".md"] = "text/markdown; charset=utf-8",
        [".log"] = "text/plain; charset=utf-8",
        [".csv"] = "text/csv; charset=utf-8",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "application/javascript",
        [".pdf"] = "application/pdf",
        [".zip"] = "application/zip",
        [".rar"] = "application/vnd.rar",
        [".7z"] = "application/x-7z-compressed",
        [".gz"] = "application/gzip",
        [".tar"] = "application/x-tar",
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".webm"] = "video/webm",
        [".mkv"] = "video/x-matroska",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".flac"] = "audio/flac",
        [".aac"] = "audio/aac",
        [".ogg"] = "audio/ogg",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".exe"] = "application/octet-stream",
    };

    /// <summary>推断 Content-Type，未知时返回 <c>application/octet-stream</c>。</summary>
    public static string ForFileName(string fileName) =>
        Map.TryGetValue(Path.GetExtension(fileName), out var type) ? type : "application/octet-stream";
}
