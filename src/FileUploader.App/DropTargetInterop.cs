using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace FileUploader.App;

/// <summary>
/// OLE 拖放落点的互操作层：<see cref="IDropTarget"/> 定义 + 从 <c>IDataObject</c> 里取文件路径。
/// <para>
/// 闪现气泡用它而不是 <c>DragAcceptFiles</c>：后者只在「松手」时送一个 <c>WM_DROPFILES</c>，
/// 拿不到 <c>DragEnter</c>，也就没法在「拖到上面时高亮、不是文件时立刻缩回去」。
/// </para>
/// </summary>
internal static class DropTargetInterop
{
    /// <summary>不接收。</summary>
    public const uint DropEffectNone = 0;

    /// <summary>复制（拖拽时显示「+」光标）。</summary>
    public const uint DropEffectCopy = 1;

    private const short CfHDrop = 15;

    [DllImport("ole32.dll")]
    public static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    public static extern int RegisterDragDrop(IntPtr hwnd, [MarshalAs(UnmanagedType.Interface)] IDropTarget target);

    [DllImport("ole32.dll")]
    public static extern int RevokeDragDrop(IntPtr hwnd);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint index, System.Text.StringBuilder? buffer, uint length);

    /// <summary>
    /// 从拖放数据对象里读出文件路径。不是文件（文本、图片、Outlook 附件这类虚拟文件）返回空列表。
    /// <para>
    /// 走 <c>CF_HDROP</c>：Explorer 拖文件过来时这条最稳（已用真实资源管理器拖拽验证）。
    /// 虚拟文件（只有 FileGroupDescriptor、没有文件系统路径）拿不到，会返回空。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> ReadFilePaths(object? dataObject)
    {
        if (dataObject is not IDataObject data)
        {
            return Array.Empty<string>();
        }

        var format = new FORMATETC
        {
            cfFormat = CfHDrop,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL,
        };

        STGMEDIUM medium = default;
        try
        {
            if (data.QueryGetData(ref format) != 0)
            {
                return Array.Empty<string>();
            }

            data.GetData(ref format, out medium);
        }
        catch (Exception ex)
        {
            FlashLog.Info("ReadFilePaths: CF_HDROP 读取失败 " + ex.Message);
            return Array.Empty<string>();
        }

        try
        {
            var count = DragQueryFile(medium.unionmember, 0xFFFFFFFF, null, 0);
            var paths = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                var length = DragQueryFile(medium.unionmember, i, null, 0);
                if (length == 0)
                {
                    continue;
                }

                var buffer = new System.Text.StringBuilder((int)length + 1);
                DragQueryFile(medium.unionmember, i, buffer, (uint)buffer.Capacity);
                paths.Add(buffer.ToString());
            }

            return paths;
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }
}

/// <summary>POINTL：OLE 拖放回调里的屏幕坐标。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct DropPoint
{
    public int X;
    public int Y;
}

/// <summary>OLE 的拖放落点接口（oleidl.h 里的 <c>IDropTarget</c>）。</summary>
[ComImport]
[Guid("00000122-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDropTarget
{
    /// <summary>拖拽进入窗口。</summary>
    [PreserveSig]
    int DragEnter([MarshalAs(UnmanagedType.Interface)] object? dataObject, uint keyState, DropPoint point, ref uint effect);

    /// <summary>拖拽在窗口内移动。</summary>
    [PreserveSig]
    int DragOver(uint keyState, DropPoint point, ref uint effect);

    /// <summary>拖拽离开窗口。</summary>
    [PreserveSig]
    int DragLeave();

    /// <summary>在窗口内松手。</summary>
    [PreserveSig]
    int Drop([MarshalAs(UnmanagedType.Interface)] object? dataObject, uint keyState, DropPoint point, ref uint effect);
}

/// <summary>
/// 可被 OLE 回调的落点实现。必须是 public + ComVisible：
/// <see cref="DropTargetInterop.RegisterDragDrop"/> 要在这上面建一个 CCW 交给系统。
/// 所有回调都在注册它的那个线程（界面线程）上触发。
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class FileDropTarget : IDropTarget
{
    private readonly Action<IReadOnlyList<string>> _entered;
    private readonly Action<IReadOnlyList<string>> _dropped;
    private readonly Action _left;
    private readonly Action _rejected;
    private readonly Func<bool> _accepts;

    public FileDropTarget(
        Func<bool> accepts,
        Action<IReadOnlyList<string>> entered,
        Action<IReadOnlyList<string>> dropped,
        Action left,
        Action rejected)
    {
        _accepts = accepts;
        _entered = entered;
        _dropped = dropped;
        _left = left;
        _rejected = rejected;
    }

    /// <inheritdoc />
    public int DragEnter(object? dataObject, uint keyState, DropPoint point, ref uint effect)
    {
        var paths = DropTargetInterop.ReadFilePaths(dataObject);
        FlashLog.Info($"DragEnter: paths={paths.Count} accepts={_accepts()}");
        if (paths.Count == 0 || !_accepts())
        {
            // 不是文件：立刻让窗口让开，别把这个拖拽的落点吞掉。
            _rejected();
            effect = DropTargetInterop.DropEffectNone;
            return 0;
        }

        effect = DropTargetInterop.DropEffectCopy;
        _entered(paths);
        return 0;
    }

    /// <inheritdoc />
    public int DragOver(uint keyState, DropPoint point, ref uint effect)
    {
        effect = DropTargetInterop.DropEffectCopy;
        return 0;
    }

    /// <inheritdoc />
    public int DragLeave()
    {
        _left();
        return 0;
    }

    /// <inheritdoc />
    public int Drop(object? dataObject, uint keyState, DropPoint point, ref uint effect)
    {
        var paths = DropTargetInterop.ReadFilePaths(dataObject);
        FlashLog.Info($"Drop: paths={paths.Count}");
        if (paths.Count == 0)
        {
            effect = DropTargetInterop.DropEffectNone;
            return 0;
        }

        effect = DropTargetInterop.DropEffectCopy;
        _dropped(paths);
        return 0;
    }
}
