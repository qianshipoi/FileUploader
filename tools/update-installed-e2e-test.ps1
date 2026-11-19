# 安装版更新的端到端回归：检查更新 → 下载 setup.exe → 静默升级 → **安装器把程序重新拉起来**。
#
#   pwsh -NoProfile -File tools/update-e2e-test.ps1                              # 便携版（只下载，不替换）
#   pwsh -NoProfile -File tools/update-installed-e2e-test.ps1                    # 安装版（真装、真升级、真卸载）
#   pwsh -NoProfile -File tools/update-installed-e2e-test.ps1 -SkipUninstall     # 留着装好的程序自己看
#
# ⚠ 这个脚本会**真的**把本程序安装到当前用户（%LOCALAPPDATA%\Programs\FileUploader）并写注册表、
#   开始菜单快捷方式、卸载项；跑完会自动卸载（-SkipUninstall 时保留）。配置与历史在 %APPDATA%\FileUploader，
#   安装/卸载都不碰它。
#
# 做法：把已经打好的安装包当成"更高版本的安装包"，在本机假 feed 里宣布成 9.9.9；
#   旧进程被静默安装取代后，安装器的 [Run]（Check: WizardSilent）应该把新程序拉起来。
param(
    [string]$RepoRoot = '.',
    [int]$Port = 18796,
    [string]$SetupPath = '',
    [switch]$SkipUninstall
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

$passed = $true
function Check([string]$name, [bool]$ok, [string]$detail) {
    $script:passed = $script:passed -and $ok
    Write-Output ("{0}  {1}  {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}

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

function Get-UninstallEntry {
    Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue | ForEach-Object {
        $values = Get-ItemProperty $_.PSPath
        if ($values.DisplayName -like '*拖拽*') { $values }
    }
}

# --- 0. 准备安装包 ---
if (-not $SetupPath) {
    $version = (Select-String -Path (Join-Path $RepoRoot 'src\FileUploader.App\FileUploader.App.csproj') -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value.Trim()
    $SetupPath = Join-Path $RepoRoot "dist\FileUploader-$version-setup.exe"
}

if (-not (Test-Path $SetupPath)) { throw "找不到安装包：$SetupPath（先跑 powershell -File build-installer.ps1）" }
$SetupPath = (Resolve-Path $SetupPath).Path
$setupSha = (Get-FileHash $SetupPath -Algorithm SHA256).Hash.ToLowerInvariant()
$setupSize = (Get-Item $SetupPath).Length
Write-Output ("安装包：{0}（{1:N1} MB）" -f $SetupPath, ($setupSize / 1MB))

Get-Process FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700

# 之前可能残留一份：先静默卸掉，保证从干净状态开始
$existing = Join-Path $env:LOCALAPPDATA 'Programs\FileUploader\unins000.exe'
if (Test-Path $existing) {
    Start-Process -FilePath $existing -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait
    Start-Sleep -Seconds 2
    Remove-Item (Join-Path $env:LOCALAPPDATA 'Programs\FileUploader') -Recurse -Force -ErrorAction SilentlyContinue
}

# --- 1. 静默安装 ---
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\FileUploader'
$installedExe = Join-Path $installDir 'FileUploader.exe'
Start-Process -FilePath $SetupPath -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' | Out-Null
$ok = $false
for ($i = 0; $i -lt 120 -and -not $ok; $i++) {
    Start-Sleep -Milliseconds 500
    $ok = Test-Path $installedExe
}
Check '安装包落地（exe 出现在安装目录）' $ok $installedExe
if (-not $ok) { exit 1 }

# 安装器的 [Run] 会把程序拉起来；等它稳住再继续
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 250
    if (Get-Process FileUploader -ErrorAction SilentlyContinue) { break }
}

$registered = (Get-ItemProperty 'HKCU:\Software\FileUploader' -ErrorAction SilentlyContinue).InstallPath
Check '注册表写了安装目录' ([bool]$registered) $registered
Check '注册表路径就是 exe 所在目录' ($registered -and ((Resolve-Path $registered).Path -eq (Split-Path $installedExe).TrimEnd('\'))) "$registered"
Check '有卸载项' ([bool](Get-UninstallEntry)) ''
$shortcut = Get-ChildItem (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs') -Recurse -Filter '*.lnk' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -like '*拖拽*' -and $_.Name -notlike '卸载*' } | Select-Object -First 1
Check '有开始菜单快捷方式' ([bool]$shortcut) $(if ($shortcut) { $shortcut.FullName } else { '' })

Get-Process FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 900

# --- 2. 本机假 feed：把这份安装包宣布成 9.9.9 ---
$work = Join-Path $env:TEMP 'fu-installed-e2e'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path $work -Force | Out-Null
Copy-Item $SetupPath (Join-Path $work 'FileUploader-9.9.9-setup.exe') -Force
$release = [ordered]@{
    tag_name     = 'v9.9.9'
    name         = 'OSS 拖拽上传 v9.9.9'
    html_url     = "http://127.0.0.1:$Port/release-page"
    published_at = '2026-11-19T06:00:00Z'
    prerelease   = $false
    body         = "## OSS 拖拽上传`n`n### 本次变更（v1..v9.9.9）`n`n- feat(ui): 假新版本，用来测安装版静默升级`n- fix(core): 第二条变更`n"
    assets       = @(
        [ordered]@{
            name                 = 'FileUploader-9.9.9-setup.exe'
            size                 = $setupSize
            digest               = "sha256:$setupSha"
            browser_download_url = "http://127.0.0.1:$Port/FileUploader-9.9.9-setup.exe"
        }
    )
}
($release | ConvertTo-Json -Depth 6) | Set-Content -Path (Join-Path $work 'release.json') -Encoding utf8

$serverScript = Join-Path $work 'feed.ps1'
@'
param([int]$Port, [string]$Dir)
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
$listener.Start()
$buffer = New-Object byte[] 8192
while ($true) {
    $client = $listener.AcceptTcpClient()
    try {
        $stream = $client.GetStream()
        $head = New-Object System.IO.MemoryStream
        $text = ''
        while ($text.IndexOf("`r`n`r`n") -lt 0) {
            $n = $stream.Read($buffer, 0, $buffer.Length)
            if ($n -le 0) { break }
            $head.Write($buffer, 0, $n)
            $text = [System.Text.Encoding]::ASCII.GetString($head.ToArray())
        }
        $path = ((($text -split "`r`n")[0] -split ' ')[1]).Split('?')[0]
        $file = if ($path -like '/release.json') { Join-Path $Dir 'release.json' }
                elseif ($path -like '*setup.exe') { Join-Path $Dir 'FileUploader-9.9.9-setup.exe' }
                else { $null }
        $body = if ($file -and (Test-Path $file)) { [System.IO.File]::ReadAllBytes($file) } else { [System.Text.Encoding]::UTF8.GetBytes('nope') }
        $type = if ($path -like '*.json') { 'application/json' } else { 'application/octet-stream' }
        $header = "HTTP/1.1 200 OK`r`nContent-Type: $type`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n"
        $headerBytes = [System.Text.Encoding]::ASCII.GetBytes($header)
        $stream.Write($headerBytes, 0, $headerBytes.Length)
        $stream.Write($body, 0, $body.Length)
        $stream.Flush()
        $client.Client.Shutdown([System.Net.Sockets.SocketShutdown]::Send)
    }
    catch { }
    finally { $client.Close() }
}
'@ | Set-Content -Path $serverScript -Encoding utf8

$server = Start-Process -FilePath 'pwsh' -ArgumentList @('-NoProfile', '-File', $serverScript, '-Port', $Port, '-Dir', $work) -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2

$staging = Join-Path $env:LOCALAPPDATA 'FileUploader\update-9.9.9'
$staged = Join-Path $staging 'FileUploader-9.9.9-setup.exe'

try {
    # --- 3. 启动安装版，点检查更新 → 立即更新并重启 ---
    $env:FU_UPDATE_FEED = "http://127.0.0.1:$Port/release.json"
    $oldProc = Start-Process -FilePath $installedExe -ArgumentList '--settings' -PassThru

    $settings = $null
    for ($i = 0; $i -lt 60 -and -not $settings; $i++) {
        Start-Sleep -Milliseconds 250
        $settings = Find-Window '设置'
    }
    if (-not $settings) { throw '设置窗口没出现' }

    $texts = Get-Texts $settings
    Check '设置页说明是安装版' ([bool]($texts | Where-Object { $_ -like '*安装版*' })) $(($texts | Where-Object { $_ -like '*安装版*' }) -join ' / ')

    $checkButton = Find-Button $settings '检查更新'
    if (-not $checkButton) { throw '找不到「检查更新」按钮' }
    $checkButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    for ($i = 0; $i -lt 80; $i++) {
        Start-Sleep -Milliseconds 250
        $texts = Get-Texts $settings
        if ($texts -match '发现新版本') { break }
    }
    Check '显示发现新版本 9.9.9' ([bool]($texts | Where-Object { $_ -like '*发现新版本 9.9.9*' })) ''

    $applyButton = Find-Button $settings '立即更新并重启'
    Check '按钮是「立即更新并重启」' ([bool]$applyButton) ''
    if (-not $applyButton) { throw '找不到「立即更新并重启」' }
    $applyButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Output '已点击「立即更新并重启」'

    # --- 4. 等旧进程退出、静默安装完成、安装器把新程序拉起来 ---
    $newProc = $null
    for ($i = 0; $i -lt 300 -and -not $newProc; $i++) {
        Start-Sleep -Milliseconds 500
        $newProc = Get-Process FileUploader -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $oldProc.Id } | Select-Object -First 1
    }
    Start-Sleep -Seconds 2

    Check '旧进程已退出' (-not (Get-Process -Id $oldProc.Id -ErrorAction SilentlyContinue)) ''
    Check '静默升级后程序被重新拉起来' ([bool]$newProc) $(if ($newProc) { "PID=$($newProc.Id)" } else { '' })
    Check '启动的还是安装目录里那份' ($newProc -and ($newProc.Path -eq $installedExe)) $(if ($newProc) { $newProc.Path } else { '' })
    Check '安装包下载到了升级缓存目录' (Test-Path $staged) $staged
    if (Test-Path $staged) {
        Check '下载的安装包 SHA256 与 feed 一致' (((Get-FileHash $staged -Algorithm SHA256).Hash.ToLowerInvariant()) -eq $setupSha) ''
    }

    Check 'exe 仍然在（没有被卸载/换丢）' (Test-Path $installedExe) ''
}
finally {
    Remove-Item Env:FU_UPDATE_FEED -ErrorAction SilentlyContinue
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
}

# --- 5. 卸载并核对清理 ---
if (-not $SkipUninstall) {
    Get-Process FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
    $uninstaller = Join-Path $installDir 'unins000.exe'
    if (Test-Path $uninstaller) {
        Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait
        Start-Sleep -Seconds 3
    }

    Check '注册表标记已删除' (-not (Test-Path 'HKCU:\Software\FileUploader')) ''
    Check '卸载项已删除' (-not (Get-UninstallEntry)) ''
    Check '开始菜单快捷方式已删除' (-not ([bool](Get-ChildItem (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs') -Recurse -Filter '*.lnk' -ErrorAction SilentlyContinue | Where-Object { $_.FullName -like '*拖拽*' }))) ''
    Check '升级缓存目录已清理' (-not (Test-Path (Join-Path $env:LOCALAPPDATA 'FileUploader'))) ''

    # 卸载瞬间 exe 可能还被上一个进程的句柄占着（Windows 常见），Inno 会安排重启时删除；这里重试一下再判定。
    for ($i = 0; $i -lt 20 -and (Test-Path $installedExe); $i++) {
        Remove-Item $installedExe -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path $installedExe)) { break }
        Start-Sleep -Milliseconds 500
    }
    Check '安装目录已清理' (-not (Test-Path $installedExe)) $installedExe
    Check '配置与历史没被动（%APPDATA%\FileUploader 还在）' (Test-Path (Join-Path $env:APPDATA 'FileUploader\settings.json')) ''
}

Write-Output $(if ($passed) { 'RESULT PASS' } else { 'RESULT FAIL' })
if (-not $passed) { exit 1 }
