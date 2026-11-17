# ASCII-only UIA check: the header gear button must exist (AutomationName) and open the settings window.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

# "设置" without Chinese literals (safe for any file encoding)
$settingsName = [string]([char]0x8BBE) + [string]([char]0x7F6E)
# "存储配置"
$windowName = [string]([char]0x5B58) + [string]([char]0x50A8) + [string]([char]0x914D) + [string]([char]0x7F6E)

$root = $AE::RootElement
$proc = Get-Process -Name FileUploader -ErrorAction Stop | Select-Object -First 1
$pidCond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)
$appWindows = $root.FindAll($TS::Children, $pidCond)
Write-Output "app windows: $($appWindows.Count)"
foreach ($w in $appWindows) { Write-Output ("  title='{0}' class={1}" -f $w.Current.Name, $w.Current.ClassName) }

$main = $null
foreach ($w in $appWindows) {
    if ($w.Current.ClassName -eq "WinUIDesktopWin32WindowClass" -and $w.Current.Name -ne $windowName) { $main = $w }
}
if (-not $main) { throw "main window not found via UIA" }
Write-Output "main window: '$($main.Current.Name)'"

$buttonCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Button)
$buttons = $main.FindAll($TS::Descendants, $buttonCond)
Write-Output "buttons in main window: $($buttons.Count)"
foreach ($b in $buttons) {
    $r = $b.Current.BoundingRectangle
    Write-Output ("  name='{0}' rect={1},{2} {3}x{4}" -f $b.Current.Name, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
}

$target = $null
foreach ($b in $buttons) { if ($b.Current.Name -eq $settingsName) { $target = $b } }
if (-not $target) { Write-Output "RESULT gear button NOT found"; exit 1 }

$before = $root.FindAll($TS::Children, $pidCond).Count
$pattern = $target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
$pattern.Invoke()
Start-Sleep -Milliseconds 1500

$after = $root.FindAll($TS::Children, $pidCond)
$opened = $false
foreach ($w in $after) { if ($w.Current.Name -eq $windowName) { $opened = $true } }
Write-Output "settings window opened: $opened (windows $before -> $($after.Count))"

if ($opened) {
    # close it again so the user is not left with a window they did not ask for
    foreach ($w in $after) {
        if ($w.Current.Name -eq $windowName) {
            $wp = $w.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
            $wp.Close()
        }
    }
    Start-Sleep -Milliseconds 800
    $left = $root.FindAll($TS::Children, $pidCond).Count
    Write-Output "after close: $left windows"
}

if ($opened) { Write-Output "RESULT PASS"; exit 0 } else { Write-Output "RESULT FAIL"; exit 1 }
