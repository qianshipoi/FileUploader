# 托盘菜单「轻量关闭」回归：
# 1) --menu-preview 弹菜单 -> 在菜单外面点一下鼠标 -> 菜单应关闭；
# 2) 再弹一次 -> 按 Esc -> 菜单应关闭。
$ErrorActionPreference = "Stop"

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class DismissTest {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, IntPtr e);
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

$menuTitle = "菜单"
$exe = "src\FileUploader.App\bin\Release\net10.0-windows10.0.22621.0\win-x64\FileUploader.exe"
if (-not (Test-Path $exe)) { Write-Output "FAIL: exe not found - run from repo root"; exit 1 }

function Start-Menu {
    Get-Process -Name FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    Start-Process -FilePath (Resolve-Path $exe) -ArgumentList "--menu-preview" -WorkingDirectory (Get-Location).Path
    $proc = $null
    for ($i = 0; $i -lt 100; $i++) {
        Start-Sleep -Milliseconds 150
        $proc = Get-Process -Name FileUploader -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($proc) { break }
    }
    for ($i = 0; $i -lt 100; $i++) {
        $h = [DismissTest]::Find([uint32]$proc.Id, $menuTitle)
        if ($h -ne [IntPtr]::Zero -and [DismissTest]::IsWindowVisible($h)) { return @{ Proc = $proc; Hwnd = $h } }
        Start-Sleep -Milliseconds 150
    }
    return $null
}

$results = @()

# --- 场景 1：点菜单外面 ---
$s = Start-Menu
if (-not $s) { Write-Output "FAIL: menu did not open (scenario 1)"; exit 1 }
$r = New-Object DismissTest+RECT
[void][DismissTest]::GetWindowRect($s.Hwnd, [ref]$r)
Write-Output "menu at $($r.Left),$($r.Top) $($r.Right - $r.Left)x$($r.Bottom - $r.Top)"
$outsideX = $r.Left - 80
$outsideY = $r.Bottom + 60
[void][DismissTest]::SetCursorPos($outsideX, $outsideY)
Start-Sleep -Milliseconds 250
[DismissTest]::mouse_event(0x02, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 60
[DismissTest]::mouse_event(0x04, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 900
$gone1 = [DismissTest]::Find([uint32]$s.Proc.Id, $menuTitle) -eq [IntPtr]::Zero
Write-Output "scenario 1 (click outside): menu closed=$gone1"
$results += $gone1

# --- 场景 2：按 Esc ---
$s = Start-Menu
if (-not $s) { Write-Output "FAIL: menu did not open (scenario 2)"; exit 1 }
[DismissTest]::keybd_event(0x1B, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 80
[DismissTest]::keybd_event(0x1B, 0, 2, [IntPtr]::Zero)
Start-Sleep -Milliseconds 900
$gone2 = [DismissTest]::Find([uint32]$s.Proc.Id, $menuTitle) -eq [IntPtr]::Zero
Write-Output "scenario 2 (Escape): menu closed=$gone2"
$results += $gone2

if ($results -notcontains $false) { Write-Output "RESULT PASS"; exit 0 }
Write-Output "RESULT FAIL"; exit 1
