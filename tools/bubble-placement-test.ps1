# 简洁模式气泡位置记忆回归：
# A) 内存里给定一个合法位置 -> 启动后气泡落在该位置
# B) 给定一个屏幕外的位置 -> 启动后回落到右下角
# C) 模拟拖动 -> bubble.json 被更新成新位置
param(
    [string]$Exe = "src\FileUploader.App\bin\Release\net10.0-windows10.0.22621.0\win-x64\FileUploader.exe"
)

$ErrorActionPreference = "Stop"

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class BubbleTest {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);

    public static IntPtr Find(uint pid, string cls) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            uint o; GetWindowThreadProcessId(h, out o);
            if (o == pid) {
                var c = new StringBuilder(128); GetClassNameW(h, c, c.Capacity);
                if (c.ToString() == cls) found = h;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@

$dataDir = Join-Path $env:APPDATA "FileUploader"
$bubbleJson = Join-Path $dataDir "bubble.json"

function Start-Bubble {
    Get-Process -Name FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    Start-Process -FilePath (Resolve-Path $Exe) -ArgumentList "--compact" -WorkingDirectory (Get-Location).Path
    $proc = $null
    for ($i = 0; $i -lt 100; $i++) {
        Start-Sleep -Milliseconds 150
        $proc = Get-Process -Name FileUploader -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($proc) { break }
    }
    for ($i = 0; $i -lt 100; $i++) {
        $h = [BubbleTest]::Find([uint32]$proc.Id, "FileUploaderBubbleWindow")
        if ($h -ne [IntPtr]::Zero -and [BubbleTest]::IsWindowVisible($h)) {
            return @{ Proc = $proc; Hwnd = $h }
        }
        Start-Sleep -Milliseconds 150
    }
    return $null
}

$fail = 0

# A) 合法位置应被复用
if (-not (Test-Path $dataDir)) { New-Item -ItemType Directory -Path $dataDir -Force | Out-Null }
'{ "X": 620, "Y": 320 }' | Set-Content $bubbleJson -Encoding utf8
$s = Start-Bubble
if (-not $s) { Write-Output "FAIL: bubble did not open (A)"; exit 1 }
$r = New-Object BubbleTest+RECT
[void][BubbleTest]::GetWindowRect($s.Hwnd, [ref]$r)
$okA = ([Math]::Abs($r.Left - 620) -le 2) -and ([Math]::Abs($r.Top - 320) -le 2)
Write-Output "A 复用已保存位置: rect=$($r.Left),$($r.Top) expect=620,320 -> $okA"
if (-not $okA) { $fail++ }

# B) 屏幕外位置应回落右下角
'{ "X": -9000, "Y": -9000 }' | Set-Content $bubbleJson -Encoding utf8
$s = Start-Bubble
if (-not $s) { Write-Output "FAIL: bubble did not open (B)"; exit 1 }
$r = New-Object BubbleTest+RECT
[void][BubbleTest]::GetWindowRect($s.Hwnd, [ref]$r)
$screenW = [BubbleTest]::GetSystemMetrics(0)
$screenH = [BubbleTest]::GetSystemMetrics(1)
$expectedX = $screenW - ($r.Right - $r.Left) - 48
$expectedY = $screenH - ($r.Bottom - $r.Top) - 96
$okB = ([Math]::Abs($r.Left - $expectedX) -le 4) -and ([Math]::Abs($r.Top - $expectedY) -le 4)
Write-Output "B 离屏位置回落右下角: rect=$($r.Left),$($r.Top) expect≈$expectedX,$expectedY -> $okB"
if (-not $okB) { $fail++ }

# C) 模拟拖动后应写回新位置
$cx = $r.Left + 60
$cy = $r.Top + 60
# 用真实鼠标事件拖动（气泡是原生分层窗口，真实拖动手感与用户一致）
[void][BubbleTest]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 200
[BubbleTest]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)       # 左键按下
Start-Sleep -Milliseconds 120
for ($step = 1; $step -le 10; $step++) {
    [void][BubbleTest]::SetCursorPos($cx - (20 * $step), $cy - (14 * $step))
    Start-Sleep -Milliseconds 50
}
Start-Sleep -Milliseconds 150
[BubbleTest]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)       # 左键抬起
Start-Sleep -Milliseconds 700

$after = New-Object BubbleTest+RECT
[void][BubbleTest]::GetWindowRect($s.Hwnd, [ref]$after)
$json = Get-Content $bubbleJson -Raw
Write-Output "C 拖动后窗口=$($after.Left),$($after.Top)  bubble.json=$($json.Trim())"
$saved = $json | ConvertFrom-Json
$okC = ([Math]::Abs($saved.X - $after.Left) -le 2) -and ([Math]::Abs($saved.Y - $after.Top) -le 2) -and ($after.Left -lt $r.Left)
Write-Output "C 位置已被记录 -> $okC"
if (-not $okC) { $fail++ }

Get-Process -Name FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force

if ($fail -eq 0) { Write-Output "RESULT PASS"; exit 0 }
Write-Output "RESULT FAIL ($fail)"; exit 1
