using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using FileUploader.Core;
using Windows.Storage;

namespace FileUploader.App;

/// <summary>
/// 简洁模式的圆形气泡。WinUI 3 不支持逐像素透明窗口，这里自己开一个 WS_EX_LAYERED 窗口，
/// 用 GDI+ 画到 32 位预乘位图再 UpdateLayeredWindow —— 圆形、柔和阴影、环形进度都是画出来的。
/// </summary>
internal sealed class NativeBubbleWindow : IDisposable
{
    private const double BubbleDiameter = 148;
    private const double ShadowPadding = 16;
    private const double RingThickness = 6;

    private const int WmDestroy = 0x0002;
    private const int WmClose = 0x0010;
    private const int WmPaint = 0x000F;
    private const int WmMouseMove = 0x0200;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmRightButtonUp = 0x0205;
    private const int WmDropFiles = 0x0233;
    private const int WmRefresh = 0x8000 + 1;

    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTopmost = 0x00000008;

    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;

    private readonly Action _openFullWindow;
    private readonly Action _openSettings;
    private readonly Action _exitApplication;
    private readonly Action _closedCallback;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly UploadQueue _queue = UploadQueue.Current;

    private Thread? _thread;
    private IntPtr _hwnd;
    // 窗口类只注册一次，窗口过程用静态委托（进程级常驻），实际消息转给 _active。
    private static readonly WndProcDelegate StaticWndProc = StaticWindowProc;
    private static readonly IntPtr StaticProcPointer = Marshal.GetFunctionPointerForDelegate(StaticWndProc);
    private static NativeBubbleWindow? _active;
    private static bool _classRegistered;
    private bool _disposed;

    // 绘制快照（消息线程读取）。
    private volatile int _phase;
    private volatile int _percent;
    private volatile int _count;
    private volatile bool _configured = true;
    private volatile string _flashText = string.Empty;

    // 拖动 / 点击判定。
    private bool _dragging;
    private bool _moved;
    private Point _cursorStart;
    private Point _windowStart;

    private NativeBubbleWindow(Action openFullWindow, Action openSettings, Action exitApplication, Action closedCallback)
    {
        _openFullWindow = openFullWindow;
        _openSettings = openSettings;
        _exitApplication = exitApplication;
        _closedCallback = closedCallback;
    }

    private enum Phase
    {
        Idle = 0,
        Preparing = 1,
        Uploading = 2,
        Success = 3,
        Failed = 4,
    }

    /// <summary>启动气泡（独立 STA 线程创建窗口）。</summary>
    public static NativeBubbleWindow Start(Action openFullWindow, Action openSettings, Action exitApplication, Action closedCallback)
    {
        var bubble = new NativeBubbleWindow(openFullWindow, openSettings, exitApplication, closedCallback);
        bubble._thread = new Thread(bubble.RunMessageLoop)
        {
            IsBackground = true,
            Name = "FileUploader.Bubble",
        };
        bubble._thread.SetApartmentState(ApartmentState.STA);
        bubble._thread.Start();
        bubble._ready.Wait(TimeSpan.FromSeconds(5));
        return bubble;
    }

