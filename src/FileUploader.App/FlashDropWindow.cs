using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using FileUploader.Core;
using Windows.Storage;

namespace FileUploader.App;

/// <summary>闪现气泡的状态。</summary>
internal enum FlashDropState
{
    /// <summary>完全隐形：alpha=1/255 且点击穿透。</summary>
    Dormant = 0,

    /// <summary>察觉到可能有人在拖东西：半透明小提示。</summary>
    Peek = 1,

    /// <summary>拖的是文件，并且已经拖到气泡上：全亮 + 「松手即上传」。</summary>
    Armed = 2,

    /// <summary>正在上传。</summary>
    Uploading = 3,

    /// <summary>完成（已复制链接）。</summary>
    Done = 4,

    /// <summary>失败 / 未配置。</summary>
    Failed = 5,
}

/// <summary>
/// QQ 闪传式的「拖拽浮现」上传气泡。
/// <para>
/// 平时完全看不见（alpha=1/255，肉眼不可辨），并且 <c>WM_NCHITTEST</c> 返回 <c>HTTRANSPARENT</c>，
/// 点击和拖拽都穿透到下层窗口 —— 不占地方、不挡点击。鼠标左键一按下就改成 <c>HTCLIENT</c>，
/// OLE 的落点搜索立刻能看见它；拖的是文件才淡入并高亮，不是文件立即缩回去。
/// </para>
/// <para>
/// 为什么不用 <c>DragAcceptFiles</c>：那样只有「松手」时才知道松的是文件，没有
/// <c>DragEnter/DragLeave</c>，做不出「拖上来就亮、拖走就收」。
/// </para>
/// </summary>
internal sealed class FlashDropWindow : IDisposable
{



    private const int WmDestroy = 0x0002;
    private const int WmClose = 0x0010;
    private const int WmNcHitTest = 0x0084;
    private const int WmTimer = 0x0113;
    private const int WmDisplayChange = 0x007E;
    private const int WmSettingChange = 0x001A;
    private const int WmThemeChanged = 0x031A;
    private const int WmMouseActivate = 0x0021;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmRightButtonUp = 0x0205;

    private const int WmAppPeek = 0x8000 + 31;
    private const int WmAppHibernate = 0x8000 + 32;
    private const int WmAppPreview = 0x8000 + 33;
    private const int WmAppQueue = 0x8000 + 34;

    /// <summary>回归测试用：模拟「拖出去但还没松手」（只在 FU_FLASH_DEBUG=1 时生效）。</summary>
    private const int WmAppSimulateLeave = 0x8000 + 35;

    private const int TimerFade = 1;
    private const int TimerGrace = 2;
    private const int TimerFlash = 3;
    private const int TimerPreview = 4;
    private const int TimerHit = 5;

    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTopmost = 0x00000008;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTransparent = 0x00000020;
    private const int GwlExStyle = -20;

    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;

    /// <summary>装睡时的整窗 alpha：1/255 肉眼不可见，但仍然是「非零 alpha」，能被命中。</summary>
    private const float DormantOpacity = 1f / 255f;

    /// <summary>左键按下时的「拖拽候选」判定阈值（像素）。</summary>
    private const int VkLeftButton = 0x01;

    private readonly Action _openMain;
    private readonly Action _openSettings;
    private readonly ManualResetEventSlim _ready = new(false);

    /// <summary>窗口线程真正退出后置位：设置里关掉这个功能时要等它，避免新旧实例抢静态的 _active。</summary>
    private readonly ManualResetEventSlim _closed = new(false);
    private readonly UploadQueue _queue = UploadQueue.Current;
    private readonly FileDropTarget _dropTarget;
    private DragWatcher? _watcher;

    private Thread? _thread;
    private IntPtr _hwnd;

