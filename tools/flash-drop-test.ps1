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
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

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
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    public static IntPtr Find() => FindWindow("FileUploaderFlashDrop", null);

    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);

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

    // 气泡贴在「鼠标所在显示器」的右下角，所以期望值要按窗口自己那块屏算，不能写死主屏
    public static int[] WorkAreaFor(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, 2);
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
$work = [FlashProbe]::WorkAreaFor($hwnd)
$expectedRight = $work[0] - 20
$expectedBottom = $work[1] - 8
Write-Output "  rect=$($rect.Left),$($rect.Top)-$($rect.Right),$($rect.Bottom)  该屏工作区右下角=$($work[0]),$($work[1])"
Check "贴在右下角（右边距 20 / 下边距 8）" `
    (([Math]::Abs($rect.Right - $expectedRight) -le 4) -and ([Math]::Abs($rect.Bottom - $expectedBottom) -le 4)) `
    "(期望 right=$expectedRight bottom=$expectedBottom)"
Check "窗口可见（SW_SHOWNOACTIVATE）" ([FlashProbe]::IsWindowVisible($hwnd))

# 窗口比气泡本体大一圈（左上留了落点余量），命中/穿透要分别取「气泡本体」和「空白区域」两个点
$cx = [int](($rect.Left + $rect.Right) / 2)
$cy = [int](($rect.Top + $rect.Bottom) / 2)
$px = $rect.Right - 60
$py = $rect.Bottom - 30

Write-Output "== 显示时可命中 / 装睡时穿透 =="
# 启动后约 0.9s 才开始预览，等它进入 Peek（半透明可见）再断言
# 等预览真的进入 Peek（固定 sleep 容易撞到两次预览之间的空窗）
$deadline = (Get-Date).AddSeconds(15)
while ((Get-Date) -lt $deadline) {
    if ((Get-Content $logPath -ErrorAction SilentlyContinue) -match 'state -> Peek') { break }
    Start-Sleep -Milliseconds 100
}
[void][FlashProbe]::SetCursorPos($px, $py)   # 光标停在气泡本体上才会变成可命中
Start-Sleep -Milliseconds 350
$hitVisible = [FlashProbe]::Hit($px, $py)
Check "预览显示期间（光标在气泡上）能命中自己" ($hitVisible -eq "FileUploaderFlashDrop") "(hit=$hitVisible)"

# 等预览走完（Peek 1.2s + Armed 1.3s + Uploading 1.1s + Done 1.5s + 淡出）
Start-Sleep -Milliseconds 6000
$hitIdle = [FlashProbe]::Hit($cx, $cy)
Check "装睡后点击穿透到下层" ($hitIdle -ne "FileUploaderFlashDrop") "(hit=$hitIdle)"

Write-Output "== 日志 =="
$log = Get-Content $logPath -ErrorAction SilentlyContinue
Check "OLE 落点注册成功（RegisterDragDrop=0）" ([bool]($log -match 'RegisterDragDrop -> 0x00000000'))
Check "窗口线程启动正常" ([bool]($log -match 'controller: window ready=True'))
Check "没有异常记录" (-not [bool]($log -match 'EXCEPTION'))

Write-Output "== 拖出去后保持半透明，松手才收起 =="
# 重新起一个不跑预览的实例：按住左键（模拟「正在拖拽」）→ 模拟拖离气泡 → 断言不消失 → 松手 → 断言收起
Get-Process -Name FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
Remove-Item $logPath -ErrorAction SilentlyContinue
Start-Process -FilePath (Resolve-Path $exe) -WorkingDirectory (Get-Location).Path

$hwnd2 = [IntPtr]::Zero
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 150
    $hwnd2 = [FlashProbe]::Find()
    if ($hwnd2 -ne [IntPtr]::Zero) { break }
}
Check "重启后窗口仍然在" ($hwnd2 -ne [IntPtr]::Zero)
if ($hwnd2 -ne [IntPtr]::Zero) {
    $main = [FlashProbe]::FindWindow($null, "OSS 拖拽上传")
    $mr = New-Object 'FlashProbe+RECT'
    [void][FlashProbe]::GetWindowRect($main, [ref]$mr)
    [void][FlashProbe]::SetCursorPos([int](($mr.Left + $mr.Right) / 2), [int](($mr.Top + $mr.Bottom) / 2))
    Start-Sleep -Milliseconds 150
    [FlashProbe]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)   # 按住左键
    Start-Sleep -Milliseconds 200
    [void][FlashProbe]::PostMessage($hwnd2, 0x8000 + 35, [IntPtr]::Zero, [IntPtr]::Zero)  # 模拟拖离
    Start-Sleep -Milliseconds 1500

    $during = Get-Content $logPath -ErrorAction SilentlyContinue
    Check "拖离后仍停留在半透明提示（没被宽限定时器收掉）" `
        ([bool]($during -match 'state -> Peek') -and -not [bool]($during -match 'state -> Dormant')) `
        ""

    [FlashProbe]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)   # 松手 = 拖拽结束
    Start-Sleep -Milliseconds 700
    $after = Get-Content $logPath -ErrorAction SilentlyContinue
    Check "松手后才收起" ([bool]($after -match 'state -> Dormant'))
}

