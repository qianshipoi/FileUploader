# 检测菜单窗口周围有没有阴影渐变：采样窗口右/下/左外侧的像素亮度。
param(
    [string]$Exe = "src\FileUploader.App\bin\Release\net10.0-windows10.0.22621.0\win-x64\FileUploader.exe"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class ShadowProbe {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
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

$menuTitle = ([string][char]0x83DC) + ([string][char]0x5355)

Get-Process -Name FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Start-Process -FilePath (Resolve-Path $Exe) -ArgumentList "--menu-preview" -WorkingDirectory (Get-Location).Path

$proc = $null
for ($i = 0; $i -lt 100; $i++) {
    Start-Sleep -Milliseconds 150
    $proc = Get-Process -Name FileUploader -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($proc) { break }
}

$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 100; $i++) {
    $hwnd = [ShadowProbe]::Find([uint32]$proc.Id, $menuTitle)
    if ($hwnd -ne [IntPtr]::Zero -and [ShadowProbe]::IsWindowVisible($hwnd)) { break }
    Start-Sleep -Milliseconds 150
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Output "FAIL: menu window not found"; exit 1 }

$rect = New-Object ShadowProbe+RECT
[void][ShadowProbe]::GetWindowRect($hwnd, [ref]$rect)
Write-Output "menu rect: $($rect.Left),$($rect.Top) $($rect.Right - $rect.Left)x$($rect.Bottom - $rect.Top)"

function RowValues([int]$x, [int]$y, [int]$n) {
    $bmp = New-Object System.Drawing.Bitmap $n, 1
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
    $g.Dispose()
    $values = @()
    for ($i = 0; $i -lt $n; $i += 2) { $values += $bmp.GetPixel($i, 0).R }
    $bmp.Dispose()
    return ($values -join " ")
}

function ColValues([int]$x, [int]$y, [int]$n) {
    $bmp = New-Object System.Drawing.Bitmap 1, $n
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
    $g.Dispose()
    $values = @()
    for ($i = 0; $i -lt $n; $i += 2) { $values += $bmp.GetPixel(0, $i).R }
    $bmp.Dispose()
    return ($values -join " ")
}

$style = [ShadowProbe]::GetWindowLongPtr($hwnd, -16).ToInt64()
$ex = [ShadowProbe]::GetWindowLongPtr($hwnd, -20).ToInt64()
Write-Output ("style=0x{0:X8} exstyle=0x{1:X8} caption={2} thickframe={3} toolwindow={4}" -f $style, $ex, (($style -band 0x00C00000) -eq 0x00C00000), (($style -band 0x00040000) -ne 0), (($ex -band 0x80) -ne 0))

$midY = [int](($rect.Top + $rect.Bottom) / 2)
$midX = [int](($rect.Left + $rect.Right) / 2)
Write-Output "right  : $(RowValues $rect.Right $midY 40)"
Write-Output "below  : $(ColValues $midX $rect.Bottom 40)"
Write-Output "left   : $(RowValues ($rect.Left - 40) $midY 40)"
Write-Output "above  : $(ColValues $midX ($rect.Top - 40) 40)"
