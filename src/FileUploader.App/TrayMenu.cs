using System.Runtime.InteropServices;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;

namespace FileUploader.App;

/// <summary>
/// 托盘图标 / 简洁模式气泡的右键菜单。
/// <para>
/// 用一扇无边框的 WinUI 小窗口来画菜单（圆角卡片 + 主题色 + 悬停高亮），
/// 不再用 Win32 <c>TrackPopupMenu</c>——那套系统菜单外观和现在的 WinUI 界面完全不是一个时代。
/// </para>
/// </summary>
internal static class TrayMenu
{
    /// <summary>卡片宽度（DIP）。</summary>
    internal const double CardWidth = 236;

    /// <summary>卡片内边距（DIP）。</summary>
    internal const double CardPadding = 6;

    /// <summary>普通菜单项高度（DIP）。</summary>
    internal const double ItemHeight = 32;

    /// <summary>顶部信息行高度（DIP）。</summary>
    internal const double HeaderHeight = 34;

    /// <summary>分隔线占位高度（DIP，含上下留白）。</summary>
    internal const double SeparatorHeight = 9;

    private static ReactorWindow? _window;
    private static IntPtr _hwnd;
    private static LightDismissHook? _hook;
    private static IntPtr _shadow;

    /// <summary>菜单项：分隔符用 <see cref="Separator"/>，信息行用 <see cref="Item"/> 的 <c>IsHeader</c>。</summary>
    internal sealed record Item(string Label, Action? Click = null, bool IsEnabled = true, bool IsHeader = false)
    {
        public static Item Separator { get; } = new("-", null, false);

        /// <summary>不可点击的分隔线。</summary>
        public bool IsSeparator => Label == "-" && Click is null && !IsHeader;
    }

    /// <summary>在鼠标位置弹出菜单（同一时间只保留一个）。</summary>
    public static void Show(IReadOnlyList<Item> items)
    {
        Close();
        if (items.Count == 0)
        {
            return;
        }

        var (x, y) = ComputePosition(items);

        var window = ReactorApp.OpenWindow(
            new WindowSpec
            {
                Title = "菜单",
                Width = CardWidth,
                // 高度按行高算死：SizeToContent 依赖排版回传，弹菜单时容易出现 1px 高的空窗口。
                Height = TotalHeight(items),
                SizeToContent = WindowSizeToContent.Manual,
                Style = WindowStyle.None,
                ExtendsContentIntoTitleBar = true,
                ResizeMode = WindowResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowInSwitcher = false,
                Level = WindowLevel.AlwaysOnTop,
                CornerStyle = WindowCornerStyle.RoundedSmall,
                StartPosition = WindowStartPosition.Manual,
                ManualPosition = (x, y),
                IsMaximizable = false,
                IsMinimizable = false,
                IsMovableByBackground = false,
                ActivateOnOpen = true,
            },
            () => new TrayMenuView(items, OnPicked));

        _window = window;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window.NativeWindow);

