using System.Runtime.InteropServices;

namespace FileUploader.App;

/// <summary>
/// 「有人开始拖东西了」的探测器：只听系统的 <c>EVENT_SYSTEM_DRAGDROPSTART/END</c>。
/// <para>
/// 之前还装了一个低级鼠标钩子做兜底（左键按下 + 位移超过阈值 + 前台线程有鼠标捕获），
/// 但那个判据分不清「拖文件」和「拖选文字」——用户选中一段文本时气泡也会冒出来，所以删掉了。
/// </para>
/// <para>
/// 现在只有真正的 OLE 拖放才会触发它。即使某些来源不发这个事件也不影响功能：
/// 气泡本来就会在「文件真的拖进落点区域」时由 <see cref="FileDropTarget"/> 的 <c>DragEnter</c> 亮起来，
/// 这一路是拿拖放数据判断的，不会误报。
/// </para>
/// </summary>
internal sealed class DragWatcher : IDisposable
{
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;

    private const uint EventSystemDragDropStart = 0x000E;
    private const uint EventSystemDragDropEnd = 0x000F;

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    private readonly Action _maybeStarted;
    private readonly Action _ended;
    private readonly WinEventProc _winEventProc;

    private IntPtr _hook;
    private bool _disposed;

    public DragWatcher(Action maybeStarted, Action ended)
    {
        _maybeStarted = maybeStarted;
        _ended = ended;

        // 委托必须被字段持有，否则会被 GC 回收导致回调崩溃（简洁模式气泡那次教训）。
        _winEventProc = OnWinEvent;

        try
        {
            _hook = SetWinEventHook(
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
            _hook = IntPtr.Zero;
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
        if (_hook != IntPtr.Zero)
        {
            _ = UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        FlashLog.Info($"WinEvent: 0x{eventType:X} (0x0E=拖拽开始, 0x0F=结束)");
        if (eventType == EventSystemDragDropStart)
        {
            _maybeStarted();
        }
        else if (eventType == EventSystemDragDropEnd)
        {
            _ended();
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);
}
