using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace FileUploader.App;

/// <summary>
/// 给菜单卡片画一圈柔和阴影。
/// <para>
/// 为什么不用 DWM 自带的窗口阴影：菜单窗口是 WinUI 无边框弹层，
/// Reactor 会在激活前后反复重设窗口 chrome（实测 <c>WS_THICKFRAME</c> 一直被打回去），
/// 拿不到 DWM 阴影。所以改成"自己画"：
/// 在菜单窗口正下方垫一扇 <c>WS_EX_LAYERED | WS_EX_TRANSPARENT</c> 的分层窗口，
/// 画一张高斯模糊的圆角阴影位图（强度对齐主窗口那种淡淡的系统阴影），再贴上去。
/// 这扇窗口点击穿透、不抢焦点，随菜单一起创建和销毁。
/// </para>
/// </summary>
internal static class MenuShadow
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private const uint UlwAlpha = 0x00000002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpShowWindow = 0x0040;

    /// <summary>阴影外扩距离（像素）。</summary>
    private const int Margin = 20;

    /// <summary>模糊半径（像素）：越大越柔。</summary>
    private const int BlurRadius = 9;

    /// <summary>阴影最深处的不透明度（0~1）：系统窗口阴影大概就是这个量级。</summary>
    private const double Strength = 0.15;

    /// <summary>阴影向下偏移（像素），模拟顶部光源。</summary>
    private const int OffsetY = 3;

    private const string ClassName = "FileUploader.MenuShadow";

    private static readonly WndProcDelegate ShadowProc = (hWnd, msg, wParam, lParam) => DefWindowProc(hWnd, msg, wParam, lParam);
    private static bool _classRegistered;

    /// <summary>在菜单窗口 <paramref name="menuHwnd"/> 下方画阴影（<paramref name="bounds"/> 是菜单窗口的屏幕矩形）。</summary>
    public static IntPtr Create(IntPtr menuHwnd, Rectangle32 bounds, int cornerRadius)
    {
        EnsureClass();

        var cardWidth = bounds.Right - bounds.Left;
        var cardHeight = bounds.Bottom - bounds.Top;
        var width = cardWidth + (Margin * 2);
        var height = cardHeight + (Margin * 2);

        var layer = CreateWindowEx(
            WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate,
            ClassName,
            string.Empty,
            WsPopup,
            bounds.Left - Margin,
            bounds.Top - Margin,
            width,
            height,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);

        if (layer == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        Paint(layer, width, height, cardWidth, cardHeight, cornerRadius);

        // 垫在菜单窗口正下方：hWndInsertAfter = menuHwnd 表示"排在它后面"。
        // 注意不能带 SWP_NOZORDER，否则插入位置被忽略，阴影窗口会盖在菜单上面。
        _ = SetWindowPos(layer, menuHwnd, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize);
        _ = ShowWindow(layer, 4 /* SW_SHOWNOACTIVATE */);

        return layer;
    }

    /// <summary>销毁阴影窗口。</summary>
    public static void Destroy(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            _ = DestroyWindow(hwnd);
        }
    }

    private static void Paint(IntPtr hwnd, int width, int height, int cardWidth, int cardHeight, int cornerRadius)
    {
        var alpha = BuildBlurredAlpha(width, height, cardWidth, cardHeight, cornerRadius);
        var stride = width * 4;
        var buffer = new byte[stride * height];

        for (var i = 0; i < alpha.Length; i++)
        {
            // 阴影是纯黑；预乘 alpha 后 RGB 仍是 0，只写 A 通道即可。
            buffer[(i * 4) + 3] = alpha[i];
        }

        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = 40,
                Width = width,
                Height = -height, // 自上而下，省一次翻转
                Planes = 1,
                BitCount = 32,
                Compression = 0,
            },
        };
        var dib = CreateDIBSection(memoryDc, ref info, 0, out var bits, IntPtr.Zero, 0);

        try
        {
            Marshal.Copy(buffer, 0, bits, buffer.Length);

            var old = SelectObject(memoryDc, dib);
            var size = new Size32 { Cx = width, Cy = height };
            var source = new Point32 { X = 0, Y = 0 };
            var blend = new BlendFunction
            {
                BlendOp = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = 1, // AC_SRC_ALPHA
            };

            // 目标位置传 IntPtr.Zero：窗口本身已经摆在正确坐标上了。
            _ = UpdateLayeredWindow(hwnd, screenDc, IntPtr.Zero, ref size, memoryDc, ref source, 0, ref blend, UlwAlpha);
            _ = SelectObject(memoryDc, old);
        }
        finally
        {
            _ = DeleteObject(dib);
            _ = DeleteDC(memoryDc);
            _ = ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>画圆角矩形遮罩 → 两次盒式模糊 → 按强度缩放，得到平滑的阴影 alpha。</summary>
    private static byte[] BuildBlurredAlpha(int width, int height, int cardWidth, int cardHeight, int cornerRadius)
    {
        var mask = new byte[width * height];

        using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        {
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                using var path = RoundedRect(new Rectangle(Margin, Margin + OffsetY, cardWidth, cardHeight), cornerRadius);
                using var brush = new SolidBrush(Color.FromArgb(255, 255, 255, 255));
                graphics.FillPath(brush, path);
            }

            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[data.Stride];
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    for (var x = 0; x < width; x++)
                    {
                        mask[(y * width) + x] = row[(x * 4) + 3];
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        var blurred = BoxBlur(mask, width, height, BlurRadius);
        blurred = BoxBlur(blurred, width, height, BlurRadius);

        var result = new byte[blurred.Length];
        for (var i = 0; i < blurred.Length; i++)
        {
            result[i] = (byte)Math.Clamp((int)Math.Round(blurred[i] * Strength), 0, 255);
        }

        return result;
    }

    /// <summary>可分离盒式模糊（前缀和实现），两次叠加近似高斯。</summary>
    private static byte[] BoxBlur(byte[] source, int width, int height, int radius)
    {
        var horizontal = new byte[source.Length];
        var result = new byte[source.Length];
        var window = (radius * 2) + 1;

        for (var y = 0; y < height; y++)
        {
            var sum = 0;
            var row = y * width;
            for (var x = -radius; x <= radius; x++)
            {
                sum += source[row + Math.Clamp(x, 0, width - 1)];
            }

            for (var x = 0; x < width; x++)
            {
                horizontal[row + x] = (byte)(sum / window);
                sum -= source[row + Math.Clamp(x - radius, 0, width - 1)];
                sum += source[row + Math.Clamp(x + radius + 1, 0, width - 1)];
            }
        }

        for (var x = 0; x < width; x++)
        {
            var sum = 0;
            for (var y = -radius; y <= radius; y++)
            {
                sum += horizontal[(Math.Clamp(y, 0, height - 1) * width) + x];
            }

            for (var y = 0; y < height; y++)
            {
                result[(y * width) + x] = (byte)(sum / window);
                sum -= horizontal[(Math.Clamp(y - radius, 0, height - 1) * width) + x];
                sum += horizontal[(Math.Clamp(y + radius + 1, 0, height - 1) * width) + x];
            }
        }

        return result;
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
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

    private static void EnsureClass()
    {
        if (_classRegistered)
        {
            return;
        }

        var wc = new WndClassEx
        {
            cbSize = Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(ShadowProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = ClassName,
        };

        _ = RegisterClassEx(ref wc);
        _classRegistered = true;
    }

    internal struct Rectangle32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
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
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ColorsUsed;
        public int ColorsImportant;
    }

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

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr dstDc, IntPtr dstPoint, ref Size32 size, IntPtr srcDc,
        ref Point32 srcPoint, int colorKey, ref BlendFunction blend, uint flags);
}
