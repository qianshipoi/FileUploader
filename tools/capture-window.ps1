# ASCII-only: capture a window of the app by title (hex char codes) with a small margin.
param(
    [string]$ProcessName = "FileUploader",
    [string]$TitleCodes = "83DC,5355",   # 菜单
    [string]$Out = "menu-shot.png",
    [int]$Margin = 14
)

Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class WinShot {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    public static IntPtr Find(uint pid, string title) {
        IntPtr f = IntPtr.Zero;
        EnumWindows((h, l) => {
            uint o; GetWindowThreadProcessId(h, out o);
            if (o == pid) {
                var sb = new StringBuilder(256); GetWindowTextW(h, sb, sb.Capacity);
                if (sb.ToString() == title) f = h;
            }
            return true;
        }, IntPtr.Zero);
        return f;
    }
}
"@

Add-Type -AssemblyName System.Drawing

$title = ""
foreach ($code in $TitleCodes.Split(',')) { $title += [string][char][Convert]::ToInt32($code, 16) }

$proc = Get-Process -Name $ProcessName -ErrorAction Stop | Select-Object -First 1
$hwnd = [WinShot]::Find([uint32]$proc.Id, $title)
if ($hwnd -eq [IntPtr]::Zero) { Write-Output "FAIL: window '$title' not found"; exit 1 }

$rect = New-Object WinShot+RECT
[void][WinShot]::GetWindowRect($hwnd, [ref]$rect)
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
Write-Output "window rect: $($rect.Left),$($rect.Top) ${w}x${h} visible=$([WinShot]::IsWindowVisible($hwnd))"

$bmp = New-Object System.Drawing.Bitmap ($w + $Margin * 2), ($h + $Margin * 2)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Left - $Margin, $rect.Top - $Margin, 0, 0, $bmp.Size)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "captured -> $Out"
