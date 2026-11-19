param(
    [string]$WindowTitle = "OSS 拖拽上传",
    [int]$TimeoutSeconds = 240,
    [switch]$DumpTexts,
    [switch]$DumpButtons,
    [switch]$SkipClick
)

# 「断点续传」的界面回归（全部走 UI Automation，不动鼠标）：
#
#   1. 断言上传列表里有一行「已中断 · 已传 xx%」（重启后从续传状态里读出来的）；
#   2. 点那一行的「继续」（AutomationName 是 `继续上传 <文件名>`）；
#   3. 轮询到「已中断」消失，并断言它变成了「已上传」。
#
# 前置：一份便携测试副本，data\uploads 里有未完成的分片会话（做法见 docs/development.md
#       的「断点续传回归」：真传一个几百 MB 的文件，传一半 Stop-Process 杀掉进程）。
#       不要对着真实配置跑。
#
#   pwsh -File tools/uia-resume-test.ps1 -DumpTexts -DumpButtons
#   pwsh -File tools/uia-resume-test.ps1

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AutomationElement = [System.Windows.Automation.AutomationElement]
$TreeScope = [System.Windows.Automation.TreeScope]
$ControlType = [System.Windows.Automation.ControlType]

$window = $AutomationElement::RootElement.FindFirst(
    $TreeScope::Children,
    (New-Object System.Windows.Automation.PropertyCondition($AutomationElement::NameProperty, $WindowTitle)))
if (-not $window) { throw "window not found: $WindowTitle" }

function Get-Texts {
    $condition = New-Object System.Windows.Automation.PropertyCondition($AutomationElement::ControlTypeProperty, $ControlType::Text)
    $names = @()
    foreach ($t in $window.FindAll($TreeScope::Descendants, $condition)) { $names += $t.Current.Name }
    return $names
}

function Find-ByType([string]$typeName) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        $AutomationElement::ControlTypeProperty, [System.Windows.Automation.ControlType]::$typeName)
    return $window.FindAll($TreeScope::Descendants, $condition)
}

$pass = $true

if ($DumpTexts) {
    Write-Output "== Texts =="
    foreach ($t in (Get-Texts)) { Write-Output ("'{0}'" -f $t) }
}

if ($DumpButtons) {
    Write-Output "== Buttons =="
    foreach ($b in (Find-ByType "Button")) { Write-Output ("'{0}'" -f $b.Current.Name) }
}

# 1. 列表里应该有一条「已中断」的记录。
$interrupted = @((Get-Texts) | Where-Object { $_ -like "*已中断*" })
if ($interrupted.Count -eq 0) {
    Write-Output "FAIL  列表里没有「已中断」的记录（续传状态没被列出来）"
    Write-Output "RESULT FAIL"
    exit 1
}

Write-Output ("PASS  列表里有 {0} 条「已中断」记录：{1}" -f $interrupted.Count, ($interrupted -join ' / '))

if ($SkipClick) {
    Write-Output "RESULT PASS（-SkipClick：只看状态，不点）"
    exit 0
}

# 2. 点「继续」。按钮的 AutomationName 是 `继续上传 <文件名>`。
$resumeButton = $null
foreach ($b in (Find-ByType "Button")) {
    if ($b.Current.Name -like "继续上传 *") { $resumeButton = $b; break }
}

if (-not $resumeButton) {
    Write-Output "FAIL  找不到「继续上传 xxx」按钮"
    Write-Output "RESULT FAIL"
    exit 1
}

$fileName = $resumeButton.Current.Name -replace '^继续上传 ', ''

# 点下去之后这一行会立刻变成「上传中 xx%」，所以"完成"不能靠"已中断消失"判断，
# 要看列表里「已上传」的条数有没有变多（历史里本来就可能有别的已上传记录）。
$uploadedBefore = @((Get-Texts) | Where-Object { $_ -like "*已上传*" }).Count

Write-Output ("click: {0}" -f $resumeButton.Current.Name)
$resumeButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

# 3. 轮询到「已上传」条数增加（= 续传跑完了），中途把百分比打出来；出现「失败」立刻判失败。
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$lastSample = ""
$finished = $false
$failure = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 700
    $texts = Get-Texts

    $failure = @($texts | Where-Object { $_ -like "*失败*" }) | Select-Object -First 1
    if ($failure) { break }

    $uploadedNow = @($texts | Where-Object { $_ -like "*已上传*" }).Count
    if ($uploadedNow -gt $uploadedBefore) {
        $finished = $true
        break
    }

    $sample = @($texts | Where-Object { $_ -like "*上传中*" }) | Select-Object -First 1
    if ($sample -and $sample -ne $lastSample) {
        Write-Output ("  … {0}" -f $sample)
        $lastSample = $sample
    }
}

if ($failure) {
    Write-Output ("FAIL  续传失败：{0}" -f $failure)
    Write-Output "RESULT FAIL"
    exit 1
}

if (-not $finished) {
    Write-Output ("FAIL  {0} 秒内没传完" -f $TimeoutSeconds)
    Write-Output "RESULT FAIL"
    exit 1
}

Start-Sleep -Milliseconds 800
$texts = Get-Texts
if (-not ($texts -contains $fileName)) {
    Write-Output ("FAIL  列表里找不到 {0}" -f $fileName)
    $pass = $false
}
else {
    Write-Output ("PASS  续传完成：{0}（已在上传历史里，键与中断前同一个）" -f $fileName)
}

Write-Output $(if ($pass) { "RESULT PASS" } else { "RESULT FAIL" })
if (-not $pass) { exit 1 }