    /// <summary>请求重画（任意线程可调用）。</summary>
    public void RequestRedraw()
    {
        var hwnd = _hwnd;
        if (hwnd != IntPtr.Zero)
        {
            PostMessage(hwnd, WmRefresh, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var hwnd = _hwnd;
        if (hwnd != IntPtr.Zero)
        {
            PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void RunMessageLoop()
    {
        try
        {
            RunMessageLoopCore();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"bubble window failed: {ex}");
            _ready.Set();
            _closedCallback();
        }
    }

    private void RunMessageLoopCore()
    {
        const string className = "FileUploaderBubbleWindow";

        // 窗口过程必须是"活到进程结束"的静态委托：类只注册一次，
        // 否则第二次创建气泡时类里仍指向已回收的旧委托，CLR 会直接 FailFast 杀掉进程。
        if (!_classRegistered)
        {
            var wc = new WndClassEx
            {
                cbSize = Marshal.SizeOf<WndClassEx>(),
                lpfnWndProc = StaticProcPointer,
                hInstance = GetModuleHandle(null),
                hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)32512),
                lpszClassName = className,
            };

            _classRegistered = RegisterClassEx(ref wc) != 0;
        }

        _active = this;
        var instance = GetModuleHandle(null);
        var initial = ComputeWindowSize();
        _hwnd = CreateWindowEx(
            WsExLayered | WsExToolWindow | WsExTopmost,
            className,
            "OSS 上传",
            WsPopup,
            0,
            0,
            initial.Cx,
            initial.Cy,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            _ready.Set();
            return;
        }

        var size = ComputeWindowSize();
        PlaceBottomRight(size);
        ShowWindow(_hwnd, 4 /* SW_SHOWNOACTIVATE */);
        DragAcceptFiles(_hwnd, true);
        Subscribe();
        Render();

        if (!_ready.IsSet)
        {
            _ready.Set();
        }

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        Unsubscribe();
        if (ReferenceEquals(_active, this))
        {
            _active = null;
        }

        _closedCallback();
    }

    private void Subscribe()
    {
        _queue.PropertyChanged += OnQueueChanged;
        _queue.Uploaded += OnUploaded;
        _queue.Failed += OnFailed;
        _queue.Rejected += OnRejected;
        SettingsState.Current.PropertyChanged += OnSettingsChanged;
        RefreshSnapshot();
    }

    private void Unsubscribe()
    {
        _queue.PropertyChanged -= OnQueueChanged;
        _queue.Uploaded -= OnUploaded;
        _queue.Failed -= OnFailed;
        _queue.Rejected -= OnRejected;
        SettingsState.Current.PropertyChanged -= OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => RequestRedraw();

    private void OnQueueChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshSnapshot();
        RequestRedraw();
    }

    private void OnUploaded(UploadItem item) =>
        ShowFlash(SettingsState.Current.Value.AutoCopyUrl ? "已复制" : "已完成", success: true);

    private void OnFailed(UploadItem item) => ShowFlash("失败", success: false);

    private void OnRejected(string message) => ShowFlash("未配置", success: false);

    private void ShowFlash(string text, bool success)
    {
        _flashText = text;
        _phase = (int)(success ? Phase.Success : Phase.Failed);
        RequestRedraw();

        var token = ++_flashSeqValue;
        _ = Task.Delay(2600).ContinueWith(
            _ =>
            {
                if (_flashSeqValue == token)
                {
                    RefreshSnapshot(force: true);
                    RequestRedraw();
                }
            },
            TaskScheduler.Default);
    }

    private long _flashSeqValue;

    private void RefreshSnapshot(bool force = false)
    {
        _configured = SettingsState.Current.Value.IsConfigured;

        var items = _queue.Items;
        var active = items.Where(i => i.State is UploadState.Uploading or UploadState.Queued).ToArray();
        _count = active.Length;

        if (active.Length == 0)
        {
            _percent = 0;
            if (force)
            {
                _phase = (int)Phase.Idle;
                _flashText = string.Empty;
            }

            return;
        }

        var percent = active.Average(i => i.Progress);
        _percent = (int)Math.Round(percent);
        _phase = (int)(percent <= 0.5 ? Phase.Preparing : Phase.Uploading);
    }

    private static IntPtr StaticWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        var instance = _active;
        return instance is null
            ? DefWindowProc(hWnd, msg, wParam, lParam)
            : instance.HandleMessage(hWnd, msg, wParam, lParam);
    }

    private IntPtr HandleMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WmRefresh:
                RefreshSnapshot();
                Render();
                return IntPtr.Zero;

            case WmPaint:
                Render();
                return IntPtr.Zero;

            case WmDropFiles:
                HandleDrop(wParam);
                return IntPtr.Zero;

            case WmLeftButtonDown:
                _dragging = true;
                _moved = false;
                if (GetCursorPos(out var cursor))
                {
                    _cursorStart = new Point(cursor.X, cursor.Y);
                    var rect = GetWindowRect(hWnd);
                    _windowStart = new Point(rect.Left, rect.Top);
                }

                return IntPtr.Zero;

