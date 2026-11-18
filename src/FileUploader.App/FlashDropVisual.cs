using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FileUploader.App;

/// <summary>
/// 「拖拽浮现」气泡的画法。整体走 Fluent 的克制路线：一层很淡的柔和投影 + 发丝描边、
/// 主标题 + 次要说明、细进度条，图标用和应用一致的「箭头 + 底线」线条，不再画彩色方块。
/// </summary>
internal static class FlashDropVisual
{
    /// <summary>气泡高度（DIP）。</summary>
    public const double PillHeight = 60;

    /// <summary>圆角（DIP）。</summary>
    public const double Radius = 16;

    /// <summary>投影留白（DIP）：只给阴影用，不参与气泡本体。</summary>
    public const double ShadowPadding = 10;

    /// <summary>最小气泡宽度（DIP）。</summary>
    public const double MinPillWidth = 196;

    /// <summary>
    /// 落点区域比气泡本体多出来的宽度 / 高度（DIP），向左、向上扩展。
    /// 拖拽时整个窗口都是落点，所以文件拖到气泡左上方一点也能落进来，不用精准瞄准。
    /// </summary>
    public const double ZonePadX = 56;

    public const double ZonePadY = 40;

    private const double MaxPillWidth = 380;
    private const double PadLeft = 16;
    private const double PadRight = 20;
    private const double GlyphWidth = 22;
    private const double GlyphGap = 12;

    /// <summary>图标语义。</summary>
    public enum IconKind
    {
        Upload = 0,
        Check = 1,
        Alert = 2,
    }

    /// <summary>当前是否浅色主题（读不到就当浅色）。</summary>
    public static bool IsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// 气泡的强调色。固定用应用自己的蓝（和托盘图标、应用图标同一套品牌色），
    /// 不跟随系统强调色 —— 否则用户把强调色改成绿色时，气泡会跟 App 不是一个色系。
    /// </summary>
    public static Color AccentColor(bool light) =>
        light ? Color.FromArgb(0x0F, 0x6C, 0xBD) : Color.FromArgb(0x6C, 0xB8, 0xFF);

    /// <summary>按状态给文案。</summary>
    public static (string Title, string Subtitle, IconKind Icon) Texts(
        FlashDropState state, bool configured, int count, int percent, string resultMessage, string? doneSubtitle = null)
    {
        return state switch
        {
            FlashDropState.Peek => configured
                ? ("拖到这里上传", string.Empty, IconKind.Upload)
                : ("还没配置存储", string.Empty, IconKind.Alert),
            FlashDropState.Armed => ("松手即上传", $"{Math.Max(1, count)} 个文件", IconKind.Upload),
            FlashDropState.Uploading => ($"正在上传 {Math.Clamp(percent, 0, 100)}%", $"{Math.Max(1, count)} 个文件", IconKind.Upload),
            FlashDropState.Done => (resultMessage.Length > 0 ? resultMessage : "已复制链接", doneSubtitle ?? "拖入即上传", IconKind.Check),
            FlashDropState.Failed => (resultMessage.Length > 0 ? resultMessage : "上传失败", "点一下看详情", IconKind.Alert),
            _ => (string.Empty, string.Empty, IconKind.Upload),
        };
    }

    /// <summary>按文案量出气泡宽度（DIP）。</summary>
    public static double MeasureWidth(float scale, string title, string subtitle)
    {
        using var bitmap = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        using var titleFont = CreateTitleFont(scale);
        using var subtitleFont = CreateSubtitleFont(scale);
        using var format = new StringFormat(StringFormat.GenericDefault) { FormatFlags = StringFormatFlags.NoWrap };

        var titleWidth = string.IsNullOrEmpty(title) ? 0f : graphics.MeasureString(title, titleFont, int.MaxValue, format).Width;
        var subtitleWidth = string.IsNullOrEmpty(subtitle) ? 0f : graphics.MeasureString(subtitle, subtitleFont, int.MaxValue, format).Width;
        var textWidth = Math.Max(titleWidth, subtitleWidth) / scale;

        var width = PadLeft + GlyphWidth + GlyphGap + textWidth + PadRight + 6;
        return Math.Clamp(width, MinPillWidth, MaxPillWidth);
    }

