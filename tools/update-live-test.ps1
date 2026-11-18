# 真实 GitHub 的「检查更新 → 下载 → 替换 → 重启」验证（用留档的 0.2.0 便携副本）。
#
#   pwsh -NoProfile -File <本文件> -Exe "$env:TEMP\fu-app-v0.2.0\FileUploader.exe"
#
# 注意：会真下载 ~93 MB 的 Release 资产并替换该副本里的 exe（副本可随时重做）。
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$ExpectVersion = '0.2.1',
    [string]$ExpectSha = ''    # 留空则只校验 exe 版本号
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

function Find-Window([string]$title) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $title)
    return $AE::RootElement.FindFirst($TS::Children, $condition)
}
function Find-Button($window, [string]$name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Button)
    foreach ($button in $window.FindAll($TS::Descendants, $condition)) {
        if ($button.Current.Name -eq $name) { return $button }
    }
    return $null
}
function Get-Texts($window) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text)
    return @($window.FindAll($TS::Descendants, $condition) | ForEach-Object { $_.Current.Name })
}

Get-Process FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
Remove-Item (Join-Path $env:TEMP 'FileUploader') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item Env:FU_UPDATE_FEED -ErrorAction SilentlyContinue

$before = (Get-Item $Exe).VersionInfo
Write-Output ("起始版本: FileVersion={0} ProductVersion={1}" -f $before.FileVersion, $before.ProductVersion)

$appProc = Start-Process -FilePath $Exe -ArgumentList '--settings' -PassThru
$settings = $null
for ($i = 0; $i -lt 80 -and -not $settings; $i++) {
    Start-Sleep -Milliseconds 250
    $settings = Find-Window '设置'
}
if (-not $settings) { throw '设置窗口没出现' }

$check = $null
for ($i = 0; $i -lt 40 -and -not $check; $i++) {
    $check = Find-Button $settings '检查更新'
    if (-not $check) { Start-Sleep -Milliseconds 250 }
}
$check.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Write-Output '已点击「检查更新」（真实 GitHub）'

$texts = @()
for ($i = 0; $i -lt 120; $i++) {
    Start-Sleep -Milliseconds 250
    $texts = Get-Texts $settings
    if ($texts -match '发现新版本|最新版本|检查失败') { break }
}
$header = $texts | Where-Object { $_ -match '发现新版本|最新版本|检查失败' } | Select-Object -First 1
Write-Output ("检查结果: '" + $header + "'")
Write-Output '--- 列出的变更 ---'
$texts | Where-Object { $_ -like '· *' } | Select-Object -First 8 | ForEach-Object { "  $_" }
$changeCount = @($texts | Where-Object { $_ -like '· *' }).Count
Write-Output ("变更条目数: $changeCount")

if ($header -notlike '*发现新版本*') {
    Write-Output '没发现新版本，后面不再继续'
    exit 1
}

$apply = Find-Button $settings '立即更新并重启'
if (-not $apply) { throw '找不到「立即更新并重启」' }
$apply.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Write-Output '已点击「立即更新并重启」，开始下载（真实网络，可能几十秒到几分钟）'

$newProc = $null
for ($i = 0; $i -lt 480 -and -not $newProc; $i++) {
    Start-Sleep -Seconds 1
    if ($i % 15 -eq 0) {
        $probe = @(Get-Process FileUploader -ErrorAction SilentlyContinue)
        $texts = @()
        if ($probe.Count -gt 0) {
            try { $texts = Get-Texts (Find-Window '设置') } catch { }
        }
        $progressText = $texts | Where-Object { $_ -like '*正在下载*' -or $_ -match '失败' } | Select-Object -First 1
        Write-Output ("  [{0}s] 进程={1} {2}" -f $i, $probe.Count, $progressText)
    }
    $newProc = Get-Process FileUploader -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $appProc.Id } | Select-Object -First 1
}

Start-Sleep -Seconds 3
$running = @(Get-Process FileUploader -ErrorAction SilentlyContinue)
Write-Output ("旧进程还在: " + [bool]($running | Where-Object { $_.Id -eq $appProc.Id }))
Write-Output ("新进程: " + (($running | ForEach-Object { "pid=$($_.Id)" }) -join ', '))

$after = (Get-Item $Exe).VersionInfo
Write-Output ("更新后版本: FileVersion={0} ProductVersion={1}" -f $after.FileVersion, $after.ProductVersion)
$sha = (Get-FileHash $Exe -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Output ("更新后 exe SHA256: $sha")

$ok = $true
if ($after.FileVersion -notlike "$ExpectVersion*") { Write-Output "FAIL 版本号不是 $ExpectVersion"; $ok = $false } else { Write-Output "PASS 版本号 $($after.FileVersion)（期望 $ExpectVersion）" }
if ($ExpectSha -and $sha -ne $ExpectSha) { Write-Output 'FAIL SHA256 与 Release 不一致'; $ok = $false }
elseif ($ExpectSha) { Write-Output 'PASS SHA256 与 Release 资产一致' }
if (-not $newProc) { Write-Output 'FAIL 没有起新进程'; $ok = $false } else { Write-Output 'PASS 已重启新版本' }

Write-Output $(if ($ok) { 'RESULT PASS' } else { 'RESULT FAIL' })
if (-not $ok) { exit 1 }
