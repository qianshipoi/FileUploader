using System.Runtime.InteropServices;

namespace FileUploader.App;

/// <summary>
/// 原生 Win32 弹出菜单（系统菜单外观，和资源管理器/其它托盘程序一致）。
/// </summary>
internal static class NativeMenu
{
    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;
    private const uint MfGrayed = 0x0001;
    private const uint TpmLeftAlign = 0x0000;
    private const uint TpmTopAlign = 0x0000;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmNoNotify = 0x0080;
    private const uint TpmReturnCmd = 0x0100;
    private const int WmNull = 0x0000;

    /// <summary>菜单项：分隔符用 <see cref="Separator"/>。</summary>
    internal sealed record Item(string Text, Action? Click = null, bool IsEnabled = true, bool IsDefault = false)
    {
        public static Item Separator { get; } = new("-", null, false);
    }

    /// <summary>在鼠标位置弹出菜单并执行选中项。</summary>
    public static void Show(IReadOnlyList<Item> items)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var index = 0u;
            var defaultIndex = uint.MaxValue;
            foreach (var item in items)
            {
                if (item == Item.Separator || item.Text == "-")
                {
                    AppendMenu(menu, MfSeparator, UIntPtr.Zero, string.Empty);
                    continue;
                }

                var flags = MfString | (item.IsEnabled ? 0 : MfGrayed);
                AppendMenu(menu, flags, (UIntPtr)(index + 1), item.Text);
                if (item.IsDefault)
                {
                    defaultIndex = index;
                }

                index++;
            }

            if (defaultIndex != uint.MaxValue)
            {
                // fByPos = 1：按位置指定（0 基），和上面的 AppendMenu 顺序一致。
                SetMenuDefaultItem(menu, defaultIndex, 1);
            }

            if (!GetCursorPos(out var point))
            {
                return;
            }

            // 必须用本线程自己的窗口做 owner：托盘右键时前台窗口常常是 explorer，
            // 拿它当 owner 会让 TrackPopupMenu 直接失败（菜单不出现）。
            var owner = EnsureOwnerWindow();
            SetForegroundWindow(owner);

            var command = TrackPopupMenu(
                menu,
                TpmLeftAlign | TpmTopAlign | TpmRightButton | TpmNoNotify | TpmReturnCmd,
                point.X,
                point.Y,
                0,
                owner,
                IntPtr.Zero);

            // 让菜单彻底消失（经典做法）。
            PostMessage(owner, WmNull, IntPtr.Zero, IntPtr.Zero);

            if (command <= 0)
            {
                return;
            }

            var chosen = items.Where(i => i != Item.Separator && i.Text != "-").ElementAtOrDefault(command - 1);
            chosen?.Click?.Invoke();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static IntPtr _ownerWindow;

    /// <summary>创建一个 0x0 的隐藏窗口，专门给菜单当 owner（属于当前线程）。</summary>
    private static IntPtr EnsureOwnerWindow()
    {
        if (_ownerWindow != IntPtr.Zero)
        {
            return _ownerWindow;
        }

        const string className = "FileUploader.MenuOwner";
        var instance = GetModuleHandle(null);
        var wc = new WndClassEx
        {
            cbSize = Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(OwnerProc),
            hInstance = instance,
            lpszClassName = className,
        };

        RegisterClassEx(ref wc);

        _ownerWindow = CreateWindowEx(
            0,
            className,
            "FileUploaderMenuOwner",
            unchecked((int)0x80000000), // WS_POPUP
            0, 0, 0, 0,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);

        return _ownerWindow;
    }

    private static readonly WndProcDelegate OwnerProc = (hWnd, msg, wParam, lParam) => DefWindowProc(hWnd, msg, wParam, lParam);

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string text);

    [DllImport("user32.dll")]
    private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr owner, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
