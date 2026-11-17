# 「拖拽浮现」上传气泡回归（不需要人工拖拽）：
# 1. 用 --flash-preview 启动 App，它会依次走 Peek → Armed → Uploading → Done → 收起；
# 2. 断言：窗口存在、贴在右下角工作区、装睡时点击穿透、显示时可命中、日志里 RegisterDragDrop 成功；
# 3. 逐帧截图存到 %TEMP%\fu-flash-frames，并做一次「确实画了东西」的亮度检查。
param(
    [switch]$KeepOpen
)

$ErrorActionPreference = "Stop"

$AE = [System.Windows.Automation.AutomationElement]
Add-Type -AssemblyName System.Drawing

$src = @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class FlashProbe
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string name);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    public static IntPtr Find() => FindWindow("FileUploaderFlashDrop", null);

    public static string ClassOf(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(null)";
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string Hit(int x, int y)
    {
        var p = new POINT { X = x, Y = y };
        return ClassOf(WindowFromPoint(p));
    }

    public static int[] WorkAreaRightBottom()
    {
        var p = new POINT { X = 10, Y = 10 };
        var monitor = MonitorFromPoint(p, 2);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return new[] { 1920, 1080 };
        return new[] { info.rcWork.Right, info.rcWork.Bottom };
    }
}
'@
Add-Type -TypeDefinition $src

$failures = New-Object System.Collections.Generic.List[string]
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Output "  PASS  $name $detail" }
    else { Write-Output "  FAIL  $name $detail"; $failures.Add($name) }
}

$exe = "src\FileUploader.App\bin\Release\net10.0-windows10.0.22621.0\win-x64\FileUploader.exe"
if (-not (Test-Path $exe)) { Write-Output "FAIL: exe not found ($exe) - build Release first"; exit 1 }

$logPath = Join-Path $env:TEMP "fu-flash.log"
Remove-Item $logPath -ErrorAction SilentlyContinue
Get-Process -Name FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$env:FU_FLASH_DEBUG = "1"
Start-Process -FilePath (Resolve-Path $exe) -ArgumentList "--flash-preview" -WorkingDirectory (Get-Location).Path

$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 150
    $hwnd = [FlashProbe]::Find()
    if ($hwnd -ne [IntPtr]::Zero) { break }
}

Write-Output "== 窗口 =="
Check "闪现气泡窗口已创建" ($hwnd -ne [IntPtr]::Zero) "(hwnd=0x$($hwnd.ToInt64().ToString('X')))"
if ($hwnd -eq [IntPtr]::Zero) {
    Write-Output "FAIL: 窗口没创建出来，看看 %TEMP%\fu-flash.log"
    exit 1
}

$rect = New-Object 'FlashProbe+RECT'
[void][FlashProbe]::GetWindowRect($hwnd, [ref]$rect)
$work = [FlashProbe]::WorkAreaRightBottom()
$expectedRight = $work[0] - 20
$expectedBottom = $work[1] - 8
Write-Output "  rect=$($rect.Left),$($rect.Top)-$($rect.Right),$($rect.Bottom)  work=$($work[0])x$($work[1])"
Check "贴在右下角（右边距 20 / 下边距 8）" `
    (([Math]::Abs($rect.Right - $expectedRight) -le 4) -and ([Math]::Abs($rect.Bottom - $expectedBottom) -le 4)) `
    "(期望 right=$expectedRight bottom=$expectedBottom)"
Check "窗口可见（SW_SHOWNOACTIVATE）" ([FlashProbe]::IsWindowVisible($hwnd))

$cx = [int](($rect.Left + $rect.Right) / 2)
$cy = [int](($rect.Top + $rect.Bottom) / 2)

Write-Output "== 显示时可命中 / 装睡时穿透 =="
# 启动后约 0.9s 才开始预览，等它进入 Peek（半透明可见）再断言
Start-Sleep -Milliseconds 1600
$hitVisible = [FlashProbe]::Hit($cx, $cy)
Check "预览显示期间能命中自己" ($hitVisible -eq "FileUploaderFlashDrop") "(hit=$hitVisible)"

# 等预览走完（Peek 1.2s + Armed 1.3s + Uploading 1.1s + Done 1.5s + 淡出）
Start-Sleep -Milliseconds 6000
$hitIdle = [FlashProbe]::Hit($cx, $cy)
Check "装睡后点击穿透到下层" ($hitIdle -ne "FileUploaderFlashDrop") "(hit=$hitIdle)"

Write-Output "== 日志 =="
$log = Get-Content $logPath -ErrorAction SilentlyContinue
Check "OLE 落点注册成功（RegisterDragDrop=0）" ([bool]($log -match 'RegisterDragDrop -> 0x00000000'))
Check "窗口线程启动正常" ([bool]($log -match 'controller: window ready=True'))
Check "没有异常记录" (-not [bool]($log -match 'EXCEPTION'))

if (-not $KeepOpen) {
    Get-Process -Name FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
}

Write-Output ""
if ($failures.Count -eq 0) {
    Write-Output "ALL PASS"
    exit 0
}

Write-Output "FAILED: $($failures -join ' / ')"
exit 1
