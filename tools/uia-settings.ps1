# 设置窗口的 UI 自动化回归（不依赖鼠标）。
#
# 新布局：左边分类（连接 / 上传 / 启动 / 关于），右边一页一屏；
# 输入框改的是草稿，只有点「保存」才落盘，所以这个脚本会显式切页 + 显式保存。
param(
    [string]$WindowTitle = "设置",
    [string]$Page = "",
    [string]$Endpoint = "",
    [string]$Bucket = "",
    [string]$AccessKeyId = "",
    [string]$KeyPrefix = "",
    [string]$Protocol = "",
    [switch]$ToggleHttps,
    [switch]$TogglePathStyle,
    [switch]$ToggleAutoCopy,
    [switch]$ToggleStartSilently,
    [switch]$ToggleAutoStart,
    [switch]$ToggleZip,
    [switch]$ClickSave,
    [switch]$ClickClose,
    [switch]$ClickDiscard,
    [switch]$ClickCancel,
    [switch]$ClickSaveAndClose,
    [switch]$ExpectUnsavedPrompt,
    [string]$ExpectFooter = "",
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

function Get-Descendants($type) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AutomationElement::ControlTypeProperty, $type)
    return $window.FindAll($TreeScope::Descendants, $cond)
}

function Find-Button([string]$name) {
    foreach ($b in Get-Descendants $ControlType::Button) {
        if ($b.Current.Name -eq $name) { return $b }
    }
    return $null
}

function Invoke-Button([string]$name) {
    $b = Find-Button $name
    if (-not $b) { throw "button not found: $name" }
    if (-not $b.Current.IsEnabled) { throw "button disabled: $name" }
    $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 400
    Write-Output "clicked: $name"
}

# 左侧分类：原生 NavigationView 的导航项在 UIA 里是 ListItem（SelectionItemPattern），
# 不是 Button，所以这里按名字找元素、优先用 SelectionItemPattern。
function Show-Page([string]$name) {
    if ([string]::IsNullOrEmpty($name)) { return }
    $cond = New-Object System.Windows.Automation.PropertyCondition($AutomationElement::NameProperty, $name)
    foreach ($e in $window.FindAll($TreeScope::Descendants, $cond)) {
        try {
            $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Start-Sleep -Milliseconds 450
            Write-Output ("page: {0}" -f $name)
            return
        }
        catch { }

        try {
            $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Milliseconds 450
            Write-Output ("page: {0}" -f $name)
            return
        }
        catch { }
    }

    throw "nav item not found: $name"
}

# 字段属于哪一页：输入框现在分在「连接」和「上传」两页里。
$FieldPages = @{
    'Endpoint' = '连接'
    'Bucket' = '连接'
    'AccessKey ID' = '连接'
    '对象键前缀' = '上传'
}

function Set-Field([string]$fieldName, [string]$value) {
    if ([string]::IsNullOrEmpty($value)) { return }
    Show-Page $FieldPages[$fieldName]
    foreach ($e in Get-Descendants $ControlType::Edit) {
        if ($e.Current.Name -like "*$fieldName*") {
            $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
            Start-Sleep -Milliseconds 200
            # 读回校验：ValuePattern 落到受控 TextBox 上偶发粘错（重绘时挨着插入），
            # 与其带着脏值继续跑，不如当场报出来。
            $actual = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
            if ($actual -ne $value) {
                throw "field value mismatch: '$fieldName' expected '$value' but got '$actual'"
            }
            Write-Output ("set '{0}' = '{1}'" -f $fieldName, $value)
            return
        }
    }
    throw "field not found: $fieldName"
}

# 开关在 UIA 里是 Button 类型 + TogglePattern（AutomationName 就是标题）。
function Toggle-Switch([string]$name, [string]$page) {
    Show-Page $page
    $b = Find-Button $name
    if (-not $b) { throw "toggle not found: $name" }
    $b.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Milliseconds 250
    Write-Output ("toggled: {0}" -f $name)
}

if ($DumpTree) {
    Write-Output "== Controls =="
    foreach ($t in @($ControlType::Button, $ControlType::Edit, $ControlType::ComboBox, $ControlType::Text, $ControlType::CheckBox)) {
        foreach ($e in Get-Descendants $t) {
            $value = ""
            try { $value = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { }
            Write-Output ("[{0}] name='{1}' value='{2}' enabled={3}" -f $t.ProgrammaticName, $e.Current.Name, $value, $e.Current.IsEnabled)
        }
    }
}

Show-Page $Page

Set-Field "Endpoint" $Endpoint
Set-Field "Bucket" $Bucket
Set-Field "AccessKey ID" $AccessKeyId
Set-Field "对象键前缀" $KeyPrefix

if (-not [string]::IsNullOrEmpty($Protocol)) {
    Show-Page "连接"
    $comboCondition = New-Object System.Windows.Automation.PropertyCondition($AutomationElement::ControlTypeProperty, $ControlType::ComboBox)
    $combo = $window.FindFirst($TreeScope::Descendants, $comboCondition)
    if (-not $combo) { throw "combobox not found" }

    $expand = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    Start-Sleep -Milliseconds 500

    $target = $null
    foreach ($item in $root.FindAll($TreeScope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($item.Current.Name -eq $Protocol) { $target = $item; break }
    }
    if (-not $target) { throw "combo item not found: $Protocol" }

    $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 300
    try { $expand.Collapse() } catch { }
    Write-Output ("selected protocol: {0}" -f $Protocol)
}

if ($ToggleHttps) { Toggle-Switch "使用 HTTPS" "连接" }
if ($TogglePathStyle) { Toggle-Switch "路径风格地址" "连接" }
if ($ToggleAutoCopy) { Toggle-Switch "自动复制链接" "上传" }
if ($ToggleZip) { Toggle-Switch "多个文件打包成 ZIP 上传" "上传" }
if ($ToggleStartSilently) { Toggle-Switch "静默启动" "启动" }
if ($ToggleAutoStart) { Toggle-Switch "开机自启" "启动" }

if (-not [string]::IsNullOrEmpty($ExpectFooter)) {
    $found = $false
    foreach ($t in Get-Descendants $ControlType::Text) {
        if ($t.Current.Name -like "*$ExpectFooter*") { $found = $true; Write-Output ("footer: {0}" -f $t.Current.Name) }
    }
    if (-not $found) { throw "footer text not found: $ExpectFooter" }
}

if ($ClickSave) {
    # 没改动时「保存」是禁用的，这里顺便断言"点得动才点"。
    Invoke-Button "保存"
}

if ($ClickClose) {
    Invoke-Button "关闭"
}

if ($ExpectUnsavedPrompt) {
    $found = $false
    foreach ($t in Get-Descendants $ControlType::Text) {
        if ($t.Current.Name -eq "有输入还没保存") { $found = $true; break }
    }
    if (-not $found) { throw "unsaved prompt not shown" }
    Write-Output "ok: unsaved prompt shown"
}

if ($ClickDiscard) {
    Invoke-Button "不保存"
}

if ($ClickCancel) {
    Invoke-Button "取消"
}

if ($ClickSaveAndClose) {
    Invoke-Button "保存并关闭"
}
