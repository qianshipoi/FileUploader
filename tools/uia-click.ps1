param(
    [string]$WindowTitle = "存储配置",
    [Parameter(Mandatory = $true)][string]$ButtonName
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AutomationElement = [System.Windows.Automation.AutomationElement]
$TreeScope = [System.Windows.Automation.TreeScope]
$ControlType = [System.Windows.Automation.ControlType]

$cond = New-Object System.Windows.Automation.PropertyCondition($AutomationElement::NameProperty, $WindowTitle)
$window = $AutomationElement::RootElement.FindFirst($TreeScope::Children, $cond)
if (-not $window) { throw "window not found: $WindowTitle" }

$buttonCondition = New-Object System.Windows.Automation.PropertyCondition($AutomationElement::ControlTypeProperty, $ControlType::Button)
foreach ($b in $window.FindAll($TreeScope::Descendants, $buttonCondition)) {
    if ($b.Current.Name -eq $ButtonName) {
        $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Write-Output ("clicked: {0}" -f $ButtonName)
        return
    }
}

throw "button not found: $ButtonName"
