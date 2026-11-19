# 真实 GitHub 的「检查更新 → 下载」验证（对着一个留档的旧版副本跑）。
#
#   pwsh -NoProfile -File <本文件> -Exe "$env:TEMP\fu-app-v0.2.0\FileUploader.exe"
#   pwsh -NoProfile -File <本文件> -Exe (Join-Path $env:LOCALAPPDATA 'Programs\FileUploader\FileUploader.exe')
#
# 注意：会真下载 ~90 MB 的 Release 资产。
#   * 便携副本：只下载（exe 不变，新文件落在 exe 旁边的 update-<版本>\）
#   * 安装版  ：下载安装包 → 静默升级 → 自动重启（exe 版本号会变）
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
    # 注意别匹配到卡片上的「正在向 GitHub 查询最新版本…」，那还是查询中
    if ($texts -match '发现新版本|就是最新版本|检查失败') { break }
}
$header = $texts | Where-Object { $_ -match '发现新版本|就是最新版本|检查失败' } | Select-Object -First 1
Write-Output ("检查结果: '" + $header + "'")
Write-Output '--- 列出的变更 ---'
$texts | Where-Object { $_ -like '· *' } | Select-Object -First 8 | ForEach-Object { "  $_" }
$changeCount = @($texts | Where-Object { $_ -like '· *' }).Count
Write-Output ("变更条目数: $changeCount")

if ($header -notlike '*发现新版本*') {
    Write-Output '没发现新版本，后面不再继续'
    exit 1
}

# 应用自己决定怎么更新：安装版是「立即更新并重启」，便携版是「下载新版」。
$apply = Find-Button $settings '立即更新并重启'
$portable = $false
if (-not $apply) {
    $apply = Find-Button $settings '下载新版'
    $portable = $true
}
if (-not $apply) { throw '既找不到「立即更新并重启」也找不到「下载新版」' }

Write-Output ("已点击：{0}（{1}）" -f $apply.Current.Name, $(if ($portable) { '便携版：只下载，不替换' } else { '安装版：静默升级并重启' }))
$apply.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

$ok = $true
$exeBefore = (Get-Item $Exe).VersionInfo.FileVersion
$shaBefore = (Get-FileHash $Exe -Algorithm SHA256).Hash.ToLowerInvariant()

if ($portable) {
    # 便携版：等「新版已下载」，然后核对下载下来的文件
    $texts = @()
    for ($i = 0; $i -lt 480; $i++) {
        Start-Sleep -Seconds 1
        $texts = @()
        $win = Find-Window '设置'
        if ($win) { try { $texts = Get-Texts $win } catch { } }
        if ($i % 15 -eq 0) {
            $progress = $texts | Where-Object { $_ -like '*正在下载*' -or $_ -match '失败' } | Select-Object -First 1
            Write-Output ("  [{0}s] {1}" -f $i, $progress)
        }
        if ($texts -match '新版已下载') { break }
        if ($texts -match '校验失败|更新失败') { break }
    }

    if ($texts -notmatch '新版已下载') {
        Write-Output ('FAIL 没有出现「新版已下载」：' + (($texts | Where-Object { $_ -match '失败|校验|目录' }) -join ' / '))
        $ok = $false
    }
    else {
        Write-Output 'PASS 显示「新版已下载」'
        $downloaded = Get-ChildItem (Join-Path (Split-Path $Exe) 'update-*') -Filter '*.exe' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $downloaded) { Write-Output 'FAIL 没在 exe 旁边找到 update-<版本>\ 里的新版'; $ok = $false }
        else { Write-Output ("PASS 新版位置：{0}（{1:N1} MB）" -f $downloaded.FullName, ($downloaded.Length / 1MB)) }
    }

    $shaAfter = (Get-FileHash $Exe -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($shaAfter -eq $shaBefore) { Write-Output 'PASS 便携版没有被替换（exe 哈希不变）' } else { Write-Output 'FAIL 便携版的 exe 被改动了'; $ok = $false }
}
else {
    # 安装版：等旧进程退出、安装器把新程序拉起来
    $newProc = $null
    for ($i = 0; $i -lt 480 -and -not $newProc; $i++) {
        Start-Sleep -Seconds 1
        if ($i % 15 -eq 0) {
            $probe = @(Get-Process FileUploader -ErrorAction SilentlyContinue)
            $texts = @()
            if ($probe.Count -gt 0) { try { $texts = Get-Texts (Find-Window '设置') } catch { } }
            $progressText = $texts | Where-Object { $_ -like '*正在下载*' -or $_ -match '失败' } | Select-Object -First 1
            Write-Output ("  [{0}s] 进程={1} {2}" -f $i, $probe.Count, $progressText)
        }
        $newProc = Get-Process FileUploader -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $appProc.Id } | Select-Object -First 1
    }

    Start-Sleep -Seconds 3
    $after = (Get-Item $Exe).VersionInfo
    $sha = (Get-FileHash $Exe -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Output ("更新后版本: {0}（原 {1}）" -f $after.FileVersion, $exeBefore)
    Write-Output ("更新后 exe SHA256: {0}" -f $sha)

    if ($after.FileVersion -eq $exeBefore) { Write-Output 'WARN 版本号没变（旧版本升级到同版本时会这样）' }
    if ($ExpectVersion -and $after.FileVersion -notlike "$ExpectVersion*") { Write-Output "FAIL 版本号不是 $ExpectVersion"; $ok = $false }
    if ($ExpectSha -and $sha -ne $ExpectSha) { Write-Output 'FAIL SHA256 与 Release 不一致'; $ok = $false }
    if (-not $newProc) { Write-Output 'FAIL 没有起新进程'; $ok = $false } else { Write-Output 'PASS 静默升级后程序被重新拉起来' }
}

Write-Output $(if ($ok) { 'RESULT PASS' } else { 'RESULT FAIL' })
if (-not $ok) { exit 1 }
