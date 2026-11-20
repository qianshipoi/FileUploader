using Microsoft.UI.Reactor;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FileUploader.App;

/// <summary>
/// 主题画刷的安全取法。
/// <para>
/// 踩过的坑：<c>ThemeResource.Brush(key)</c> 在找不到资源时是**抛异常**（KeyNotFoundException），
/// 不是返回 null —— 所以原来那种 <c>ThemeResource.Brush(k) ?? 兜底</c> 的写法根本走不到兜底。
/// 而它一旦在 Render 里抛出，整个窗口就只剩 Reactor 的红字错误页（用户实测：下载的 0.2.0 一打开
/// 就是 "Theme resource 'ControlStrokeColorDefaultBrush' not found or is not a Brush"）。
/// </para>
/// <para>
/// 资源缺失通常来自高对比度模式 / 精简过的主题字典，属于环境差异，不该让程序打不开。
/// 这里逐级尝试多个 key，全都取不到就退回固定颜色；取不到时会写一次 crash.log 便于回查。
/// </para>
/// </summary>
internal static class ThemeBrushes
{
    private static readonly HashSet<string> ReportedMissing = new(StringComparer.Ordinal);

    private static readonly Brush NeutralStrokeFallback = new SolidColorBrush(Color.FromArgb(0x66, 0x86, 0x86, 0x86));
    private static readonly Brush AccentFallback = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x5F, 0xB8));
    private static readonly Brush SecondaryTextFallback = new SolidColorBrush(Color.FromArgb(0xFF, 0x6B, 0x6B, 0x6B));

    private static readonly Brush PageBackgroundFallback = new SolidColorBrush(Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3));

    /// <summary>拖拽区的虚线描边：强调色（悬停/拖拽中）或中性描边色。</summary>
    public static Brush Stroke(bool accent) => accent
        ? TryGetFirst("AccentFillColorDefaultBrush", "SystemControlHighlightAccentBrush") ?? AccentFallback
        : TryGetFirst("ControlStrokeColorDefaultBrush", "CardStrokeColorDefaultBrush", "TextFillColorDisabledBrush")
          ?? NeutralStrokeFallback;

    /// <summary>图标前景色：强调色或次要文字色。</summary>
    public static Brush IconForeground(bool accent) => accent
        ? TryGetFirst("AccentTextFillColorPrimaryBrush", "SystemControlHighlightAccentBrush") ?? AccentFallback
        : TryGetFirst("TextFillColorSecondaryBrush", "TextFillColorPrimaryBrush") ?? SecondaryTextFallback;

    /// <summary>次要文字色（TestPulseIcon 的折线与底图）。</summary>
    public static Brush SecondaryText =>
        TryGetFirst("TextFillColorSecondaryBrush", "TextFillColorPrimaryBrush") ?? SecondaryTextFallback;

    /// <summary>
    /// 设置页的页面底色：比卡片略深一点，白色卡片才有层次（深色主题下跟着变深）。
    /// 取不到时退回 Windows 浅色主题的 #F3F3F3。
    /// </summary>
    public static Brush PageBackground =>
        TryGetFirst("SolidBackgroundFillColorBaseBrush", "LayerFillColorDefaultBrush", "ApplicationPageBackgroundThemeBrush")
        ?? PageBackgroundFallback;

    /// <summary>强调色（TestPulseIcon 的扫描亮线）。</summary>
    public static Brush Accent =>
        TryGetFirst("AccentTextFillColorPrimaryBrush", "SystemControlHighlightAccentBrush") ?? AccentFallback;

    /// <summary>按顺序尝试若干主题资源 key，返回第一个取到的画刷；全都取不到返回 null。</summary>
    private static Brush? TryGetFirst(params string[] keys)
    {
        foreach (var key in keys)
        {
            var brush = TryGet(key);
            if (brush is not null)
            {
                return brush;
            }
        }

        return null;
    }

    private static Brush? TryGet(string key)
    {
        try
        {
            return ThemeResource.Brush(key);
        }
        catch (Exception ex)
        {
            // 每个 key 只记一次，避免每帧刷日志。
            if (ReportedMissing.Add(key))
            {
                CrashLog.Write($"主题资源取不到，已用兜底颜色：{key}", ex);
            }

            return null;
        }
    }
}
