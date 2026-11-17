# 复现/回归：简洁模式下气泡被快速点击时，主窗口不应该被「补隐藏」再藏起来。
#
# 做法：用 --compact 启动（会走 EnterCompact → HideMainWindow 的延迟补隐藏），
# 立刻给气泡窗口 PostMessage 一个 WM_LBUTTONUP（等价于点一下气泡），
# 然后观察主窗口是否一直可见。
param(
    [string]$Exe = "src\FileUploader.App\bin\Release\net10.0-windows10.0.22621.0\win-x64\FileUploader.exe",
    [int]$ObserveMs = 3000
)

$ErrorActionPreference = "Stop"

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class B {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    public static IntPtr Find(uint pid, string cls) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            uint owner; GetWindowThreadProcessId(h, out owner);
            if (owner == pid) {
                var c = new StringBuilder(128); GetClassNameW(h, c, c.Capacity);
                if (c.ToString() == cls) { found = h; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@

Get-Process -Name FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$proc = Start-Process -FilePath (Resolve-Path $Exe) -ArgumentList "--compact" -WorkingDirectory (Get-Location).Path -PassThru
$pid2 = [uint32]$proc.Id
$started = Get-Date
Write-Output "started pid=$pid2 with --compact"

# 等气泡窗口出现
$bubble = [IntPtr]::Zero
$deadline = (Get-Date).AddSeconds(20)
while ((Get-Date) -lt $deadline) {
    $bubble = [B]::Find($pid2, "FileUploaderBubbleWindow")
    if ($bubble -ne [IntPtr]::Zero) { break }
    Start-Sleep -Milliseconds 60
}

if ($bubble -eq [IntPtr]::Zero) { throw "没等到气泡窗口" }
$elapsed = [int]((Get-Date) - $started).TotalMilliseconds
Write-Output "bubble hwnd=$bubble (出现于启动后 ${elapsed}ms)"

$main = [B]::Find($pid2, "WinUIDesktopWin32WindowClass")
Write-Output "main hwnd=$main  点击前可见=$([B]::IsWindowVisible($main))"

# 点一下气泡（等价 WM_LBUTTONUP）
$r = New-Object B+RECT
[void][B]::GetWindowRect($bubble, [ref]$r)
$lparam = [IntPtr]((([int](($r.Top + $r.Bottom) / 2) -band 0xFFFF) -shl 16) -bor (([int](($r.Left + $r.Right) / 2)) -band 0xFFFF))
[void][B]::PostMessage($bubble, 0x0202, [IntPtr]::Zero, $lparam)
$clicked = Get-Date
Write-Output "已点击气泡（启动后 $([int]($clicked - $started).TotalMilliseconds)ms）"

# 观察主窗口可见性：任何一次「可见 → 隐藏」都算被补隐藏
$samples = @()
$hiddenAfterShow = $false
$seenVisible = $false
for ($t = 0; $t -lt $ObserveMs; $t += 150) {
    Start-Sleep -Milliseconds 150
    $visible = [B]::IsWindowVisible($main)
    if ($visible) { $seenVisible = $true }
    elseif ($seenVisible) { $hiddenAfterShow = $true }
    $samples += "$([int]((Get-Date) - $clicked).TotalMilliseconds)ms=$visible"
}

Write-Output ("采样: " + ($samples -join "  "))
Write-Output "RESULT seenVisible=$seenVisible hiddenAfterShow=$hiddenAfterShow"

Get-Process -Id $pid2 -ErrorAction SilentlyContinue | Stop-Process -Force
if ($hiddenAfterShow) {
    Write-Output "FAIL 主窗口显示后又被隐藏（BUG 复现）"
    exit 1
}
Write-Output "PASS 主窗口一直保持可见"
exit 0
