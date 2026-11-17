using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;

namespace FileUploader.App;

/// <summary>窗口管理：主窗口、设置窗口、简洁模式（气泡）之间的切换。</summary>
internal static class AppWindows
{
    private const int SwHide = 0;
    private const int SwShowNormal = 1;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private static ReactorWindow? _settings;
    private static NativeBubbleWindow? _bubble;
    private static ReactorTrayIcon? _tray;
    private static bool _exiting;

    /// <summary>是否正在真正退出（关闭窗口不再收进托盘）。</summary>
    public static bool IsExiting => _exiting;

    /// <summary>创建托盘图标（只建一次）并挂上右键菜单。</summary>
    public static void EnsureTray()
    {
        if (_tray is not null)
        {
            return;
        }

        try
        {
            _tray = ReactorApp.OpenTrayIcon(new TrayIconSpec(BuildTrayIcon(), "OSS 拖拽上传 · 右键更多"));

            _tray.Click += (_, _) => RunOnUi(ShowMainWindow);
            _tray.DoubleClick += (_, _) => RunOnUi(ShowMainWindow);
            _tray.RightClick += (_, _) => RunOnUi(ShowTrayMenu);
        }
        catch (Exception)
        {
            _tray = null;
        }
    }

    /// <summary>托盘图标是否可用：不可用时关闭窗口就真的退出，避免程序"消失"。</summary>
    public static bool CanMinimizeToTray => _tray is not null;

    /// <summary>启动「拖拽浮现」上传气泡：拖文件到屏幕右下角就能直接传，不占位置。</summary>
    public static void EnsureDragFlash() => DragFlashController.Apply();

    /// <summary>闪现气泡上的点击/右键：把主窗口叫回来。</summary>
    internal static void ShowMainFromFlash() => RunOnUi(ShowMainWindow);

    /// <summary>把主窗口收进托盘（关闭按钮的默认行为）。</summary>
    public static void HideToTray() => HideMainWindow();

    private static void ShowTrayMenu()
    {
        var settings = SettingsState.Current.Value;
        var target = settings.IsConfigured
            ? $"{settings.Bucket} · {settings.NormalizedEndpoint}"
            : "未配置存储";

        TrayMenu.Show(new[]
        {
            new TrayMenu.Item(target, IsHeader: true),
            TrayMenu.Item.Separator,
            new TrayMenu.Item("打开主窗口", ShowMainWindow),
            new TrayMenu.Item("简洁模式", EnterCompact),
            new TrayMenu.Item("设置", OpenSettings),
            TrayMenu.Item.Separator,
            new TrayMenu.Item("退出", ExitApplication),
        });
    }

    /// <summary>开发辅助：配合 <c>--menu-preview</c> 启动参数直接弹一次托盘菜单，便于无鼠标回归（见 tools/uia-tray-menu.ps1）。</summary>
    internal static void ShowTrayMenuForPreview() => ShowTrayMenu();