            case WmMouseMove:
                if (_dragging && GetCursorPos(out var now))
                {
                    var dx = now.X - _cursorStart.X;
                    var dy = now.Y - _cursorStart.Y;
                    if (!_moved && (Math.Abs(dx) > 3 || Math.Abs(dy) > 3))
                    {
                        _moved = true;
                    }

                    if (_moved)
                    {
                        var size = ComputeWindowSize();
                        SetWindowPos(hWnd, IntPtr.Zero, _windowStart.X + dx, _windowStart.Y + dy, size.Cx, size.Cy, SwpNoActivate | SwpNoZOrder);
                    }
                }

                return IntPtr.Zero;

            case WmLeftButtonUp:
                var wasDrag = _dragging && _moved;
                _dragging = false;
                _moved = false;
                if (!wasDrag)
                {
                    _openFullWindow();
                }

                return IntPtr.Zero;

            case WmRightButtonUp:
                ShowContextMenu(hWnd);
                return IntPtr.Zero;

            case WmClose:
                DestroyWindow(hWnd);
                return IntPtr.Zero;

            case WmDestroy:
                _hwnd = IntPtr.Zero;
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu(IntPtr hWnd)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        const uint mfString = 0x0000;
        const uint mfSeparator = 0x0800;
        AppendMenu(menu, mfString, 1, "完整窗口");
        AppendMenu(menu, mfString, 2, "设置");
        AppendMenu(menu, mfSeparator, 0, string.Empty);
        AppendMenu(menu, mfString, 3, "退出");

        GetCursorPos(out var point);
        const uint tpmRightButton = 0x0002;
        const uint tpmReturnCmd = 0x0100;
        var command = TrackPopupMenu(menu, tpmRightButton | tpmReturnCmd, point.X, point.Y, 0, hWnd, IntPtr.Zero);
        DestroyMenu(menu);