    private static Font CreateTitleFont(float scale) =>
        new("Microsoft YaHei UI", 13.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);

    private static Font CreateSubtitleFont(float scale) =>
        new("Microsoft YaHei UI", 11f * scale, FontStyle.Regular, GraphicsUnit.Pixel);

    // ── 投影 ────────────────────────────────────────────────────────────

    private static class ShadowCache
    {
        public static Bitmap? Bitmap;
        public static (int Width, int Height, int X, int Y, int W, int H, bool Light) Key = (-1, -1, -1, -1, -1, -1, false);
    }

    /// <summary>很淡的一层投影：模糊半径小、峰值低，只是让气泡「浮」起来一点点。</summary>
    public static void DrawShadow(Graphics graphics, RectangleF pill, float scale, bool light, Size bitmapSize)
    {
        var key = (bitmapSize.Width, bitmapSize.Height, (int)pill.X, (int)pill.Y, (int)pill.Width, (int)pill.Height, light);
        if (ShadowCache.Key != key || ShadowCache.Bitmap is null)
        {
            ShadowCache.Bitmap?.Dispose();
            ShadowCache.Bitmap = BuildShadow(bitmapSize, pill, scale, light);
            ShadowCache.Key = key;
        }

        graphics.DrawImageUnscaled(ShadowCache.Bitmap, 0, 0);
    }