    // 窗口类只注册一次；窗口过程用「活到进程结束」的静态委托（同简洁模式气泡的教训）。
    private static readonly WndProcDelegate StaticProc = StaticWindowProc;
    private static readonly IntPtr StaticProcPointer = Marshal.GetFunctionPointerForDelegate(StaticProc);
    private static FlashDropWindow? _active;
    private static bool _classRegistered;

    private bool _disposed;

    // 绘制快照。
    private volatile int _state = (int)FlashDropState.Dormant;
    private bool _light = true;
    private volatile int _count;
    private volatile int _percent;
    private volatile bool _configured = true;
    private int _pendingResult;
    private string _resultMessage = string.Empty;

    private float _opacity = DormantOpacity;
    private float _targetOpacity = DormantOpacity;
    private float _fadeStep;

    private int _previewStep;
    private bool _mouseDownOnSelf;
    private bool _afterDragLeave;
    private int _lastHitResult = -2;
    private bool _hitEnabled = true;


    private FlashDropWindow(Action openMain, Action openSettings)
    {
        _openMain = openMain;
        _openSettings = openSettings;
        _dropTarget = new FileDropTarget(
            accepts: () => true,
            entered: OnDropEntered,
            dropped: OnDropped,
            left: OnDropLeft);
    }

    /// <summary>闪现气泡是否已经可用。</summary>
    public bool IsReady => _hwnd != IntPtr.Zero;

    /// <summary>启动（独立 STA 线程，和简洁模式气泡互不影响）。</summary>
    public static FlashDropWindow Start(Action openMain, Action openSettings)
    {
        var window = new FlashDropWindow(openMain, openSettings);
        window._thread = new Thread(window.RunMessageLoop)
        {
            IsBackground = true,
            Name = "FileUploader.FlashDrop",
        };
        window._thread.SetApartmentState(ApartmentState.STA);
        window._thread.Start();
        window._ready.Wait(TimeSpan.FromSeconds(5));
        return window;
    }

    /// <summary>察觉到可能开始拖拽：半透明浮现一下（任意线程可调）。</summary>
    public void Peek() => Post(WmAppPeek);

    /// <summary>拖拽结束但没落到我们身上：收起（任意线程可调）。</summary>
    public void HibernateIfTransient() => Post(WmAppHibernate);