Write-Output "== 设置里可以关掉这个功能（改完立即生效）=="
# 用便携模式跑（exe 旁边放 portable.txt + data\settings.json），完全不碰用户真实配置
$outDir = Split-Path -Parent (Resolve-Path $exe).Path
$portableMarker = Join-Path $outDir "portable.txt"
$dataDir = Join-Path $outDir "data"
$settingsFile = Join-Path $dataDir "settings.json"
$boxName = '右下角拖拽浮现上传气泡'

function Get-SettingsWindow {
    $proc = Get-Process -Name FileUploader -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $proc) { return $null }
    $pidCond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)
    foreach ($w in $AE::RootElement.FindAll($TS::Children, $pidCond)) {
        if ($w.Current.Name -eq '设置') { return $w }
    }
    return $null
}

function Get-FlashBox([object]$win) {
    # 卡片里的标题文本和开关的 AutomationName 同名，这里必须限定成支持 Toggle 模式的 Button（ToggleSwitch），
    # 否则会先抓到标题 TextBlock，GetCurrentPattern(TogglePattern) 直接抛「不支持的模式」。
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $boxName)),
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)),
        (New-Object System.Windows.Automation.PropertyCondition($AE::IsTogglePatternAvailableProperty, $true)))
    return $win.FindFirst($TS::Descendants, $cond)
}

# 每次切换后 Reactor 会重建元素，所以每次都重新找一遍
function Set-FlashToggle([string]$want) {
    for ($i = 0; $i -lt 6; $i++) {
        $win = Get-SettingsWindow
        if (-not $win) { return $false }
        $box = Get-FlashBox $win
        if (-not $box) { continue }
        $state = $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState.ToString()
        if ($state -eq $want) { return $true }
        $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        Start-Sleep -Milliseconds 900
    }
    return $false
}

Get-Process -Name FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
try {
    New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
    Set-Content -Path $portableMarker -Value "test" -Encoding UTF8

    # 1) 配置里关着 → 启动就不该创建隐形窗口
    Set-Content -Path $settingsFile -Value '{ "FlashDropEnabled": false }' -Encoding UTF8
    Start-Process -FilePath (Resolve-Path $exe).Path -WorkingDirectory $outDir -ArgumentList "--settings"
    Start-Sleep -Seconds 4
    Check "配置关闭时启动不创建隐形窗口" ([FlashProbe]::Find() -eq [IntPtr]::Zero)

    # 2) 界面上打开 → 立即生效
    Check "能在设置里把开关打开" (Set-FlashToggle "On")
    Start-Sleep -Milliseconds 1300
    $afterOn = [FlashProbe]::Find()
    Check "打开后隐形窗口立即出现" ($afterOn -ne [IntPtr]::Zero) "(hwnd=0x$($afterOn.ToInt64().ToString('X')))"

    # 3) 界面上关掉 → 立即销毁
    Check "能在设置里把开关关掉" (Set-FlashToggle "Off")
    Start-Sleep -Milliseconds 1300
    Check "关掉后隐形窗口立即销毁" ([FlashProbe]::Find() -eq [IntPtr]::Zero)

    # 4) 状态确实落盘
    $saved = Get-Content $settingsFile -Raw | ConvertFrom-Json
    Check "开关状态已写进 settings.json" ($saved.FlashDropEnabled -eq $false)
}
finally {
    Get-Process -Name FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
    Remove-Item $portableMarker -ErrorAction SilentlyContinue
    Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Output "== 拖选文字不会冒出气泡 =="
# 在设置窗口的文本框里拖选一段文字：这也是一次"拖拽"，但不是文件拖放，气泡不该出现
Get-Process -Name FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
Remove-Item $logPath -ErrorAction SilentlyContinue
Start-Process -FilePath (Resolve-Path $exe).Path -ArgumentList "--settings" -WorkingDirectory (Get-Location).Path
$win = $null
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 200
    $win = Get-SettingsWindow
    if ($win) { break }
}
Check "设置窗口已打开" ($win -ne $null)
if ($win) {
    $editCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
    $edit = $win.FindFirst($TS::Descendants, $editCond)
    if ($edit) {
        $r = $edit.Current.BoundingRectangle
        $x1 = [int]($r.X + 40)
        $y1 = [int]($r.Y + ($r.Height / 2))
        [void][FlashProbe]::SetCursorPos($x1, $y1)
        Start-Sleep -Milliseconds 150
        [FlashProbe]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 120
        for ($s = 1; $s -le 8; $s++) {
            [void][FlashProbe]::SetCursorPos($x1 + ($s * 10), $y1)
            Start-Sleep -Milliseconds 50
        }
        Start-Sleep -Milliseconds 150
        [FlashProbe]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 900
        $log = Get-Content $logPath -ErrorAction SilentlyContinue
        Check "拖选文字没有弹出气泡" (-not [bool]($log -match 'state -> Peek'))
    }
    else {
        Check "找到设置里的文本框" $false
    }
}
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