    private static Bitmap BuildShadow(Size size, RectangleF pill, float scale, bool light)
    {
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        var width = size.Width;
        var height = size.Height;
        var mask = new float[width * height];

        // 1) 圆角矩形写进掩码（整体下移 1.5px，模拟光从上方来）。
        using (var shape = new Bitmap(width, height, PixelFormat.Format32bppPArgb))
        {
            using (var graphics = Graphics.FromImage(shape))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                using var path = RoundedRect(new RectangleF(pill.X, pill.Y + (1.5f * scale), pill.Width, pill.Height), (float)(Radius * scale));
                using var brush = new SolidBrush(Color.White);
                graphics.FillPath(brush, path);
            }

            var data = shape.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var buffer = new byte[data.Stride * height];
                Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
                for (var y = 0; y < height; y++)
                {
                    var row = y * data.Stride;
                    for (var x = 0; x < width; x++)
                    {
                        mask[(y * width) + x] = buffer[row + (x * 4) + 3] / 255f;
                    }
                }
            }
            finally
            {
                shape.UnlockBits(data);
            }
        }

        // 2) 两趟盒式模糊 ≈ 高斯。
        var radius = Math.Max(2, (int)Math.Round(5f * scale));
        BoxBlur(mask, width, height, radius);
        BoxBlur(mask, width, height, radius);

        var strength = light ? 0.26f : 0.40f;
        var output = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var buffer = new byte[output.Stride * height];
            for (var y = 0; y < height; y++)
            {
                var row = y * output.Stride;
                for (var x = 0; x < width; x++)
                {
                    var alpha = Math.Clamp(mask[(y * width) + x] * strength, 0f, 1f);
                    if (alpha < 0.004f)
                    {
                        continue;
                    }

                    // 预乘黑：RGB 全 0，只写 alpha。
                    buffer[row + (x * 4) + 3] = (byte)(alpha * 255);
                }
            }

            Marshal.Copy(buffer, 0, output.Scan0, buffer.Length);
        }
        finally
        {
            bitmap.UnlockBits(output);
        }

        return bitmap;
    }

    private static void BoxBlur(float[] source, int width, int height, int radius)
    {
        var temp = new float[source.Length];
        var window = (radius * 2) + 1;

        for (var y = 0; y < height; y++)
        {
            var sum = 0f;
            var row = y * width;
            for (var i = -radius; i <= radius; i++)
            {
                sum += source[row + Math.Clamp(i, 0, width - 1)];
            }

            for (var x = 0; x < width; x++)
            {
                temp[row + x] = sum / window;
                sum -= source[row + Math.Clamp(x - radius, 0, width - 1)];
                sum += source[row + Math.Clamp(x + radius + 1, 0, width - 1)];
            }
        }

        for (var x = 0; x < width; x++)
        {
            var sum = 0f;
            for (var i = -radius; i <= radius; i++)
            {
                sum += temp[(Math.Clamp(i, 0, height - 1) * width) + x];
            }

            for (var y = 0; y < height; y++)
            {
                source[(y * width) + x] = sum / window;
                sum -= temp[(Math.Clamp(y - radius, 0, height - 1) * width) + x];
                sum += temp[(Math.Clamp(y + radius + 1, 0, height - 1) * width) + x];
            }
        }
    }

    // ── 气泡本体 ────────────────────────────────────────────────────────

    /// <summary>半透明表面 + 1px 发丝描边；「可以松手了」时描边换成强调色。</summary>
    public static void DrawSurface(Graphics graphics, RectangleF pill, float scale, bool light, FlashDropState state)
    {
        using var path = RoundedRect(pill, (float)(Radius * scale));
        var accent = AccentColor(light);
        var armed = state == FlashDropState.Armed;

        var fill = armed
            ? (light ? Color.FromArgb(246, 246, 250, 253) : Color.FromArgb(244, 26, 31, 38))
            : (light ? Color.FromArgb(245, 252, 252, 252) : Color.FromArgb(243, 32, 33, 36));

        var border = armed
            ? Color.FromArgb(light ? 96 : 120, accent.R, accent.G, accent.B)
            : (light ? Color.FromArgb(18, 0, 0, 0) : Color.FromArgb(24, 255, 255, 255));

        using (var brush = new SolidBrush(fill))
        {
            graphics.FillPath(brush, path);
        }

        using var pen = new Pen(border, Math.Max(1f, 1.2f * scale));
        graphics.DrawPath(pen, path);
    }

    /// <summary>图标 + 文案 + 进度条。</summary>
    public static void DrawContent(
        Graphics graphics, RectangleF pill, float scale, bool light, FlashDropState state,
        string title, string subtitle, IconKind icon, int percent)
    {
        var accent = AccentColor(light);
        var titleColor = light ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF);
        var subtitleColor = light ? Color.FromArgb(0x62, 0x62, 0x62) : Color.FromArgb(0xA8, 0xFF, 0xFF, 0xFF);

        var iconColor = icon switch
        {
            IconKind.Check => light ? Color.FromArgb(0x0F, 0x7B, 0x0F) : Color.FromArgb(0x6C, 0xCB, 0x5F),
            IconKind.Alert => light ? Color.FromArgb(0xC4, 0x2B, 0x1C) : Color.FromArgb(0xFF, 0x99, 0x8F),
            _ => accent,
        };

        var glyphBox = new RectangleF(
            pill.Left + (float)(PadLeft * scale),
            pill.Top + ((pill.Height - (float)(GlyphWidth * scale)) / 2f),
            (float)(GlyphWidth * scale),
            (float)(GlyphWidth * scale));

        switch (icon)
        {
            case IconKind.Check:
                DrawCheck(graphics, glyphBox, iconColor, scale);
                break;

            case IconKind.Alert:
                DrawAlert(graphics, glyphBox, iconColor, scale);
                break;

            default:
                DrawUpload(graphics, glyphBox, iconColor, scale);
                break;
        }

        var textLeft = glyphBox.Right + (float)(GlyphGap * scale);
        var textWidth = pill.Right - textLeft - (float)(PadRight * scale);

        using var titleFont = CreateTitleFont(scale);
        using var subtitleFont = CreateSubtitleFont(scale);
        using var titleBrush = new SolidBrush(titleColor);
        using var subtitleBrush = new SolidBrush(subtitleColor);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        var uploading = state == FlashDropState.Uploading;
        var titleRect = new RectangleF(textLeft, pill.Top + ((uploading ? 7 : 10) * scale), textWidth, 20 * scale);
        var subtitleRect = new RectangleF(textLeft, pill.Top + ((uploading ? 29 : 30) * scale), textWidth, 16 * scale);

        graphics.DrawString(title, titleFont, titleBrush, titleRect, format);
        if (!string.IsNullOrEmpty(subtitle))
        {
            graphics.DrawString(subtitle, subtitleFont, subtitleBrush, subtitleRect, format);
        }

        if (!uploading)
        {
            return;
        }

        // 底部细进度条：贴着气泡下沿，和文字拉开距离。
        var trackRect = new RectangleF(
            pill.Left + (float)(PadLeft * scale),
            pill.Bottom - (7 * scale),
            pill.Width - (float)((PadLeft + PadRight) * scale),
            3 * scale);

        using (var track = new SolidBrush(light ? Color.FromArgb(22, 0, 0, 0) : Color.FromArgb(28, 255, 255, 255)))
        {
            graphics.FillPath(track, RoundedRect(trackRect, 1.5f * scale));
        }

        var ratio = Math.Clamp(percent, 0, 100) / 100f;
        if (ratio > 0.01f)
        {
            var fillRect = new RectangleF(trackRect.Left, trackRect.Top, trackRect.Width * ratio, trackRect.Height);
            using var fillBrush = new SolidBrush(accent);
            graphics.FillPath(fillBrush, RoundedRect(fillRect, 1.5f * scale));
        }
    }

    /// <summary>箭头 + 底线（和应用托盘图标同一套语言）。</summary>
    private static void DrawUpload(Graphics graphics, RectangleF box, Color color, float scale)
    {
        var cx = box.Left + (box.Width / 2f);
        var top = box.Top + (4f * scale);
        var stemBottom = box.Bottom - (6f * scale);

        using var pen = new Pen(color, Math.Max(1.2f, 1.7f * scale))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        graphics.DrawLine(pen, cx - (5.2f * scale), top + (5.2f * scale), cx, top);
        graphics.DrawLine(pen, cx, top, cx + (5.2f * scale), top + (5.2f * scale));
        graphics.DrawLine(pen, cx, top + (1.2f * scale), cx, stemBottom);
        graphics.DrawLine(pen, cx - (5.6f * scale), box.Bottom - (1.8f * scale), cx + (5.6f * scale), box.Bottom - (1.8f * scale));
    }

    private static void DrawCheck(Graphics graphics, RectangleF box, Color color, float scale)
    {
        var cx = box.Left + (box.Width / 2f);
        var cy = box.Top + (box.Height / 2f);
        using var pen = new Pen(color, Math.Max(1.4f, 2.0f * scale))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        graphics.DrawLines(pen, new[]
        {
            new PointF(cx - (5.6f * scale), cy + (0.2f * scale)),
            new PointF(cx - (1.6f * scale), cy + (4.2f * scale)),
            new PointF(cx + (6.0f * scale), cy - (4.4f * scale)),
        });
    }

    private static void DrawAlert(Graphics graphics, RectangleF box, Color color, float scale)
    {
        var cx = box.Left + (box.Width / 2f);
        using var pen = new Pen(color, Math.Max(1.4f, 1.9f * scale))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        graphics.DrawLine(pen, cx, box.Top + (4f * scale), cx, box.Top + (13f * scale));
        using var dot = new SolidBrush(color);
        graphics.FillEllipse(dot, cx - (1.2f * scale), box.Bottom - (7.5f * scale), 2.4f * scale, 2.4f * scale);
    }

    /// <summary>圆角矩形路径。</summary>
    public static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var diameter = Math.Max(1f, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