    /// <summary>开发辅助：依次演示 Peek → Armed → Uploading → Done（<c>--flash-preview</c> 用）。</summary>
    public void Preview() => Post(WmAppPreview);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Post(WmClose);
    }

    private void Post(int message)
    {
        var hwnd = _hwnd;
        if (hwnd != IntPtr.Zero)
        {
            PostMessage(hwnd, message, IntPtr.Zero, IntPtr.Zero);
        }
    }

    // ── 窗口线程 ────────────────────────────────────────────────────────

    private void RunMessageLoop()
    {
        try
        {
            RunMessageLoopCore();
        }
        catch (Exception ex)
        {
            CrashLog.Write("FlashDropWindow.RunMessageLoop", ex);
            FlashLog.Info("EXCEPTION: " + ex);
            _ready.Set();
        }
        finally
        {
            _closed.Set();
        }
    }

    /// <summary>等窗口线程真正退出（最多等 <paramref name="timeout"/>）。</summary>
    public bool WaitForClose(TimeSpan timeout) => _closed.Wait(timeout);

    private void RunMessageLoopCore()
    {
        const string className = "FileUploaderFlashDrop";

        FlashLog.Info("thread start");
        if (!_classRegistered)
        {
            var wc = new WndClassEx
            {
                cbSize = Marshal.SizeOf<WndClassEx>(),
                lpfnWndProc = StaticProcPointer,
                hInstance = GetModuleHandle(null),
                lpszClassName = className,
            };

            _classRegistered = RegisterClassEx(ref wc) != 0;
            FlashLog.Info($"RegisterClassEx -> {_classRegistered} (lastError={Marshal.GetLastWin32Error()})");
        }

        // 拖放回调要求线程先 OLE 初始化；这条线程本来就是 STA + 自己泵消息，正好合适。
        _ = DropTargetInterop.OleInitialize(IntPtr.Zero);
        _active = this;

        var size = ComputeSize();
        _light = FlashDropVisual.IsLightTheme();
        _hwnd = CreateWindowEx(
            WsExLayered | WsExToolWindow | WsExTopmost | WsExNoActivate,
            className,
            "OSS 闪现上传",
            WsPopup,
            0,
            0,
            size.Cx,
            size.Cy,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);

        FlashLog.Info($"CreateWindowEx -> 0x{_hwnd.ToInt64():X} (lastError={Marshal.GetLastWin32Error()})");
        if (_hwnd == IntPtr.Zero)
        {
            _ready.Set();
            return;
        }

        var dropResult = DropTargetInterop.RegisterDragDrop(_hwnd, _dropTarget);
        FlashLog.Info($"RegisterDragDrop -> 0x{dropResult:X8}");
        if (dropResult != 0)
        {
            CrashLog.Write("FlashDropWindow.RegisterDragDrop", new InvalidOperationException($"hr=0x{dropResult:X8}"));
        }

        Place();
        ShowWindow(_hwnd, 4 /* SW_SHOWNOACTIVATE */);
        UpdateHitTestStyle();
        SetTimer(_hwnd, TimerHit, 120, IntPtr.Zero);
        Subscribe();
        Render();

        // 拖拽探测器装在专用线程上（只泵消息、不做重活）：装在界面线程上的话，
        // 界面一卡顿就会把全系统鼠标一起拖慢。
        _watcher = new DragWatcher(Peek, HibernateIfTransient);

        _ready.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        _watcher?.Dispose();
        _watcher = null;
        Unsubscribe();
        Interlocked.Exchange(ref _hwnd, IntPtr.Zero);
        if (ReferenceEquals(_active, this))
        {
            _active = null;
        }
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
            // 隐形 + 不挡点击的关键：没在显示、且鼠标左键没按下时，让命中测试直接穿透。
            // 左键按下（有人在拖东西）时才变成可命中，拖到右下角就能落上来。
            case WmNcHitTest:
                var state = (FlashDropState)_state;
                var buttonDown = (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0;
                var hitResult = state != FlashDropState.Dormant || buttonDown ? 1 : -1;
                if (hitResult != _lastHitResult)
                {
                    _lastHitResult = hitResult;
                    FlashLog.Info($"hittest -> {(hitResult > 0 ? "HTCLIENT" : "HTTRANSPARENT")} (state={state}, lbutton={buttonDown})");
                }

                return (IntPtr)hitResult;

            case WmMouseActivate:
                return (IntPtr)3; // MA_NOACTIVATE：点了也不抢前台

            case WmAppPeek:
                if ((FlashDropState)_state == FlashDropState.Dormant)
                {
                    RefreshSnapshot();
                    SetState(FlashDropState.Peek);
                }

                return IntPtr.Zero;

            case WmAppHibernate:
                if ((FlashDropState)_state is FlashDropState.Peek or FlashDropState.Armed)
                {
                    Hibernate();
                }

                return IntPtr.Zero;

            case WmAppPreview:
                StartPreview();
                return IntPtr.Zero;

            case WmAppSimulateLeave:
                if (FlashLog.DebugEnabled)
                {
                    _afterDragLeave = true;
                    SetState(FlashDropState.Peek, graceMs: -1);
                }

                return IntPtr.Zero;

            case WmAppQueue:
                OnQueueMessage();
                return IntPtr.Zero;

            case WmTimer:
                OnTimer((int)wParam);
                return IntPtr.Zero;

            case WmSettingChange:
            case WmThemeChanged:
                _light = FlashDropVisual.IsLightTheme();
                Place();
                Render();
                return IntPtr.Zero;

            case WmDisplayChange:
                Place();
                Render();
                return IntPtr.Zero;

            case WmLeftButtonDown:
                // 只认「在气泡上按下」的点击：拖放结束后 OLE 可能补一个松手消息，
                // 那种不能当成点击，否则刚传完就把主窗口弹出来了。
                _mouseDownOnSelf = true;
                return IntPtr.Zero;

            case WmLeftButtonUp:
                var wasClick = _mouseDownOnSelf;
                _mouseDownOnSelf = false;
                if (!wasClick)
                {
                    return IntPtr.Zero;
                }

                // 显示状态下的单击：完成/上传中点开主窗口，失败/未配置直接进设置。
                if ((FlashDropState)_state is FlashDropState.Done or FlashDropState.Uploading)
                {
                    _openMain();
                }
                else if ((FlashDropState)_state == FlashDropState.Failed)
                {
                    _openSettings();
                }

                return IntPtr.Zero;

            case WmRightButtonUp:
                _openMain();
                return IntPtr.Zero;

            case WmClose:
                DestroyWindow(hWnd);
                return IntPtr.Zero;

            case WmDestroy:
                Interlocked.Exchange(ref _hwnd, IntPtr.Zero);
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // ── 拖放回调（都在本窗口线程上） ─────────────────────────────────────

    private void OnDropEntered(IReadOnlyList<string> paths)
    {
        FlashLog.Info($"DragEnter: {paths.Count} 个文件");
        _count = paths.Count;
        _afterDragLeave = false;
        SetState(FlashDropState.Armed, graceMs: -1);
    }

    private void OnDropLeft()
    {
        FlashLog.Info("DragLeave");
        if ((FlashDropState)_state == FlashDropState.Armed)
        {
            // 拖走了：退回半透明提示并一直亮着，直到这次拖拽结束（松手/放下）才收起。
            // 不能靠宽限定时器收：用户常常拖出去又拖回来，中途消失会很难受。
            _afterDragLeave = true;
            SetState(FlashDropState.Peek, graceMs: -1);
        }
    }

    private void OnDropped(IReadOnlyList<string> paths)
    {
        FlashLog.Info($"Drop: {paths.Count} 个文件 -> {string.Join(",", paths.Take(3))}");
        _afterDragLeave = false;
        RefreshSnapshot();
        SetState(FlashDropState.Uploading);
        _ = EnqueueAsync(paths);
    }

    private async Task EnqueueAsync(IReadOnlyList<string> paths)
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
                // 读不到的条目跳过（和简洁模式气泡一致）。
            }
        }

        if (items.Count > 0)
        {
            await _queue.EnqueueAsync(items);
            return;
        }

        // 一个都读不到（路径失效 / 拖来的是虚拟文件）：给个明确反馈，别让气泡一直停在「上传中」。
        _resultMessage = "读不到这些文件";
        _pendingResult = 2;
        Post(WmAppQueue);
    }

    // ── 队列订阅 ────────────────────────────────────────────────────────

    private void Subscribe()
    {
        _queue.PropertyChanged += OnQueueChanged;
        _queue.Uploaded += OnUploaded;
        _queue.Failed += OnFailed;
        _queue.Rejected += OnRejected;
    }

    private void Unsubscribe()
    {
        _queue.PropertyChanged -= OnQueueChanged;
        _queue.Uploaded -= OnUploaded;
        _queue.Failed -= OnFailed;
        _queue.Rejected -= OnRejected;
    }

    private void OnQueueChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Post(WmAppQueue);

    private void OnUploaded(UploadItem item)
    {
        _pendingResult = 1;
        Post(WmAppQueue);
    }

    private void OnFailed(UploadItem item)
    {
        _pendingResult = 2;
        Post(WmAppQueue);
    }

    private void OnRejected(string message)
    {
        _pendingResult = 3;
        _resultMessage = message;
        Post(WmAppQueue);
    }

    private void OnQueueMessage()
    {
        var state = (FlashDropState)_state;
        if (state is FlashDropState.Dormant or FlashDropState.Peek)
        {
            // 不是我们这条链路触发的上传（例如在主窗口里传的），不弹。
            return;
        }

        RefreshSnapshot();
        var pending = Interlocked.Exchange(ref _pendingResult, 0);

        if (pending == 1)
        {
            if (_queue.IsBusy)
            {
                if (state != FlashDropState.Uploading)
                {
                    SetState(FlashDropState.Uploading);
                }
                else
                {
                    Render();
                }

                return;
            }

            _resultMessage = SettingsState.Current.Value.AutoCopyUrl ? "已复制链接" : "已完成";
            SetState(FlashDropState.Done);
            return;
        }

        if (pending is 2 or 3)
        {
            if (pending == 3)
            {
                _resultMessage = "还没配置存储";
            }
            else
            {
                _resultMessage = "上传失败";
            }

            SetState(FlashDropState.Failed);
            return;
        }

        Render();
    }

    private void RefreshSnapshot()
    {
        _configured = SettingsState.Current.Value.IsConfigured;

        var items = _queue.Items;
        var active = items.Where(i => i.State is UploadState.Uploading or UploadState.Queued).ToArray();
        _count = active.Length;
        _percent = active.Length == 0 ? 0 : (int)Math.Round(active.Average(i => i.Progress));
    }

    // ── 状态机 ──────────────────────────────────────────────────────────

    /// <summary>切状态：定透明度目标、起淡入淡出、按状态挂宽限/收尾定时器。</summary>
    private void SetState(FlashDropState state, int graceMs = 0)
    {
        _state = (int)state;
        FlashLog.Info($"state -> {state}");
        KillTimer(_hwnd, TimerGrace);
        KillTimer(_hwnd, TimerFlash);

        switch (state)
        {
            case FlashDropState.Peek:
                _targetOpacity = 0.74f;
                if (graceMs >= 0)
                {
                    SetTimer(_hwnd, TimerGrace, (uint)(graceMs > 0 ? graceMs : 1800), IntPtr.Zero);
                }

                break;

            case FlashDropState.Armed:
                _targetOpacity = 1f;
                // 不设兜底定时器：一直停在「松手即上传」，直到拖走（DragLeave）或拖拽结束。
                if (graceMs >= 0)
                {
                    SetTimer(_hwnd, TimerGrace, (uint)(graceMs > 0 ? graceMs : 4000), IntPtr.Zero);
                }

                break;

            case FlashDropState.Uploading:
                _targetOpacity = 1f;
                break;

            case FlashDropState.Done:
                _targetOpacity = 1f;
                SetTimer(_hwnd, TimerFlash, 2600, IntPtr.Zero);
                break;

            case FlashDropState.Failed:
                _targetOpacity = 1f;
                SetTimer(_hwnd, TimerFlash, 3200, IntPtr.Zero);
                break;

            default:
                _targetOpacity = DormantOpacity;
                break;
        }

        if (state != FlashDropState.Dormant)
        {
            Place();
        }

        var delta = _targetOpacity - _opacity;
        _fadeStep = Math.Abs(delta) / 12f;
        if (Math.Abs(delta) < 0.001f)
        {
            _opacity = _targetOpacity;
            Render();
            return;
        }

        SetTimer(_hwnd, TimerFade, 16, IntPtr.Zero);
        UpdateHitTestStyle();
        Render();
    }

    private void Hibernate()
    {
        _afterDragLeave = false;
        Interlocked.Exchange(ref _pendingResult, 0);
        SetState(FlashDropState.Dormant);
    }

    private void OnTimer(int timerId)
    {
        switch (timerId)
        {
            case TimerFade:
                var delta = _targetOpacity - _opacity;
                if (Math.Abs(delta) <= _fadeStep || _fadeStep <= 0f)
                {
                    _opacity = _targetOpacity;
                    KillTimer(_hwnd, TimerFade);
                }
                else
                {
                    _opacity += Math.Sign(delta) * _fadeStep;
                }

                Render();
                break;

            case TimerGrace:
                KillTimer(_hwnd, TimerGrace);
                if ((FlashDropState)_state is FlashDropState.Peek or FlashDropState.Armed)
                {
                    Hibernate();
                }

                break;

            case TimerFlash:
                KillTimer(_hwnd, TimerFlash);
                Hibernate();
                break;

            case TimerPreview:
                AdvancePreview();
                break;

            case TimerHit:
                UpdateHitTestStyle();
                break;
        }
    }

    /// <summary>
    /// 装睡时给窗口加 <c>WS_EX_TRANSPARENT</c>：鼠标消息、拖放落点搜索都会直接跳过它，
    /// 零死区；只要「有东西在显示」或者「鼠标左键正按着」，就把这个样式摘掉，让拖放能落上来。
    /// <para>
    /// 不能只靠 <c>WM_NCHITTEST</c> 返回 <c>HTTRANSPARENT</c>：逐像素 alpha 的分层窗口在
    /// <c>WindowFromPoint</c> 里不走命中测试（实测），只有扩展样式这一层是可靠的。
    /// </para>
    /// </summary>
    private void UpdateHitTestStyle()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var buttonDown = (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0;

        // 拖走之后保持半透明亮着，直到这次拖拽真的结束（左键松开）才收起：
        // 用户经常拖出去又拖回来，中途消失很难受。这里用轮询兜底，不依赖钩子一定收到结束事件。
        if (_afterDragLeave && (FlashDropState)_state == FlashDropState.Peek && !buttonDown)
        {
            Hibernate();
            return;
        }

        var shouldHit = (FlashDropState)_state != FlashDropState.Dormant || buttonDown;
        if (shouldHit == _hitEnabled)
        {
            return;
        }

        _hitEnabled = shouldHit;
        var style = GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64();
        var updated = shouldHit
            ? style & ~(long)WsExTransparent
            : style | WsExTransparent;
        _ = SetWindowLongPtr(_hwnd, GwlExStyle, new IntPtr(updated));
        FlashLog.Info($"hit-test style -> {shouldHit} (lbutton={buttonDown})");
    }

    /// <summary>开发演示：Peek → Armed → Uploading → Done → 收起。</summary>
    private void StartPreview()
    {
        _previewStep = 0;
        _count = 2;
        _percent = 43;
        SetState(FlashDropState.Peek, graceMs: 1200);
        SetTimer(_hwnd, TimerPreview, 1200, IntPtr.Zero);
    }

    private void AdvancePreview()
    {
        KillTimer(_hwnd, TimerPreview);
        _previewStep++;
        switch (_previewStep)
        {
            case 1:
                _count = 2;
                _afterDragLeave = false;
                SetState(FlashDropState.Armed, graceMs: -1);
                SetTimer(_hwnd, TimerPreview, 1300, IntPtr.Zero);
                break;

            case 2:
                _count = 2;
                _percent = 43;
                SetState(FlashDropState.Uploading);
                SetTimer(_hwnd, TimerPreview, 1100, IntPtr.Zero);
                break;

            case 3:
                _resultMessage = "已复制链接";
                SetState(FlashDropState.Done);
                SetTimer(_hwnd, TimerPreview, 1500, IntPtr.Zero);
                break;

            default:
                Hibernate();
                break;
        }
    }

    // ── 摆放与绘制 ──────────────────────────────────────────────────────

    private double Scale()
    {
        var dpi = _hwnd != IntPtr.Zero ? GetDpiForWindow(_hwnd) : 96;
        return dpi <= 0 ? 1.0 : dpi / 96.0;
    }

    private Size32 ComputeSize()
    {
        var scale = Scale();
        var state = (FlashDropState)_state;
        var (title, subtitle, _) = FlashDropVisual.Texts(state, _configured, _count, _percent, _resultMessage);
        var width = state == FlashDropState.Dormant
            ? FlashDropVisual.MinPillWidth
            : FlashDropVisual.MeasureWidth((float)scale, title, subtitle);

        return new Size32
        {
            Cx = (int)Math.Round((width + (FlashDropVisual.ShadowPadding * 2)) * scale),
            Cy = (int)Math.Round((FlashDropVisual.PillHeight + (FlashDropVisual.ShadowPadding * 2)) * scale),
        };
    }

    /// <summary>摆到「鼠标所在显示器」工作区的右下角（工作区已经排除了任务栏）。</summary>
    private void Place()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var size = ComputeSize();
        var point = new Point32();
        GetCursorPos(out point);

        var monitor = MonitorFromPoint(point, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        var work = GetMonitorInfo(monitor, ref info) ? info.rcWork : new Rect32 { Right = 1920, Bottom = 1080 };

        var scale = Scale();
        var margin = (int)Math.Round(20 * scale);
        var bottom = (int)Math.Round(8 * scale);
        var x = work.Right - size.Cx - margin;
        var y = work.Bottom - size.Cy - bottom;
        x = Math.Max(work.Left, x);
        y = Math.Max(work.Top, y);

        SetWindowPos(_hwnd, IntPtr.Zero, x, y, size.Cx, size.Cy, SwpNoActivate | SwpNoZOrder);
    }

    private void Render()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var size = ComputeSize();
        using var bitmap = new Bitmap(size.Cx, size.Cy, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

            var state = (FlashDropState)_state;
            if (state == FlashDropState.Dormant)
            {
                // 整窗 alpha=1：肉眼看不见，但像素是非零 alpha，命中测试仍然有效。
                graphics.Clear(Color.FromArgb(1, 0, 0, 0));
                UpdateLayered(bitmap, size);
                return;
            }

            graphics.Clear(Color.Transparent);

            var scale = (float)Scale();
            var pad = (float)(FlashDropVisual.ShadowPadding * scale);
            var pill = new RectangleF(pad, pad, size.Cx - (pad * 2), size.Cy - (pad * 2));
            var (title, subtitle, icon) = FlashDropVisual.Texts(state, _configured, _count, _percent, _resultMessage);

            FlashDropVisual.DrawShadow(graphics, pill, scale, _light, new Size(size.Cx, size.Cy));
            FlashDropVisual.DrawSurface(graphics, pill, scale, _light, state);
            FlashDropVisual.DrawContent(graphics, pill, scale, _light, state, title, subtitle, icon, _percent);
        }

        if (_opacity < 0.999f)
        {
            using var faded = new Bitmap(size.Cx, size.Cy, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(faded))
            using (var attributes = new ImageAttributes())
            {
                attributes.SetColorMatrix(new ColorMatrix { Matrix33 = _opacity });
                graphics.DrawImage(bitmap, new Rectangle(0, 0, size.Cx, size.Cy), 0, 0, size.Cx, size.Cy, GraphicsUnit.Pixel, attributes);
            }

            UpdateLayered(faded, size);
            return;
        }

        UpdateLayered(bitmap, size);
    }



    private void UpdateLayered(Bitmap bitmap, Size32 size)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        var oldObject = SelectObject(memoryDc, hBitmap);

        var rect = new Rect32();
        GetWindowRect(_hwnd, out rect);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect32 rcMonitor;
        public Rect32 rcWork;
        public uint dwFlags;
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
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point32 point, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern UIntPtr SetTimer(IntPtr hWnd, int id, uint elapse, IntPtr callback);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hWnd, int id);

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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}

/// <summary>临时排查日志（只在开发期用，提交前删除）。</summary>
internal static class FlashLog
{
    /// <summary>只有显式打开调试环境变量时才写日志（回归脚本用）。</summary>
    public static bool DebugEnabled { get; } = Environment.GetEnvironmentVariable("FU_FLASH_DEBUG") == "1";

    private static readonly object Gate = new();

    public static void Info(string message)
    {
        if (!DebugEnabled)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fu-flash.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
        }
    }
}
