# 真实拖拽验证：从资源管理器把一个文件拖到「拖拽浮现」气泡上。
# 拖到气泡后按 ESC 取消，不会真的上传（只验证 DragEnter 是否触发、气泡是否亮起）。
# 用法：先以 FU_FLASH_DEBUG=1 启动 App，再运行本脚本；结束后看 %TEMP%\fu-flash.log
param(
    [string]$TargetX = "2432",
    [string]$TargetY = "1352"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$src = @'
using System;using System.Runtime.InteropServices;
public static class RealDrag{
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f,int dx,int dy,uint d,UIntPtr e);
 [DllImport("user32.dll")] public static extern void keybd_event(byte vk,byte scan,uint flags,UIntPtr extra);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int cx,int cy,uint flags);
 public static string Go(int x1,int y1,int x2,int y2){
   SetCursorPos(x1,y1); System.Threading.Thread.Sleep(200);
   mouse_event(0x0002,0,0,0,UIntPtr.Zero);          // LEFTDOWN
   System.Threading.Thread.Sleep(200);
   for(int i=1;i<=24;i++){
     SetCursorPos(x1+(x2-x1)*i/24, y1+(y2-y1)*i/24);
     System.Threading.Thread.Sleep(55);
   }
   System.Threading.Thread.Sleep(500);              // 让 DragEnter 有机会触发
   keybd_event(0x1B,0,0,UIntPtr.Zero);              // ESC：取消拖拽，避免真的上传
   keybd_event(0x1B,0,2,UIntPtr.Zero);
   System.Threading.Thread.Sleep(200);
   mouse_event(0x0004,0,0,0,UIntPtr.Zero);          // LEFTUP
   return "dragged";
 }
}
'@
Add-Type -TypeDefinition $src

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

$folder = Join-Path $env:TEMP "fu-realdrag"
New-Item -ItemType Directory -Force -Path $folder | Out-Null
$file = Join-Path $folder "探针文件.txt"
Set-Content -Path $file -Value "probe" -Encoding UTF8

Start-Process explorer.exe -ArgumentList $folder
Start-Sleep -Seconds 2

$cond = New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, "CabinetWClass")
$win = $null
for ($i = 0; $i -lt 40; $i++) {
    foreach ($w in $AE::RootElement.FindAll($TS::Children, $cond)) {
        if ($w.Current.Name -eq "fu-realdrag") { $win = $w; break }
    }
    if ($win) { break }
    Start-Sleep -Milliseconds 250
}
if (-not $win) { "FAIL: 没找到资源管理器窗口"; exit 1 }

# 提到最前，保证鼠标落在文件图标上
$hwnd = [IntPtr]$win.Current.NativeWindowHandle
[void][RealDrag]::SetForegroundWindow($hwnd)
# 只抬到普通窗口最上层：设成 TOPMOST 会盖住气泡的探针窗口，测出来的行为就不对了
Start-Sleep -Milliseconds 500

$itemCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
$item = $null
foreach ($c in $win.FindAll($TS::Descendants, $itemCond)) {
    if ($c.Current.Name -like "探针文件*") { $item = $c; break }
}
if (-not $item) { "FAIL: 没找到文件项"; exit 1 }

$r = $item.Current.BoundingRectangle
$sx = [int]($r.X + $r.Width / 2)
$sy = [int]($r.Y + $r.Height / 2)
"从 ($sx,$sy) 拖到 ($TargetX,$TargetY)"
[RealDrag]::Go($sx, $sy, [int]$TargetX, [int]$TargetY)

Start-Sleep -Milliseconds 600
"--- 应用日志 ---"
Get-Content "$env:TEMP\fu-flash.log" -ErrorAction SilentlyContinue | Select-Object -Skip 5

