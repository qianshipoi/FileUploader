# 托盘激活回归：验证「主窗口压在别的窗口下面时，点托盘能把它叫到前台」。
#
#   pwsh -NoProfile -File tools/tray-activate-test.ps1 -Exe "%TEMP%\fu-tray-test\FileUploader.exe"
#
# 不用真实鼠标点托盘（托盘图标在通知区域/溢出里，UIA 摸不到）：
# 用 --bring-preview 启动参数让程序在 5 秒后调用一次「和点托盘完全相同的 ShowMainWindow」，
# 这期间把记事本顶到前台当遮挡物，然后断言前台窗口变成了本程序。
# 旧实现（只 ShowWindow + Window.Activate）在这一步会失败：实测普通 SetForegroundWindow 被系统拒绝。
param(
    [Parameter(Mandatory = $true)][string]$Exe
)

$ErrorActionPreference = 'Stop'

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class TrayFg {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint t, bool doAttach);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    public static readonly IntPtr NotImplemented = IntPtr.Zero;

    public static string Title(IntPtr h) {
        var sb = new StringBuilder(256);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static IntPtr FindByClass(uint pid, string cls) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            uint owner;
            GetWindowThreadProcessId(h, out owner);
            if (owner != pid) return true;
            var sb = new StringBuilder(128);
            GetClassNameW(h, sb, sb.Capacity);
            if (sb.ToString() == cls) found = h;
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static IntPtr FindByTitle(string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            var sb = new StringBuilder(256);
            GetWindowTextW(h, sb, sb.Capacity);
            if (sb.ToString() == title) found = h;
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>从后台进程抢前台（测试里用它来把记事本摆到前面当遮挡物）。</summary>
    public static bool ForceForeground(IntPtr target) {
        if (SetForegroundWindow(target) && GetForegroundWindow() == target) return true;
        var fg = GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero);
        uint myThread = GetCurrentThreadId();
        bool attached = false;
        try {
            if (fgThread != 0 && fgThread != myThread) attached = AttachThreadInput(myThread, fgThread, true);
            BringWindowToTop(target);
            SetForegroundWindow(target);
        } finally {
            if (attached) AttachThreadInput(myThread, fgThread, false);
        }
        return GetForegroundWindow() == target;
    }
}
"@

$passed = $true
function Check([string]$name, [bool]$ok, [string]$detail) {
    $script:passed = $script:passed -and $ok
    Write-Output ("{0}  {1}  {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}

# 清理上一轮
Get-Process FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

# 给回归脚本 12 秒准备时间：窗口出现 → 摆好遮挡物 → 断言基线，然后程序自己把自己叫到前台。
$env:FU_BRING_PREVIEW_MS = '12000'
$launch = Get-Date
$proc = Start-Process -FilePath (Resolve-Path $Exe) -ArgumentList '--bring-preview' -PassThru

# 等主窗口出现
$app = [IntPtr]::Zero
for ($i = 0; $i -lt 60 -and $app -eq [IntPtr]::Zero; $i++) {
    Start-Sleep -Milliseconds 200
    $app = [TrayFg]::FindByClass([uint32]$proc.Id, 'WinUIDesktopWin32WindowClass')
}
if ($app -eq [IntPtr]::Zero) { throw '主窗口没出现' }

# 等窗口真的显示出来（Reactor 在内容挂载后才 Show），否则测的就不是"压在别的窗口下面"这个场景。
for ($i = 0; $i -lt 50 -and -not [TrayFg]::IsWindowVisible($app); $i++) {
    Start-Sleep -Milliseconds 200
}
Write-Output ("主窗口 hwnd={0} title='{1}' visible={2}" -f $app, [TrayFg]::Title($app), [TrayFg]::IsWindowVisible($app))

# 用记事本当遮挡物，把它摆到前台（此时本程序在后面）
$np = Start-Process notepad -PassThru
Start-Sleep -Seconds 2
$notepad = [TrayFg]::FindByTitle('无标题 - 记事本')
if ($notepad -eq [IntPtr]::Zero) {
    $notepad = [TrayFg]::FindByTitle('*无标题 - 记事本')
}
if ($notepad -eq [IntPtr]::Zero) { throw '记事本窗口没找到' }

[void][TrayFg]::ForceForeground($notepad)
Start-Sleep -Milliseconds 400
$baseline = [TrayFg]::GetForegroundWindow()
Check '遮挡物就位（本程序在后）' ($baseline -eq $notepad) ("前台='" + [TrayFg]::Title($baseline) + "'")
Check '本程序窗口仍可见（只是被压住）' ([TrayFg]::IsWindowVisible($app)) ''
Check '此时前台不是本程序' ($baseline -ne $app) ''

# --bring-preview 在启动 5 秒后触发；等它跑完再看前台是谁
$remaining = 13.0 - ((Get-Date) - $launch).TotalSeconds
if ($remaining -gt 0) { Start-Sleep -Milliseconds ([int]($remaining * 1000)) }

$fg = [IntPtr]::Zero
for ($i = 0; $i -lt 30; $i++) {
    $fg = [TrayFg]::GetForegroundWindow()
    if ($fg -eq $app) { break }
    Start-Sleep -Milliseconds 200
}
Check '激活后前台是本程序' ($fg -eq $app) ("前台='" + [TrayFg]::Title($fg) + "' hwnd=$fg")

# 再点一次不应该把窗口弄丢（幂等）
[void][TrayFg]::ForceForeground($notepad)
Start-Sleep -Milliseconds 300
Check '窗口仍然可见' ([TrayFg]::IsWindowVisible($app)) ''

# 收尾
Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Output $(if ($passed) { 'RESULT PASS' } else { 'RESULT FAIL' })
if (-not $passed) { exit 1 }
