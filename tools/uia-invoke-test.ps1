# ASCII-only: click the "test connection" button in the settings window via UIA.
Start-Sleep -Milliseconds 900
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

function S([int[]]$codes) { ($codes | ForEach-Object { [string][char]$_ }) -join "" }

$winName = S 0x5B58, 0x50A8, 0x914D, 0x7F6E          # 存储配置
$btnName = S 0x6D4B, 0x8BD5, 0x8FDE, 0x63A5          # 测试连接

$proc = Get-Process -Name FileUploader -ErrorAction Stop | Select-Object -First 1
$cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)
$tops = $AE::RootElement.FindAll($TS::Children, $cond)

$window = $null
foreach ($w in $tops) { if ($w.Current.Name -eq $winName) { $window = $w } }
if (-not $window) { Write-Output "FAIL: settings window not found"; exit 1 }

$bcond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Button)
$buttons = $window.FindAll($TS::Descendants, $bcond)
$names = @()
foreach ($b in $buttons) { $names += $b.Current.Name }
foreach ($b in $buttons) {
    if ($b.Current.Name -eq $btnName) {
        try {
            $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Write-Output "invoked test button"
            exit 0
        } catch {
            Write-Output ("FAIL invoke: " + $_.Exception.Message)
            exit 1
        }
    }
}

Write-Output ("FAIL: button not found. buttons=" + ($names -join " | "))
exit 1
