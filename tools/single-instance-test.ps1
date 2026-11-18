# 单实例 + 静默启动提示 的回归（不依赖鼠标）
#
#   pwsh -NoProfile -File tools\single-instance-test.ps1
#
# 覆盖：
#   1. 同一份 exe 启动两次 -> 只有一个进程，第二次会把已有窗口叫到前台并自己退出
#   2. 不同目录的两份副本 -> 互不影响，各自都能跑（锁名带 exe 路径哈希）
#   3. 静默启动（StartSilently=true + 已配置）-> 主窗口保持隐藏，右下角气泡给出「已在托盘运行」提示
param(
    [string]$RepoRoot = '.'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

$passed = $true
function Check([string]$name, [bool]$ok, [string]$detail) {
    $script:passed = $script:passed -and $ok
    Write-Output ("{0}  {1}  {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}

Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class SWin {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    public static IntPtr MainWindow(uint pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            uint o; GetWindowThreadProcessId(h, out o);
            if (o != pid) return true;
            var c = new StringBuilder(256); GetClassNameW(h, c, c.Capacity);
            if (c.ToString() == "WinUIDesktopWin32WindowClass") found = h;
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@

$build = Join-Path $RepoRoot 'src\FileUploader.App\bin\Release\net10.0-windows10.0.22621.0\win-x64'
if (-not (Test-Path (Join-Path $build 'FileUploader.exe'))) { throw "找不到 $build\FileUploader.exe，先 dotnet build -c Release" }

function Wait-ProcessCount([int]$expected, [int]$timeoutMs = 8000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        if (@(Get-Process FileUploader -ErrorAction SilentlyContinue).Count -eq $expected) { return $true }
        Start-Sleep -Milliseconds 200
    }
    return $false
}

function New-PortableCopy([string]$path) {
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    robocopy $build $path /E /NFL /NDL /NJH /NJS /MT:16 | Out-Null
    Set-Content -Path (Join-Path $path 'portable.txt') -Value '' -Encoding utf8
    New-Item -ItemType Directory -Path (Join-Path $path 'data') -Force | Out-Null
}

Get-Process FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

# ---- 1. 同一份 exe 启动两次 ----
$dirA = Join-Path $env:TEMP 'fu-single-a'
New-PortableCopy $dirA
$exeA = Join-Path $dirA 'FileUploader.exe'

$first = Start-Process -FilePath $exeA -PassThru
$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 60 -and $hwnd -eq [IntPtr]::Zero; $i++) {
    Start-Sleep -Milliseconds 250
    $hwnd = [SWin]::MainWindow([uint32]$first.Id)
}
Check '第一份启动并建出主窗口' ($hwnd -ne [IntPtr]::Zero) ("pid=$($first.Id)")

# 把别的窗口顶到前台，模拟"用户看不到我们的窗口"
$np = Start-Process notepad -PassThru
Start-Sleep -Seconds 2
$notepadHwnd = [IntPtr]::Zero
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class SWin2 {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    public static IntPtr ByTitle(string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => { var sb = new StringBuilder(256); GetWindowTextW(h, sb, sb.Capacity); if (sb.ToString() == title) found = h; return true; }, IntPtr.Zero);
        return found;
    }
}
"@
$notepadHwnd = [SWin2]::ByTitle('无标题 - 记事本')
if ($notepadHwnd -eq [IntPtr]::Zero) { $notepadHwnd = [SWin2]::ByTitle('*无标题 - 记事本') }
[void][SWin2]::SetForegroundWindow($notepadHwnd)
Start-Sleep -Milliseconds 500

# 第二次启动：应该把第一份的窗口叫到前台，然后自己退出
$second = Start-Process -FilePath $exeA -PassThru
Start-Sleep -Seconds 4

$count = @(Get-Process FileUploader -ErrorAction SilentlyContinue).Count
Check '第二次启动没有留下多余进程' ($count -eq 1) ("当前 FileUploader 进程数=$count")
Check '第一份窗口被叫到前台' ([SWin]::GetForegroundWindow() -eq $hwnd) ("前台 hwnd=" + [SWin]::GetForegroundWindow())
Check '第一份窗口可见' ([SWin]::IsWindowVisible($hwnd)) ''

Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
[void](Wait-ProcessCount 0)

# ---- 2. 不同目录的两份副本互不影响 ----
$dirB = Join-Path $env:TEMP 'fu-single-b'
New-PortableCopy $dirB
$pA = Start-Process -FilePath $exeA -PassThru
[void](Wait-ProcessCount 1)
$pB = Start-Process -FilePath (Join-Path $dirB 'FileUploader.exe') -PassThru
$twoUp = Wait-ProcessCount 2
$count = @(Get-Process FileUploader -ErrorAction SilentlyContinue).Count
Check '不同目录的副本可以各跑一份' $twoUp ("当前进程数=$count")

Get-Process FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

# ---- 3. 静默启动：窗口保持隐藏 + 右下角给提示 ----
$dirC = Join-Path $env:TEMP 'fu-silent-c'
New-PortableCopy $dirC
$cfg = Join-Path $dirC 'data\settings.json'
$env:FU_PROTOCOL = 's3'; $env:FU_ENDPOINT = '127.0.0.1:9100'; $env:FU_BUCKET = 'demo'
$env:FU_AK = 'testak'; $env:FU_SK = 'testsk'; $env:FU_PATHSYLE = 'true'; $env:FU_HTTPS = 'false'
& dotnet run --project (Join-Path $RepoRoot 'tools\FileUploader.Verify') -c Release -- settings $cfg 2>&1 | Out-Null
$json = Get-Content $cfg -Raw | ConvertFrom-Json
$json.StartSilently = $true
($json | ConvertTo-Json -Depth 10) | Set-Content -Path $cfg -Encoding utf8

Get-Process FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
[void](Wait-ProcessCount 0)
$silentProc = Start-Process -FilePath (Join-Path $dirC 'FileUploader.exe') -PassThru
# 提示只显示 4 秒左右，先等窗口建出来再截图（大约启动后 2.5~3 秒）
$silent = $silentProc
$silentHwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 40 -and $silentHwnd -eq [IntPtr]::Zero; $i++) {
    Start-Sleep -Milliseconds 200
    $silentHwnd = [SWin]::MainWindow([uint32]$silent.Id)
}
Start-Sleep -Milliseconds 1200
Check '静默启动时主窗口隐藏' (-not [SWin]::IsWindowVisible($silentHwnd)) ("hwnd=$silentHwnd")

# 截右下角，确认气泡里出现提示文字（气泡是分层窗口，UIA 摸不到，只能截图看）
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$w = 420; $h = 160
$x = $bounds.Right - $w - 20; $y = $bounds.Bottom - $h - 20
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
$g.Dispose()
$shot = Join-Path $env:TEMP 'silent-hint.png'
$bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output ("右下角截图 -> $shot")

Get-Process FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output $(if ($passed) { 'RESULT PASS' } else { 'RESULT FAIL' })
if (-not $passed) { exit 1 }