        switch (command)
        {
            case 1:
                _openFullWindow();
                break;
            case 2:
                _openSettings();
                break;
            case 3:
                _exitApplication();
                break;
        }
    }

    private void HandleDrop(IntPtr hDrop)
    {
        try
        {
            var count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
            var paths = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                var length = DragQueryFile(hDrop, i, null, 0);
                var buffer = new System.Text.StringBuilder((int)length + 1);
                DragQueryFile(hDrop, i, buffer, (uint)buffer.Capacity);
                paths.Add(buffer.ToString());
            }

            _ = EnqueuePathsAsync(paths);
        }
        finally
        {
            DragFinish(hDrop);
        }
    }

    private async Task EnqueuePathsAsync(IEnumerable<string> paths)
    {
        var items = new List<IStorageItem>();
        foreach (var path in paths)
        {
            try
            {
                items.Add(Directory.Exists(path)
                    ? await StorageFolder.GetFolderFromPathAsync(path)
                    : await StorageFile.GetFileFromPathAsync(path));
            }
            catch (Exception)
            {
                // 读不到的条目跳过。
            }
        }

        if (items.Count > 0)
        {
            await _queue.EnqueueAsync(items);
        }
    }

    private double Scale()
    {
        var dpi = _hwnd != IntPtr.Zero ? GetDpiForWindowSafe() : 96;
        return dpi <= 0 ? 1.0 : dpi / 96.0;
    }

    private uint GetDpiForWindowSafe()
    {
        try
        {
            return GetDpiForWindow(_hwnd);
        }
        catch (Exception)
        {
            return 96;
        }
    }

    private Size32 ComputeWindowSize()
    {
        var side = (int)Math.Round((BubbleDiameter + (ShadowPadding * 2)) * Scale());
        return new Size32 { Cx = side, Cy = side };
    }

    private void PlaceBottomRight(Size32 size)
    {
        var screenWidth = GetSystemMetrics(0);
        var screenHeight = GetSystemMetrics(1);
        var x = screenWidth - size.Cx - 48;
        var y = screenHeight - size.Cy - 96;
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, size.Cx, size.Cy, SwpNoActivate | SwpNoZOrder);
    }

    private void Render()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var size = ComputeWindowSize();
        using var bitmap = new Bitmap(size.Cx, size.Cy, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);

            var scale = (float)(size.Cx / (BubbleDiameter + (ShadowPadding * 2)));
            var radius = (float)(BubbleDiameter / 2 * scale);
            var center = new PointF(size.Cx / 2f, size.Cy / 2f);

            DrawShadow(graphics, scale, size);
            DrawBubble(graphics, center, radius, scale);
        }

        UpdateLayered(bitmap, size);


    }

    private Bitmap? _shadowCache;
    private int _shadowCacheSide;

    /// <summary>
    /// 柔和但不夸张的投影：按到圆边的距离做高斯衰减，峰值仅 0.16，衰减长度约 6px。
    /// 结果按尺寸缓存，只有窗口尺寸/DPI 变化时才重算。
    /// </summary>
    private void DrawShadow(Graphics graphics, float scale, Size32 size)
    {
        if (_shadowCache is null || _shadowCacheSide != size.Cx)
        {
            _shadowCache?.Dispose();
            _shadowCache = BuildShadow(size.Cx, scale);
            _shadowCacheSide = size.Cx;
        }

        graphics.DrawImageUnscaled(_shadowCache, 0, 0);
    }

    private static Bitmap BuildShadow(int side, float scale)
    {
        var bitmap = new Bitmap(side, side, PixelFormat.Format32bppPArgb);
        var centerX = side / 2f;
        var centerY = (side / 2f) + (2f * scale);
        var radius = (float)(BubbleDiameter / 2 * scale);
        var sigma = Math.Max(2.2f, 5.2f * scale);
        const float peak = 0.16f;
        var twoSigmaSquared = 2 * sigma * sigma;

        var data = bitmap.LockBits(
            new Rectangle(0, 0, side, side),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            PixelFormat.Format32bppPArgb);

        try
        {
            var buffer = new byte[data.Stride * side];
            for (var y = 0; y < side; y++)
            {
                var dy = y - centerY;
                var row = y * data.Stride;
                for (var x = 0; x < side; x++)
                {
                    var dx = x - centerX;
                    var distance = MathF.Sqrt((dx * dx) + (dy * dy)) - radius;
                    var index = row + (x * 4);
                    if (distance < -sigma)
                    {
                        // 圆内部：整块都会被白色圆盖住，写满峰值省一次判断。
                        buffer[index + 3] = (byte)(peak * 255);
                        continue;
                    }

                    var alpha = distance <= 0
                        ? peak
                        : (float)(peak * Math.Exp(-(distance * distance) / twoSigmaSquared));

                    // 太淡的值直接归零，避免边缘出现一圈"灰边"。
                    buffer[index + 3] = alpha < 0.004f ? (byte)0 : (byte)(alpha * 255);
                    // 黑色预乘：RGB 也是 0，无需写。
                }
            }

            Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    private void DrawBubble(Graphics graphics, PointF center, float radius, float scale)
    {
        var rect = new RectangleF(center.X - radius, center.Y - radius, radius * 2, radius * 2);

        using (var fill = new SolidBrush(Color.White))
        {
            graphics.FillEllipse(fill, rect);
        }

        using (var border = new Pen(Color.FromArgb(24, 0, 0, 0), Math.Max(1f, scale)))
        {
            graphics.DrawEllipse(border, rect);
        }

        var phase = (Phase)_phase;
        var showRing = phase is Phase.Preparing or Phase.Uploading or Phase.Success or Phase.Failed;
        if (!showRing)
        {
            DrawCenterContent(graphics, center, scale, phase);
            return;
        }

        var width = (float)(RingThickness * scale);
        var inset = width / 2;
        var ringRect = RectangleF.Inflate(rect, -inset, -inset);

        using (var track = new Pen(Color.FromArgb(34, 0, 0, 0), width))
        {
            graphics.DrawEllipse(track, ringRect);
        }

        var sweep = phase switch
        {
            Phase.Success => 359.99f,
            Phase.Uploading => Math.Clamp(_percent, 0, 100) * 3.5999f,
            _ => 0f,
        };

        if (sweep > 0.5f)
        {
            var color = phase switch
            {
                Phase.Success => Color.FromArgb(0x10, 0x7C, 0x10),
                Phase.Failed => Color.FromArgb(0xC4, 0x2B, 0x1C),
                _ => AccentColor(),
            };

            using var arc = new Pen(color, width)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            graphics.DrawArc(arc, ringRect, -90f, sweep);
        }

        DrawCenterContent(graphics, center, scale, phase);
    }

    private void DrawCenterContent(Graphics graphics, PointF center, float scale, Phase phase)
    {
        using var titleFont = new Font("Segoe UI", 15f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var captionFont = new Font("Microsoft YaHei UI", 9.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var glyphFont = new Font("Segoe UI Symbol", 15f * scale, FontStyle.Regular, GraphicsUnit.Pixel);

        string title;
        string caption;
        var titleColor = Color.FromArgb(0x1B, 0x1B, 0x1B);
        var captionColor = Color.FromArgb(0x62, 0x62, 0x62);

        switch (phase)
        {
            case Phase.Uploading:
                title = $"{Math.Clamp(_percent, 0, 100)}%";
                caption = $"{_count} 个文件";
                break;
            case Phase.Preparing:
                title = "准备中";
                caption = _count > 1 ? $"{_count} 个文件" : string.Empty;
                break;
            case Phase.Success:
                title = "✓";
                caption = _flashText;
                titleColor = Color.FromArgb(0x10, 0x7C, 0x10);
                break;
            case Phase.Failed:
                title = "!";
                caption = _flashText;
                titleColor = Color.FromArgb(0xC4, 0x2B, 0x1C);
                break;
            default:
                title = "↑";
                caption = _configured ? "拖入上传" : "未配置";
                break;
        }

        var isGlyph = phase is Phase.Idle or Phase.Success or Phase.Failed;
        var font = isGlyph ? glyphFont : titleFont;

        var titleFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var captionFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        var titleHeight = 30f * scale;
        var captionHeight = 18f * scale;
        var total = titleHeight + (string.IsNullOrEmpty(caption) ? 0 : captionHeight + (2 * scale));
        var top = center.Y - (total / 2f);

        using var titleBrush = new SolidBrush(titleColor);
        using var captionBrush = new SolidBrush(captionColor);

        var titleRect = new RectangleF(center.X - 70 * scale, top, 140 * scale, titleHeight);
        graphics.DrawString(title, font, titleBrush, titleRect, titleFormat);

        if (!string.IsNullOrEmpty(caption))
        {
            var captionRect = new RectangleF(center.X - 70 * scale, titleRect.Bottom + (2 * scale), 140 * scale, captionHeight);
            graphics.DrawString(caption, captionFont, captionBrush, captionRect, captionFormat);
        }
    }

    private void UpdateLayered(Bitmap bitmap, Size32 size)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        var oldObject = SelectObject(memoryDc, hBitmap);

        var rect = GetWindowRect(_hwnd);
        var position = new Point32 { X = rect.Left, Y = rect.Top };
        var source = new Point32 { X = 0, Y = 0 };
        var blend = new BlendFunction
        {
            BlendOp = 0,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = 1,
        };

        const uint ulwAlpha = 0x00000002;
        UpdateLayeredWindow(_hwnd, screenDc, ref position, ref size, memoryDc, ref source, 0, ref blend, ulwAlpha);

        SelectObject(memoryDc, oldObject);
        DeleteObject(hBitmap);
        DeleteDC(memoryDc);
        ReleaseDC(IntPtr.Zero, screenDc);
    }

    private Color AccentColor()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                return Color.FromArgb(255, abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
            }
        }
        catch (Exception)
        {
            // 读不到就用默认蓝。
        }

        return Color.FromArgb(0x0F, 0x6C, 0xBD);
    }

    // ── Win32 ───────────────────────────────────────────────────────────

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size32
    {
        public int Cx;
        public int Cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message32
    {
        public IntPtr Hwnd;
        public int Msg;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point32 Point;
        public uint Private;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message32 msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message32 msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Message32 msg);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr hWnd, bool accept);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint index, System.Text.StringBuilder? buffer, uint length);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr hDrop);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr id, string text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hWnd, IntPtr dstDc, ref Point32 dstPoint, ref Size32 size, IntPtr srcDc,
        ref Point32 srcPoint, int colorKey, ref BlendFunction blend, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr cursorName);

    private Rect32 GetWindowRect(IntPtr hWnd)
    {
        GetWindowRect(hWnd, out var rect);
        return rect;
    }
}
