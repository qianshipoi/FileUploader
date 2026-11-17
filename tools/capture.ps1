param(
    [string]$ProcessName = "FileUploader",
    [string]$OutFile = "tools/screenshot.png",
    [string]$TitleLike = "",
    [switch]$CopyFromScreen
)

Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class WinCap {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);

    public static List<string> ListWindows(uint pid) {
        var result = new List<string>();
        EnumWindows((h, l) => {
            uint owner;
            GetWindowThreadProcessId(h, out owner);
            if (owner == pid) {
                var sb = new StringBuilder(512);
                GetWindowTextW(h, sb, sb.Capacity);
                if (sb.Length > 0) {
                    result.Add(h.ToInt64().ToString() + "|" + (IsWindowVisible(h) ? "1" : "0") + "|" + sb.ToString());
                }
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
"@

[WinCap]::SetProcessDPIAware() | Out-Null

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw "未找到进程: $ProcessName" }

$windows = [WinCap]::ListWindows([uint32]$proc.Id)
if (-not $windows -or $windows.Count -eq 0) { throw "进程 $ProcessName 没有顶层窗口" }

$chosen = $null
foreach ($entry in $windows) {
    $parts = $entry.Split('|', 3)
    $title = $parts[2]
    if ([string]::IsNullOrEmpty($TitleLike) -or $title -like $TitleLike) {
        $chosen = $parts
        break
    }
}

Write-Output "windows: $($windows -join ' ; ')"
if (-not $chosen) { throw "没有匹配标题 '$TitleLike' 的窗口" }

$hwnd = [IntPtr][int64]$chosen[0]
Write-Output "chosen: $($chosen[2]) visible=$($chosen[1])"

[WinCap]::ShowWindow($hwnd, 9) | Out-Null
Start-Sleep -Milliseconds 500

$rect = New-Object WinCap+RECT
[WinCap]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -le 0 -or $height -le 0) { throw "窗口尺寸无效: ${width}x${height}" }

$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)

if ($CopyFromScreen) {
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
}
else {
    $hdc = $graphics.GetHdc()
    $ok = [WinCap]::PrintWindow($hwnd, $hdc, 2)
    $graphics.ReleaseHdc($hdc)
    if (-not $ok) { throw "PrintWindow 失败" }
}

$graphics.Dispose()

$dir = Split-Path -Parent $OutFile
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$bitmap.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

Write-Output "captured ${width}x${height} -> $OutFile"
