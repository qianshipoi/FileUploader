using System.Globalization;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Reactor.Factories;

namespace FileUploader.App;

/// <summary>
/// 「测试连接」按钮里的小心电图。
/// <para>
/// 空闲时是一条静态折线；测试中由一段亮线从左到右扫过（像心电监护仪刷新的感觉），
/// 扫完自动循环，测试结束回到静态折线。
/// </para>
/// <para>
/// 做成子组件是为了让按帧重绘的范围只在 22×14 DIP 这一小块里，
/// 不牵动整个设置窗口（否则每个字都重渲染，输入框光标会有风险）。
/// </para>
/// </summary>
internal sealed class TestPulseIcon : Component<bool>
{
    private const double IconWidth = 24;
    private const double IconHeight = 16;
    private const double StrokeThickness = 1.5;

    /// <summary>一次完整扫描的时长。</summary>
    private const double SweepMs = 1100;

    /// <summary>亮线长度占整条折线的比例（拖尾）。</summary>
    private const double Trail = 0.30;

    /// <summary>心电图折线（DIP，坐标基于 22×14 画布）：平线 → 小凹 → 大尖峰 → 平线。</summary>
    private static readonly (double X, double Y)[] Trace =
    {
        (0.6, 9.6),
        (4.4, 9.6),
        (6.2, 9.6),
        (7.7, 5.6),
        (9.1, 12.6),
        (10.7, 1.6),
        (12.3, 9.6),
        (14.1, 9.6),
        (21.4, 9.6),
    };

    /// <summary>每一段累计长度，用来把一个 0~1 的进度换算成折线上的坐标。</summary>
    private static readonly double[] Cumulative = BuildCumulative();

    private static readonly double TotalLength = Cumulative[^1];

    private static readonly string FullPath = BuildPath(0, 1);

    /// <inheritdoc />
    public override Element Render()
    {
        var testing = Props;
        var (progress, setProgress) = UseState(0.0);

        UseEffect(
            () =>
            {
                if (!Props)
                {
                    return () => { };
                }

                var queue = DispatcherQueue.GetForCurrentThread();
                if (queue is null)
                {
                    return () => { };
                }

                var timer = queue.CreateTimer();
                timer.Interval = TimeSpan.FromMilliseconds(33);
                var startedAt = Environment.TickCount64;
                timer.Tick += (_, _) =>
                {
                    var elapsed = Environment.TickCount64 - startedAt;
                    setProgress((elapsed % (long)SweepMs) / SweepMs);
                };
                timer.Start();

                return () => timer.Stop();
            },
            testing);

        if (!testing)
        {
            // 空闲：一条静态的次要色折线
            return IconBox(new PathElement
            {
                PathDataString = FullPath,
                Stroke = SecondaryBrush,
                StrokeThickness = StrokeThickness,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });
        }

        var head = Math.Clamp(progress, 0.02, 1.0);
        var tail = Math.Max(0, head - Trail);

        return IconBox(
            // 底图：整条折线压暗，衬托亮线
            new PathElement
            {
                PathDataString = FullPath,
                Stroke = Fade(SecondaryBrush, 0x59),
                StrokeThickness = StrokeThickness,
                StrokeLineJoin = PenLineJoin.Round,
            },
            // 扫描线：一小段亮线 + 圆头（圆头本身就像是"亮点"）
            new PathElement
            {
                PathDataString = BuildPath(tail, head),
                Stroke = AccentBrush,
                StrokeThickness = StrokeThickness + 0.35,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });
    }

    /// <summary>固定 22×14 的图标盒子，保证按钮内文字与图标基线对齐。</summary>
    private static Element IconBox(params Element[] children) =>
        Grid(columns: [GridSize.Star()], rows: [GridSize.Star()], children: children)
            .Width(IconWidth)
            .Height(IconHeight)
            .VAlign(VerticalAlignment.Center);

    /// <summary>次要文字色（空闲折线 / 扫描底图）。</summary>
    private static Brush SecondaryBrush =>
        ThemeResource.Brush("TextFillColorSecondaryBrush")
        ?? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A));

    /// <summary>强调色（扫描的亮线）。</summary>
    private static Brush AccentBrush =>
        ThemeResource.Brush("AccentTextFillColorPrimaryBrush")
        ?? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x0F, 0x6C, 0xBD));

    /// <summary>取同色但指定透明度的刷子（PathElement 没有 Opacity 属性，只能改颜色）。</summary>
    private static Brush Fade(Brush source, byte alpha)
    {
        var color = source is SolidColorBrush solid
            ? solid.Color
            : Windows.UI.Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    /// <summary>把 [from, to] 段折线拼成 Path 的 data 字符串（长度按比例换算）。</summary>
    private static string BuildPath(double from, double to)
    {
        var startLength = Math.Clamp(from, 0, 1) * TotalLength;
        var endLength = Math.Clamp(to, 0, 1) * TotalLength;
        if (endLength - startLength < 0.05)
        {
            endLength = Math.Min(TotalLength, startLength + 0.05);
        }

        var builder = new StringBuilder();
        AppendPoint(builder, PointAt(startLength), first: true);

        for (var i = 0; i < Trace.Length; i++)
        {
            var cumulative = Cumulative[i];
            if (cumulative > startLength && cumulative < endLength)
            {
                AppendPoint(builder, Trace[i], first: false);
            }
        }

        AppendPoint(builder, PointAt(endLength), first: false);
        return builder.ToString();
    }

    /// <summary>折线上距离起点 <paramref name="distance"/> 处的坐标。</summary>
    private static (double X, double Y) PointAt(double distance)
    {
        if (distance <= 0)
        {
            return Trace[0];
        }

        for (var i = 1; i < Trace.Length; i++)
        {
            if (Cumulative[i] >= distance)
            {
                var segment = Cumulative[i] - Cumulative[i - 1];
                var t = segment <= 0 ? 0 : (distance - Cumulative[i - 1]) / segment;
                var (x0, y0) = Trace[i - 1];
                var (x1, y1) = Trace[i];
                return (x0 + ((x1 - x0) * t), y0 + ((y1 - y0) * t));
            }
        }

        return Trace[^1];
    }

    private static double[] BuildCumulative()
    {
        var cumulative = new double[Trace.Length];
        for (var i = 1; i < Trace.Length; i++)
        {
            var dx = Trace[i].X - Trace[i - 1].X;
            var dy = Trace[i].Y - Trace[i - 1].Y;
            cumulative[i] = cumulative[i - 1] + Math.Sqrt((dx * dx) + (dy * dy));
        }

        return cumulative;
    }

    private static void AppendPoint(StringBuilder builder, (double X, double Y) point, bool first)
    {
        builder.Append(first ? 'M' : 'L');
        builder.Append(point.X.ToString("0.##", CultureInfo.InvariantCulture));
        builder.Append(',');
        builder.Append(point.Y.ToString("0.##", CultureInfo.InvariantCulture));
        builder.Append(' ');
    }
}
