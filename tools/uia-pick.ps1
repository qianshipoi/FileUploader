param(
    [string]$WindowTitle = "OSS 拖拽上传",
    [string]$ButtonName = "选择文件…",
    [string]$FilePath = "",
    [string]$DialogTitle = "打开",
    [switch]$Cancel
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AutomationElement = [System.Windows.Automation.AutomationElement]
$TreeScope = [System.Windows.Automation.TreeScope]
$ControlType = [System.Windows.Automation.ControlType]
$root = $AutomationElement::RootElement

function Find-Window([string]$title) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($AutomationElement::NameProperty, $title)
    return $root.FindFirst($TreeScope::Children, $condition)
}

function Find-ButtonIn($scope, [string]$name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($AutomationElement::ControlTypeProperty, $ControlType::Button)
    foreach ($b in $scope.FindAll($TreeScope::Descendants, $condition)) {
        if ($b.Current.Name -like "*$name*") { return $b }
    }
    return $null
}

$main = Find-Window $WindowTitle
if (-not $main) { throw "main window not found: $WindowTitle" }

$button = Find-ButtonIn $main $ButtonName
if (-not $button) { throw "button not found: $ButtonName" }

$button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Write-Output "clicked: $ButtonName"
Start-Sleep -Seconds 3

$dialog = Find-Window $DialogTitle
if (-not $dialog) {
    Write-Output "dialog not found: $DialogTitle"
    exit 2
}

Write-Output ("dialog: {0}" -f $dialog.Current.Name)

if ($Cancel) {
    $cancel = Find-ButtonIn $dialog "取消"
    if ($cancel) {
        $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Write-Output "clicked cancel"
    }
    exit 0
}

if ([string]::IsNullOrEmpty($FilePath)) { throw "FilePath required unless -Cancel" }

$editCondition = New-Object System.Windows.Automation.PropertyCondition($AutomationElement::AutomationIdProperty, "1148")
$edit = $dialog.FindFirst($TreeScope::Descendants, $editCondition)
if (-not $edit) {
    $editCondition = New-Object System.Windows.Automation.PropertyCondition($AutomationElement::ControlTypeProperty, $ControlType::Edit)
    $edit = $dialog.FindFirst($TreeScope::Descendants, $editCondition)
}
if (-not $edit) { throw "filename edit not found in dialog" }

$edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($FilePath)
Write-Output ("typed path: {0}" -f $FilePath)
Start-Sleep -Milliseconds 500

$open = Find-ButtonIn $dialog "打开"
if (-not $open) { $open = Find-ButtonIn $dialog "Open" }
if (-not $open) { throw "open button not found" }

$open.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Write-Output "clicked open"
