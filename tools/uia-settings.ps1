param(
    [string]$WindowTitle = "设置",
    [string]$Endpoint = "",
    [string]$Bucket = "",
    [string]$AccessKeyId = "",
    [string]$Protocol = "",
    [switch]$ToggleHttps,
    [switch]$ToggleStartSilently,
    [switch]$ToggleAutoStart,
    [switch]$ToggleZip,
    [switch]$ClickSave,
    [switch]$ClickClose,
    [switch]$DumpTree
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AutomationElement = [System.Windows.Automation.AutomationElement]
$TreeScope = [System.Windows.Automation.TreeScope]
$ControlType = [System.Windows.Automation.ControlType]

$root = $AutomationElement::RootElement
$nameCondition = New-Object System.Windows.Automation.PropertyCondition($AutomationElement::NameProperty, $WindowTitle)
$window = $root.FindFirst($TreeScope::Children, $nameCondition)
if (-not $window) { throw "window not found: $WindowTitle" }

$editCondition = New-Object System.Windows.Automation.PropertyCondition($AutomationElement::ControlTypeProperty, $ControlType::Edit)
$edits = $window.FindAll($TreeScope::Descendants, $editCondition)

if ($DumpTree) {
    Write-Output "== Edit controls =="
    for ($i = 0; $i -lt $edits.Count; $i++) {
        $e = $edits.Item($i)
        $value = ""
        try { $value = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { }
        Write-Output ("[{0}] name='{1}' value='{2}'" -f $i, $e.Current.Name, $value)
    }

    $buttonCondition = New-Object System.Windows.Automation.PropertyCondition($AutomationElement::ControlTypeProperty, $ControlType::Button)
    $buttons = $window.FindAll($TreeScope::Descendants, $buttonCondition)
    Write-Output "== Buttons =="
    for ($i = 0; $i -lt $buttons.Count; $i++) {
        Write-Output ("[{0}] name='{1}'" -f $i, $buttons.Item($i).Current.Name)
    }
}

function Find-Button([string]$name) {
    $buttonCondition = New-Object System.Windows.Automation.PropertyCondition($AutomationElement::ControlTypeProperty, $ControlType::Button)
    $buttons = $window.FindAll($TreeScope::Descendants, $buttonCondition)
    foreach ($b in $buttons) {
        if ($b.Current.Name -eq $name) { return $b }
    }
    return $null
}

function Set-FieldByName([string]$fieldName, [string]$value) {
    if ([string]::IsNullOrEmpty($value)) { return }
    foreach ($e in $edits) {
        if ($e.Current.Name -like "*$fieldName*") {
            $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
            Write-Output ("set '{0}' = '{1}'" -f $fieldName, $value)
            return
        }
    }
    throw "field not found: $fieldName"
}

Set-FieldByName "Endpoint" $Endpoint
Set-FieldByName "Bucket" $Bucket
Set-FieldByName "AccessKey ID" $AccessKeyId

if (-not [string]::IsNullOrEmpty($Protocol)) {
    $comboCondition = New-Object System.Windows.Automation.PropertyCondition($AutomationElement::ControlTypeProperty, $ControlType::ComboBox)
    $combo = $window.FindFirst($TreeScope::Descendants, $comboCondition)
    if (-not $combo) { throw "combobox not found" }

    $expand = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    Start-Sleep -Milliseconds 500

    $items = $combo.FindAll($TreeScope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $target = $null
    foreach ($item in $items) {
        if ($item.Current.Name -eq $Protocol) { $target = $item; break }
    }
    if (-not $target) {
        foreach ($item in $root.FindAll($TreeScope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            if ($item.Current.Name -eq $Protocol) { $target = $item; break }
        }
    }
    if (-not $target) { throw "combo item not found: $Protocol" }

    $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Write-Output ("selected protocol: {0}" -f $Protocol)
    try { $expand.Collapse() } catch { }
    Start-Sleep -Milliseconds 300
}

if ($ToggleHttps) {
    # 新布局把「使用 HTTPS」放在「存储服务」卡片里，不再折叠在「高级设置」下；
    # 旧版本留下的折叠头仍然兼容处理（找得到就展开）。
    $advanced = Find-Button "高级设置"
    if ($advanced) {
        try {
            $advanced.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
            Write-Output "expanded: 高级设置"
        }
        catch {
            $advanced.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Write-Output "invoked: 高级设置"
        }
        Start-Sleep -Milliseconds 700
    }

    $checkCondition = New-Object System.Windows.Automation.PropertyCondition($AutomationElement::ControlTypeProperty, $ControlType::CheckBox)
    $checks = $window.FindAll($TreeScope::Descendants, $checkCondition)
    $toggled = $false
    foreach ($e in $checks) {
        if ($e.Current.Name -like "*HTTPS*") {
            $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
            Write-Output ("toggled: {0}" -f $e.Current.Name)
            $toggled = $true
            break
        }
    }
    if (-not $toggled) { throw "https checkbox not found" }
}

function Toggle-SwitchByName([string]$name) {
    # ToggleSwitch 在 UIA 里是 Button 类型 + TogglePattern（开关的 AutomationName 就是标题）
    $buttonCondition = New-Object System.Windows.Automation.PropertyCondition($AutomationElement::ControlTypeProperty, $ControlType::Button)
    $candidates = $window.FindAll($TreeScope::Descendants, $buttonCondition)
    foreach ($e in $candidates) {
        if ($e.Current.Name -eq $name) {
            $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
            Write-Output ("toggled: {0}" -f $name)
            return
        }
    }
    throw "toggle not found: $name"
}

if ($ToggleStartSilently) {
    Toggle-SwitchByName "静默启动"
}

if ($ToggleAutoStart) {
    Toggle-SwitchByName "开机自启"
}

if ($ToggleZip) {
    # 「多个文件打包成 ZIP 上传」是 CheckBox（不是开关）：UIA 里是 ControlType.CheckBox + TogglePattern。
    $checkCondition = New-Object System.Windows.Automation.PropertyCondition($AutomationElement::ControlTypeProperty, $ControlType::CheckBox)
    $found = $false
    foreach ($e in $window.FindAll($TreeScope::Descendants, $checkCondition)) {
        if ($e.Current.Name -like "*打包成 ZIP*") {
            $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
            Write-Output ("toggled: {0}" -f $e.Current.Name)
            $found = $true
            break
        }
    }
    if (-not $found) { throw "zip checkbox not found" }
}

if ($ClickSave) {
    $save = Find-Button "保存"
    if (-not $save) { throw "save button not found" }
    $save.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Output "clicked save"
}

if ($ClickClose) {
    $close = Find-Button "关闭"
    if (-not $close) { throw "close button not found" }
    $close.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Output "clicked close"
}
