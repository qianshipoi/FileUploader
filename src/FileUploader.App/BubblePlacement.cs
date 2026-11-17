using System.Runtime.InteropServices;
using System.Text.Json;
using FileUploader.Core;

namespace FileUploader.App;

/// <summary>
/// 记录简洁模式气泡停在哪（<c>%APPDATA%\FileUploader\bubble.json</c>，便携模式在 <c>data\</c> 下）。
/// <para>
/// 只有"气泡至少还有一半面积落在某个显示器的工作区里"时才复用上次位置；
/// 换了显示器/分辨率、或者被拖到屏幕外，就回落到默认的右下角。
/// </para>
/// </summary>
internal static class BubblePlacement
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>位置文件路径。</summary>
    public static string FilePath => Path.Combine(AppPaths.DataDirectory, "bubble.json");

    /// <summary>读取上次的位置；没有或损坏返回 <c>null</c>。</summary>
    public static (int X, int Y)? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(FilePath), JsonOptions);
            return dto is null ? null : (dto.X, dto.Y);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>记住当前位置（失败不影响使用）。</summary>
    public static void Save(int x, int y)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Dto { X = x, Y = y }, JsonOptions));
        }
        catch (Exception)
        {
            // 位置记不下来不算错误。
        }
    }

    /// <summary>
    /// 气泡是否还"在屏幕上"：至少一半面积落在某个显示器的工作区里。
    /// </summary>
    public static bool IsMostlyVisible(int x, int y, int width, int height)
    {
        var area = (long)width * height;
        if (area <= 0)
        {
            return false;
        }

        var best = 0L;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var left = Math.Max(x, info.Work.Left);
                var top = Math.Max(y, info.Work.Top);
                var right = Math.Min(x + width, info.Work.Right);
                var bottom = Math.Min(y + height, info.Work.Bottom);
                if (right > left && bottom > top)
                {
                    best = Math.Max(best, (long)(right - left) * (bottom - top));
                }
            }

            return true;
        }, IntPtr.Zero);

        return best * 2 >= area;
    }

    private sealed class Dto
    {
        public int X { get; set; }

        public int Y { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public uint Flags;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
