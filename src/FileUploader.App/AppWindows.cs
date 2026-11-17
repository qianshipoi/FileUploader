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

    /// <summary>把主窗口收进托盘（关闭按钮的默认行为）。</summary>
    public static void HideToTray() => HideMainWindow();

    private static void ShowTrayMenu()
    {
        var settings = SettingsState.Current.Value;
        var target = settings.IsConfigured
            ? $"{settings.Bucket} · {settings.NormalizedEndpoint}"
            : "未配置存储";

        NativeMenu.Show(new[]
        {
            new NativeMenu.Item(target, Click: null, IsEnabled: false),
            NativeMenu.Item.Separator,
            new NativeMenu.Item("打开主窗口", ShowMainWindow, IsDefault: true),
            new NativeMenu.Item("简洁模式", EnterCompact),
            new NativeMenu.Item("设置", OpenSettings),
            NativeMenu.Item.Separator,
            new NativeMenu.Item("退出", ExitApplication),
        });
    }
    /// <summary>运行时画一个托盘图标（蓝色圆 + 白色上传箭头），避免依赖外部 .ico。</summary>
    private static WindowIcon BuildTrayIcon()
    {
        const int side = 32;
        using var bitmap = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var circle = new SolidBrush(Color.FromArgb(0xFF, 0x0F, 0x6C, 0xBD));
            graphics.FillEllipse(circle, 1f, 1f, side - 2, side - 2);
            using var pen = new Pen(Color.White, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawLine(pen, 16f, 23.5f, 16f, 10.5f);
            graphics.DrawLine(pen, 10.5f, 16f, 16f, 10.5f);
            graphics.DrawLine(pen, 21.5f, 16f, 16f, 10.5f);
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
            closedCallback: () => RunOnUi(OnBubbleClosed));
    }

    /// <summary>隐藏主窗口（Reactor 在内容挂载后才真正显示窗口，所以要多收几次）。</summary>
    private static void HideMainWindow()
    {
        var main = ReactorApp.PrimaryWindow;
        if (main is null)
        {
            return;
        }

        var handle = WinRT.Interop.WindowNative.GetWindowHandle(main.NativeWindow);
        ShowWindow(handle, SwHide);

        foreach (var delay in new[] { 250, 700, 1500 })
        {
            _ = Task.Delay(delay).ContinueWith(
                _ => RunOnUi(() =>
                {
                    if (IsWindowVisible(handle))
                    {
                        ShowWindow(handle, SwHide);
                    }
                }),
                TaskScheduler.Default);
        }
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
