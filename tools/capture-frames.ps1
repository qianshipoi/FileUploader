# ASCII-only helper: capture N frames of a window (CopyFromScreen) while optionally invoking a UIA script.
param(
    [string]$ProcessName = "FileUploader",
    [string]$TitleCodes = "5B58,50A8,914D,7F6E",   # 存储配置
    [int]$Frames = 10,
    [int]$IntervalMs = 150,
    [string]$InvokeScript = "",
    [string]$OutPrefix = "frame"
)

Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class Cap {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
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
$hwnd = [Cap]::Find([uint32]$proc.Id, $title)
if ($hwnd -eq [IntPtr]::Zero) { Write-Output "FAIL window not found: $title"; exit 1 }

[void][Cap]::ShowWindow($hwnd, 9)
[void][Cap]::SetWindowPos($hwnd, [IntPtr](-1), 0, 0, 0, 0, 0x0041 -bor 0x0002)   # TOPMOST | SHOWWINDOW | NOSIZE
Start-Sleep -Milliseconds 600

$rect = New-Object Cap+RECT
[void][Cap]::GetWindowRect($hwnd, [ref]$rect)
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top

function Shoot([string]$path) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bmp.Size)
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return (Get-FileHash $path).Hash.Substring(0, 8)
}

if ($InvokeScript -ne "") {
    $psi = Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $InvokeScript -PassThru -WindowStyle Hidden
}

$hashes = @()
for ($i = 1; $i -le $Frames; $i++) {
    $hashes += Shoot (Join-Path $env:TEMP ("{0}-{1}.png" -f $OutPrefix, $i))
    Start-Sleep -Milliseconds $IntervalMs
}
Write-Output ("frames: " + ($hashes -join " "))

[void][Cap]::SetWindowPos($hwnd, [IntPtr](-2), 0, 0, 0, 0, 0x0002)   # NOTOPMOST
