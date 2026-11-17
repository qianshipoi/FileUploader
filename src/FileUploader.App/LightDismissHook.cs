using System.Runtime.InteropServices;

namespace FileUploader.App;

/// <summary>
/// 菜单的「点到别处就关」。
/// <para>
/// 不能只靠窗口的 <c>Deactivated</c>：无边框弹层常常拿不到前台（
/// <c>ActivateOnOpen</c> 被系统的前台锁挡掉），既然从未激活，就永远不会失活。
/// 所以这里装一个 WH_MOUSE_LL / WH_KEYBOARD_LL 低级钩子，只在菜单打开期间存在：
/// </para>
/// <list type="bullet">
/// <item>鼠标左/右/中键按下，且落点在菜单窗口之外 → 关闭菜单（点击照常传给目标窗口）；</item>
/// <item>按 Esc → 关闭菜单，并把这次按键吃掉。</item>
/// </list>
/// </summary>
internal sealed class LightDismissHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;

    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmRightButtonDown = 0x0204;
    private const int WmMiddleButtonDown = 0x0207;

    private const int VkEscape = 0x1B;

    private readonly IntPtr _hwnd;
    private readonly Action _dismiss;
    private readonly HookProc _mouseProc;
    private readonly HookProc _keyboardProc;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private bool _disposed;

    public LightDismissHook(IntPtr hwnd, Action dismiss)
    {
        _hwnd = hwnd;
        _dismiss = dismiss;

        // 委托必须被字段持有，否则会被 GC 回收导致回调崩溃。
        _mouseProc = OnMouse;
        _keyboardProc = OnKeyboard;

        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, IntPtr.Zero, 0);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, IntPtr.Zero, 0);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_mouseHook != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && !_disposed)
        {
            var message = (int)wParam;
            if (message is WmLeftButtonDown or WmRightButtonDown or WmMiddleButtonDown)
            {
                var info = Marshal.PtrToStructure<MouseHookStruct>(lParam);
                if (!IsInsideWindow(info.Point))
                {
                    _dismiss();

                    // 不吞这次点击：用户就是想点那个窗口，菜单让开就好。
                }
            }
        }

        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private IntPtr OnKeyboard(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && !_disposed)
        {
            var message = (int)wParam;
            if (message is WmKeyDown or WmSysKeyDown)
            {
                var info = Marshal.PtrToStructure<KeyboardHookStruct>(lParam);
                if (info.VirtualKeyCode == VkEscape)
                {
                    _dismiss();
                    return new IntPtr(1); // 吃掉 Esc，别让底层程序也收到
                }
            }
        }

        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private bool IsInsideWindow(Point32 point)
    {
        if (_hwnd == IntPtr.Zero || !GetWindowRect(_hwnd, out var rect))
        {
            return false;
        }

        return point.X >= rect.Left && point.X <= rect.Right && point.Y >= rect.Top && point.Y <= rect.Bottom;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

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
        public Point32 Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookStruct
    {
        public uint VirtualKeyCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);
}