        // 阴影：DWM 不给无边框弹层画阴影（Reactor 还会反复重设窗口样式），
        // 所以在菜单窗口下面垫一扇点击穿透的分层窗口自己画。等排版稳定再画。
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is not null)
        {
            var timer = queue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(120);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                CreateShadow();
            };
            timer.Start();
        }
        else
        {
            CreateShadow();
        }

        // 窗口能拿到前台时，失焦就收起（正常情况下最灵敏）。
        window.Deactivated += (_, _) => Close();

        // 但无边框弹层常常拿不到前台（被系统前台锁挡掉），失焦事件就不会来，
        // 所以再挂一个只在菜单显示期间存在的低级钩子兜底：点别处 / 按 Esc 都关。
        _hook = new LightDismissHook(_hwnd, () => RunOnUi(Close));
    }

    /// <summary>收起菜单（如果开着）。</summary>
    public static void Close()
    {
        var window = _window;
        var hwnd = _hwnd;
        _window = null;
        _hwnd = IntPtr.Zero;

        _hook?.Dispose();
        _hook = null;

        MenuShadow.Destroy(_shadow);
        _shadow = IntPtr.Zero;

        if (window is null)
        {
            return;
        }

        try
        {
            window.Close();
        }
        catch (Exception)
        {
            // 已经关掉了。
        }
    }

    /// <summary>
    /// 把窗口本身裁成圆角。
    /// 无边框窗口的底色是白/不透明的，光靠 XAML 里的圆角 Border 只是"里面圆、外面还是直角"，
    /// 这里直接给窗口设一个圆角区域，四角才会真的空出来露出阴影。
    /// </summary>
    private static void ApplyRoundedRegion(int radius)
    {
        if (_hwnd == IntPtr.Zero || !GetWindowRect(_hwnd, out var bounds))
        {
            return;
        }

        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        if (region != IntPtr.Zero)
        {
            // 交给系统托管，不要自己 DeleteObject。
            _ = SetWindowRgn(_hwnd, region, true);
        }
    }

    /// <summary>在菜单窗口下方画一圈阴影。</summary>
    private static void CreateShadow()
    {
        if (_hwnd == IntPtr.Zero || !GetWindowRect(_hwnd, out var bounds))
        {
            return;
        }

        var dpi = GetDpiForWindow(_hwnd);
        var scale = dpi <= 0 ? 1.0 : dpi / 96.0;
        var radius = (int)Math.Round(5 * scale);   // 与卡片外圈描边一致
        ApplyRoundedRegion(radius);

        MenuShadow.Destroy(_shadow);
        _shadow = MenuShadow.Create(_hwnd, bounds, radius);
    }

    /// <summary>菜单关闭动作统一切回 UI 线程（钩子回调可能来自消息循环的不同阶段）。</summary>
    private static void RunOnUi(Action action)
    {
        var dispatcher = ReactorApp.UIDispatcher;
        if (dispatcher is null)
        {
            action();
            return;
        }

        dispatcher.TryEnqueue(() => action());
    }

    private static void OnPicked(Item item)
    {
        Close();
        item.Click?.Invoke();
    }

    /// <summary>菜单总高度（DIP），和 <see cref="TrayMenuView"/> 的排版保持一致。</summary>
    private static double TotalHeight(IReadOnlyList<Item> items)
    {
        var height = CardPadding * 2;
        foreach (var item in items)
        {
            height += item.IsSeparator ? SeparatorHeight : item.IsHeader ? HeaderHeight : ItemHeight;
        }

        return height;
    }

    /// <summary>算出菜单左上角位置（DIP）：跟随鼠标，靠边时翻转，别跑出屏幕。</summary>
    private static (double X, double Y) ComputePosition(IReadOnlyList<Item> items)
    {
        if (!GetCursorPos(out var cursor))
        {
            return (200, 200);
        }

        // 用鼠标下面那扇窗口的 DPI 换算 DIP（多显示器不同缩放也对得上）。
        var dpi = 96u;
        var under = WindowFromPoint(cursor);
        if (under != IntPtr.Zero)
        {
            var actual = GetDpiForWindow(under);
            if (actual > 0)
            {
                dpi = actual;
            }
        }

        var scale = dpi / 96.0;
        var x = cursor.X / scale;
        var y = cursor.Y / scale;
        var height = TotalHeight(items);

        var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(monitor, ref info))
        {
            var left = info.Work.Left / scale;
            var top = info.Work.Top / scale;
            var right = info.Work.Right / scale;
            var bottom = info.Work.Bottom / scale;

            if (x + CardWidth > right)
            {
                x -= CardWidth;
            }

            if (y + height > bottom)
            {
                y -= height;
            }

            x = Math.Clamp(x, left + 4, Math.Max(left + 4, right - CardWidth - 4));
            y = Math.Clamp(y, top + 4, Math.Max(top + 4, bottom - height - 4));
        }

        return (x, y);
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
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

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point32 point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);


    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point32 point, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out MenuShadow.Rectangle32 rect);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);


    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