    /// <summary>简洁模式气泡的右键菜单（和托盘菜单同一套现代化外观）。</summary>
    private static void ShowCompactMenu() =>
        TrayMenu.Show(new[]
        {
            new TrayMenu.Item("完整窗口", ExitCompact),
            TrayMenu.Item.Separator,
            new TrayMenu.Item("设置", OpenSettings),
            new TrayMenu.Item("退出", ExitApplication),
        });
    /// <summary>
    /// 运行时画一个托盘图标：和应用图标同一套设计（蓝色圆角方块 + 白色上传箭头 + 底线）。
    /// 托盘走原生 HICON 通道，喂不进 .ico 容器，所以这里用 GDI+ 现画，避免额外文件依赖。
    /// </summary>
    private static WindowIcon BuildTrayIcon()
    {
        const int side = 32;
        using var bitmap = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            var bounds = new RectangleF(0.5f, 0.5f, side - 1f, side - 1f);
            using var path = RoundedRect(bounds, side * 0.215f);
            using var brush = new LinearGradientBrush(
                bounds,
                Color.FromArgb(0x4A, 0x9C, 0xFF),
                Color.FromArgb(0x0F, 0x4C, 0xC6),
                LinearGradientMode.ForwardDiagonal);
            graphics.FillPath(brush, path);

            using var pen = new Pen(Color.White, 3.2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            const float cx = side / 2f;
            graphics.DrawLine(pen, cx - 5.4f, 13.4f, cx, 8.0f);   // 箭头左臂
            graphics.DrawLine(pen, cx, 8.0f, cx + 5.4f, 13.4f);   // 箭头右臂
            graphics.DrawLine(pen, cx, 9.6f, cx, 20.0f);          // 竖杆
            graphics.DrawLine(pen, cx - 4.8f, 23.2f, cx + 4.8f, 23.2f); // 底线
        }

        var data = bitmap.LockBits(new Rectangle(0, 0, side, side), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var rgba = new byte[side * side * 4];
        try
        {
            var source = new byte[data.Stride * side];
            Marshal.Copy(data.Scan0, source, 0, source.Length);
            for (var y = 0; y < side; y++)
            {
                for (var x = 0; x < side; x++)
                {
                    var s = (y * data.Stride) + (x * 4);
                    var d = ((y * side) + x) * 4;
                    rgba[d] = source[s + 2];
                    rgba[d + 1] = source[s + 1];
                    rgba[d + 2] = source[s];
                    rgba[d + 3] = source[s + 3];
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return WindowIcon.FromRgba(rgba, side, side);
    }

    /// <summary>圆角矩形路径。</summary>
    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>打开（或激活）设置窗口。</summary>
    public static void OpenSettings()
    {
        if (_settings is { } existing)
        {
            existing.Activate();
            return;
        }

        var window = ReactorApp.OpenWindow(
            new WindowSpec
            {
                Title = "存储配置",
                Width = 720,
                Height = 800,
                MinWidth = 620,
                MinHeight = 600,
                StartPosition = WindowStartPosition.CenterOnCurrent,
            },
            () => new SettingsWindow());

        _settings = window;
        ReactorApp.WindowClosed += OnWindowClosed;
    }

    /// <summary>切到简洁模式：隐藏主窗口，显示右下角的圆形气泡。</summary>
    public static void EnterCompact()
    {
        if (_bubble is not null)
        {
            return;
        }

        HideMainWindow();

        _bubble = NativeBubbleWindow.Start(
            openFullWindow: () => RunOnUi(ExitCompact),
            openSettings: () => RunOnUi(OpenSettings),
            exitApplication: () => RunOnUi(ExitApplication),
            showMenu: () => RunOnUi(ShowCompactMenu),
            closedCallback: () => RunOnUi(OnBubbleClosed));
    }

    /// <summary>
    /// 主窗口「应该保持隐藏」的意图。延迟补隐藏只有在意图仍为隐藏时才生效，
    /// 否则用户快速点气泡回来时会被补刀（见 <see cref="HideMainWindowAfter"/>）。
    /// </summary>
    private static bool _mainWindowShouldHide;

    /// <summary>隐藏代次：每次显示主窗口就 +1，让队列里还没跑的延迟隐藏全部作废。</summary>
    private static int _hideSequence;

    /// <summary>隐藏主窗口（Reactor 在内容挂载后才真正显示窗口，所以要多收几次）。</summary>
    private static void HideMainWindow()
    {
        var main = ReactorApp.PrimaryWindow;
        if (main is null)
        {
            return;
        }

        var handle = WinRT.Interop.WindowNative.GetWindowHandle(main.NativeWindow);
        _mainWindowShouldHide = true;
        var sequence = ++_hideSequence;
        ShowWindow(handle, SwHide);

        foreach (var delay in new[] { 250, 700, 1500 })
        {
            HideMainWindowAfter(handle, sequence, delay);
        }
    }

    /// <summary>
    /// 延迟再收一次窗口。注意必须带「意图 + 代次」校验：
    /// 用户可能在 1.5 秒内就点气泡回到完整窗口，这时若还照旧隐藏，
    /// 就会出现「刚打开又被自动藏起来」的鬼畜行为。
    /// </summary>
    private static void HideMainWindowAfter(IntPtr handle, int sequence, int delayMs)
    {
        _ = Task.Delay(delayMs).ContinueWith(
            _ => RunOnUi(() =>
            {
                if (sequence != _hideSequence || !_mainWindowShouldHide)
                {
                    return;
                }

                if (IsWindowVisible(handle))
                {
                    ShowWindow(handle, SwHide);
                }
            }),
            TaskScheduler.Default);
    }

    /// <summary>从简洁模式回到完整窗口。</summary>
    public static void ExitCompact()
    {
        CloseBubble();
        ShowMainWindow();
    }

    /// <summary>关闭气泡并退出程序。</summary>
    public static void ExitApplication()
    {
        _exiting = true;
        CloseBubble();
        DragFlashController.Shutdown();
        try
        {
            _tray?.Dispose();
        }
        catch (Exception)
        {
        }

        _tray = null;
        ReactorApp.Exit();
    }

    /// <summary>气泡窗口消失（用户点了退出，或者原生窗口出错）时兜底：把主窗口叫回来。</summary>
    private static void OnBubbleClosed()
    {
        if (_bubble is null)
        {
            return;
        }

        _bubble = null;
        if (!_exiting)
        {
            ShowMainWindow();
        }
    }

    private static void CloseBubble()
    {
        var bubble = _bubble;
        _bubble = null;
        try
        {
            bubble?.Dispose();
        }
        catch (Exception)
        {
            // 已经关掉了。
        }
    }

    private static void ShowMainWindow()
    {
        var main = ReactorApp.PrimaryWindow;
        if (main is null)
        {
            return;
        }

        // 取消所有还在路上的「补隐藏」，否则刚显示出来又会被藏回去。
        _mainWindowShouldHide = false;
        _hideSequence++;

        ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(main.NativeWindow), SwShowNormal);
        main.Activate();
    }

    /// <summary>气泡的交互回调在原生消息线程上触发，统一切回 UI 线程。</summary>
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

    private static void OnWindowClosed(object? sender, ReactorWindow window)
    {
        if (ReferenceEquals(window, _settings))
        {
            _settings = null;
            ReactorApp.WindowClosed -= OnWindowClosed;
        }
    }
}
