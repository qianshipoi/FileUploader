# 托盘菜单回归（不需要鼠标、不截图，锁屏/被遮挡也能跑）：
# 1. 用 --menu-preview 启动 App，该参数会立即弹一次托盘菜单；
# 2. 通过 UI Automation 枚举菜单里的按钮，校验名称与命中区；
# 3. 用 InvokePattern 点「设置」，校验菜单关闭且「设置」窗口打开。
param(
    [switch]$KeepOpen
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

$menuTitle = "菜单"
$settingsTitle = "设置"
$settingsBtn = "设置"

$exe = "src\FileUploader.App\bin\Release\net10.0-windows10.0.22621.0\win-x64\FileUploader.exe"
if (-not (Test-Path $exe)) { Write-Output "FAIL: exe not found ($exe) - run from the repo root"; exit 1 }

Get-Process -Name FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Start-Process -FilePath (Resolve-Path $exe) -ArgumentList "--menu-preview" -WorkingDirectory (Get-Location).Path

$proc = $null
for ($i = 0; $i -lt 100; $i++) {
    Start-Sleep -Milliseconds 150
    $proc = Get-Process -Name FileUploader -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($proc) { break }
}
if (-not $proc) { Write-Output "FAIL: app did not start"; exit 1 }

$pidCond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)

function FindWindow([string]$title) {
    foreach ($w in $AE::RootElement.FindAll($TS::Children, $pidCond)) {
        if ($w.Current.Name -eq $title) { return $w }
    }
    return $null
}

$menu = $null
for ($i = 0; $i -lt 100; $i++) {
    $menu = FindWindow $menuTitle
    if ($menu) { break }
    Start-Sleep -Milliseconds 200
}
if (-not $menu) { Write-Output "FAIL: menu window not found"; exit 1 }

$buttonCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Button)
$buttons = $menu.FindAll($TS::Descendants, $buttonCond)
Write-Output "menu buttons: $($buttons.Count)"
foreach ($b in $buttons) {
    $r = $b.Current.BoundingRectangle
    Write-Output ("  name='{0}' enabled={1} rect={2},{3} {4}x{5}" -f $b.Current.Name, $b.Current.IsEnabled, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
}

$target = $null
foreach ($b in $buttons) { if ($b.Current.Name -eq $settingsBtn) { $target = $b } }
if (-not $target) { Write-Output "FAIL: '$settingsBtn' item not found"; exit 1 }

$target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 1500

$menuGone = (FindWindow $menuTitle) -eq $null
$settingsOpen = (FindWindow $settingsTitle) -ne $null
Write-Output "menu closed=$menuGone settings opened=$settingsOpen"

if (-not $KeepOpen -and $settingsOpen) {
    (FindWindow $settingsTitle).GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Start-Sleep -Milliseconds 800
}

if ($menuGone -and $settingsOpen) { Write-Output "RESULT PASS"; exit 0 }
Write-Output "RESULT FAIL"; exit 1
