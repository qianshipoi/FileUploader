using System.Runtime.InteropServices;

namespace FileUploader.App;

/// <summary>
/// 「有人开始拖东西了」的探测器。两条信号并行：
/// <list type="number">
/// <item>系统的 <c>EVENT_SYSTEM_DRAGDROPSTART/END</c>：真拖拽时由拖拽源窗口发出，最准、开销最低；</item>
/// <item>低级鼠标钩子兜底：左键按下 + 位移超过阈值 + 前台线程处于鼠标捕获态且不在「拖动窗口」循环里。</item>
/// </list>
/// <para>
/// 它只负责「可能开始了 / 结束了」；拖的是不是文件、要不要真的出现，由 <see cref="FileDropTarget"/> 在
/// <c>DragEnter</c> 里用数据判断 —— 所以这里误报的代价只是气泡闪一下。
/// </para>
/// </summary>
internal sealed class DragWatcher : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmMouseMove = 0x0200;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonUp = 0x0202;

    private const int VkLeftButton = 0x01;

    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;

    private const uint EventSystemDragDropStart = 0x000E;
    private const uint EventSystemDragDropEnd = 0x000F;

    /// <summary>位移超过这么多像素才算「在拖」，避免单击抖动误报。</summary>
    private const int DragThreshold = 6;

    private const uint GuiInMoveSize = 0x0002;

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    private readonly Action _maybeStarted;
    private readonly Action _ended;
    private readonly WinEventProc _winEventProc;
    private readonly HookProc _mouseProc;

    private IntPtr _winEventHook;
    private IntPtr _mouseHook;
    private bool _disposed;

    private bool _down;
    private bool _fired;
    private Point32 _start;

    public DragWatcher(Action maybeStarted, Action ended)
    {
        _maybeStarted = maybeStarted;
        _ended = ended;

        // 委托必须被字段持有，否则会被 GC 回收导致回调崩溃（简洁模式气泡那次教训）。
        _winEventProc = OnWinEvent;
        _mouseProc = OnMouse;

        try
        {
            _winEventHook = SetWinEventHook(
                EventSystemDragDropStart,
                EventSystemDragDropEnd,
                IntPtr.Zero,
                _winEventProc,
                0,
                0,
                WineventOutOfContext | WineventSkipOwnProcess);
        }
        catch (Exception)
        {
            _winEventHook = IntPtr.Zero;
        }

        try
        {
            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, IntPtr.Zero, 0);
        }
        catch (Exception)
        {
            _mouseHook = IntPtr.Zero;
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
        if (_winEventHook != IntPtr.Zero)
        {
            _ = UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (eventType == EventSystemDragDropStart)
        {
            _maybeStarted();
        }
        else if (eventType == EventSystemDragDropEnd)
        {
            _ended();
        }
    }

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        // 回调里只做几个字段判断，任何 UI/IO 都投递给窗口线程去做，避免低级钩子超时被系统摘掉。
        if (code >= 0)
        {
            var message = (int)wParam;
            var info = Marshal.PtrToStructure<MouseHookStruct>(lParam);

            if (message == WmLeftButtonDown)
            {
                _down = true;
                _fired = false;
                _start = info.pt;
            }
            else if (message == WmMouseMove && _down && !_fired)
            {
                var dx = Math.Abs(info.pt.X - _start.X);
                var dy = Math.Abs(info.pt.Y - _start.Y);
                if (Math.Max(dx, dy) >= DragThreshold)
                {
                    _fired = true;
                    if (LooksLikeDrag())
                    {
                        _maybeStarted();
                    }
                }
            }
            else if (message == WmLeftButtonUp)
            {
                _down = false;
                if (_fired)
                {
                    _fired = false;
                    _ended();
                }
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// <summary>
    /// 粗判「这次左键拖动像不像拖放」：前台线程持有鼠标捕获，且不在窗口移动/缩放循环里。
    /// 选文本也会命中，但这只会让气泡半透明闪一下，随后被数据判断纠正。
    /// </summary>
    private static bool LooksLikeDrag()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(foreground, out var processId);
        if (processId == (uint)Environment.ProcessId)
        {
            return false;
        }

        var thread = GetWindowThreadProcessId(foreground, out _);
        var info = new GuiThreadInfo { cbSize = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(thread, ref info))
        {
            return false;
        }

        if ((info.flags & GuiInMoveSize) != 0)
        {
            return false;
        }

        return info.hwndCapture != IntPtr.Zero;
    }

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
    private struct MouseHookStruct
    {
        public Point32 pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr extraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public Rect32 rcCaret;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
}
