# 便携版更新的端到端回归：检查更新 → 下载新版 → 校验 → **只下载、不替换自己**。
#
#   pwsh -NoProfile -File tools/update-e2e-test.ps1
#
# 做法（全程不碰真实的 GitHub、也不需要发版）：
#   1. 把 Release 的 build 输出复制成便携副本（带 portable.txt），作为"当前版本"；
#   2. 本机起一个 TCP 服务当 GitHub：/release.json 返回 release JSON，
#      /FileUploader-9.9.9-win-x64.exe 返回"新版 exe"（当前 exe 末尾追加几个字节，哈希不同）；
#   3. 用环境变量 FU_UPDATE_FEED 把 app 的检查更新指到本机 feed；
#   4. UIA 点「检查更新」→ 断言"发现新版本 9.9.9"与两条变更 → 断言按钮是「下载新版」
#      （便携版不该出现「立即更新并重启」）；
#   5. 先用错摘要点「下载新版」→ 必须报「校验失败」、目标目录里不留文件、exe 哈希不变；
#   6. 换正确摘要再点 → 断言「新版已下载」、update-9.9.9 里的文件哈希与 feed 一致、
#      **当前 exe 哈希一点没变**（便携版绝不替换自己），且没有 .new/.old 中间文件；
#   7. 再启动一次，旧版照常能跑。
#
# 安装版的「下载 setup.exe → 静默升级 → 自动重启」见 tools/update-installed-e2e-test.ps1。
param(
    [string]$RepoRoot = '.',
    [int]$Port = 18791
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

# --- 0. 清理 ---
Get-Process FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700

$work = Join-Path $env:TEMP 'fu-update-e2e'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path $work -Force | Out-Null

# --- 1. 便携副本 ---
$build = Join-Path $RepoRoot 'src\FileUploader.App\bin\Release\net10.0-windows10.0.22621.0\win-x64'
if (-not (Test-Path (Join-Path $build 'FileUploader.exe'))) { throw "找不到 $build\FileUploader.exe，先 dotnet build -c Release" }
$appDir = Join-Path $work 'app'
robocopy $build $appDir /E /NFL /NDL /NJH /NJS /MT:16 | Out-Null
Set-Content -Path (Join-Path $appDir 'portable.txt') -Value '' -Encoding utf8
New-Item -ItemType Directory -Path (Join-Path $appDir 'data') -Force | Out-Null
$appExe = Join-Path $appDir 'FileUploader.exe'
$originalSha = (Get-FileHash $appExe -Algorithm SHA256).Hash.ToLowerInvariant()

# --- 2. 「新版 exe」：当前 exe + 追加几个字节（便携版不会运行它，只是拿来比哈希）---
$fakeExe = Join-Path $work 'FileUploader-9.9.9-win-x64.exe'
$bytes = [System.IO.File]::ReadAllBytes($appExe)
$tail = [System.Text.Encoding]::ASCII.GetBytes("`n# update e2e payload " + [Guid]::NewGuid().ToString('N') + "`n")
$merged = New-Object byte[] ($bytes.Length + $tail.Length)
[Array]::Copy($bytes, 0, $merged, 0, $bytes.Length)
[Array]::Copy($tail, 0, $merged, $bytes.Length, $tail.Length)
[System.IO.File]::WriteAllBytes($fakeExe, $merged)
$fakeSha = (Get-FileHash $fakeExe -Algorithm SHA256).Hash.ToLowerInvariant()
$fakeSize = (Get-Item $fakeExe).Length
Write-Output ("新版 exe 准备完毕：{0} bytes  sha256={1}" -f $fakeSize, ($fakeSha.Substring(0, 12) + '...'))

# --- 3. 本机 feed 服务 ---
$releaseJsonPath = Join-Path $work 'release.json'
$notes = @(
    '## OSS 拖拽上传',
    '',
    '### 本次变更（v0.2.0..v9.9.9）',
    '',
    '- feat(ui): 端到端测试用的假新版本 (test0001)',
    '- fix(core): 第二条变更 (test0002)',
    ''
) -join "`n"

function Write-Release([string]$digest) {
    $release = [ordered]@{
        tag_name     = 'v9.9.9'
        name         = 'OSS 拖拽上传 v9.9.9'
        html_url     = "http://127.0.0.1:$Port/release-page"
        published_at = '2026-11-18T06:00:00Z'
        prerelease   = $false
        body         = $notes
        assets       = @(
            [ordered]@{
                name                 = 'FileUploader-9.9.9-win-x64.exe'
                size                 = $fakeSize
                digest               = "sha256:$digest"
                browser_download_url = "http://127.0.0.1:$Port/FileUploader-9.9.9-win-x64.exe"
            }
        )
    }
    ($release | ConvertTo-Json -Depth 6) | Set-Content -Path $releaseJsonPath -Encoding utf8
}

# 先故意给一个错的摘要，用来验证"校验不过就什么都不留"。
Write-Release ('0' * 64)

$serverScript = Join-Path $work 'feed.ps1'
@'
param([int]$Port, [string]$JsonPath, [string]$FeedExePath)
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
        $path = (($text -split "`r`n")[0] -split ' ')[1]
        if ($path -like '/release.json*') {
            # 每次请求重新读：回归脚本会在两轮之间换摘要
            $body = [System.IO.File]::ReadAllBytes($JsonPath)
            $type = 'application/json'
        }
        elseif ($path -like '*FileUploader-9.9.9-win-x64.exe*') {
            $body = [System.IO.File]::ReadAllBytes($FeedExePath)
            $type = 'application/octet-stream'
        }
        else {
            $body = [System.Text.Encoding]::UTF8.GetBytes('<!doctype html><title>release page</title>')
            $type = 'text/html'
        }
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

$server = Start-Process -FilePath 'pwsh' `
    -ArgumentList @('-NoProfile', '-File', $serverScript, '-Port', $Port, '-JsonPath', $releaseJsonPath, '-FeedExePath', $fakeExe) `
    -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2

try {
    $probe = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/release.json" -UseBasicParsing -TimeoutSec 10
    Check '本机 feed 就绪' ($probe.StatusCode -eq 200) ("HTTP " + $probe.StatusCode)
}
catch {
    Check '本机 feed 就绪' $false $_.Exception.Message
}

$updateDir = Join-Path $appDir 'update-9.9.9'
$downloaded = Join-Path $updateDir 'FileUploader-9.9.9-win-x64.exe'

try {
    # --- 4. 启动 app（feed 指向本机），点「检查更新」 ---
    $env:FU_UPDATE_FEED = "http://127.0.0.1:$Port/release.json"
    $appProc = Start-Process -FilePath $appExe -ArgumentList '--settings' -PassThru

    $settings = $null
    for ($i = 0; $i -lt 60 -and -not $settings; $i++) {
        Start-Sleep -Milliseconds 250
        $settings = Find-Window '设置'
    }
    if (-not $settings) { throw '设置窗口没出现' }

    $checkButton = $null
    for ($i = 0; $i -lt 40 -and -not $checkButton; $i++) {
        $checkButton = Find-Button $settings '检查更新'
        if (-not $checkButton) { Start-Sleep -Milliseconds 250 }
    }
    if (-not $checkButton) { throw '找不到「检查更新」按钮' }
    $checkButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Output '已点击「检查更新」'

    $texts = @()
    for ($i = 0; $i -lt 80; $i++) {
        Start-Sleep -Milliseconds 250
        $texts = Get-Texts $settings
        if ($texts -match '发现新版本') { break }
    }

    $found = ($texts | Where-Object { $_ -like '*发现新版本*' } | Select-Object -First 1)
    Check '显示发现新版本' ([bool]$found) ("'" + $found + "'")
    Check '显示版本号 9.9.9' ([bool]($texts | Where-Object { $_ -like '*9.9.9*' })) ''
    Check '列出变更条目 1' ([bool]($texts | Where-Object { $_ -like '*端到端测试用的假新版本*' })) ''
    Check '列出变更条目 2' ([bool]($texts | Where-Object { $_ -like '*第二条变更*' })) ''

    # --- 4b. 便携版：按钮必须是「下载新版」，且不能出现「立即更新并重启」 ---
    $downloadButton = Find-Button $settings '下载新版'
    Check '按钮是「下载新版」' ([bool]$downloadButton) ''
    Check '不出现「立即更新并重启」' (-not (Find-Button $settings '立即更新并重启')) ''

    # --- 4c. 摘要故意给错：必须报校验失败、不留文件、不碰 exe ---
    $downloadButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Output '已点击「下载新版」（摘要故意给错的这一轮）'

    $texts = @()
    for ($i = 0; $i -lt 240; $i++) {
        Start-Sleep -Milliseconds 250
        $texts = Get-Texts $settings
        if ($texts -match '校验失败') { break }
    }

    $mismatch = ($texts | Where-Object { $_ -like '*校验失败*' } | Select-Object -First 1)
    Check '摘要不匹配时报校验失败' ([bool]$mismatch) ("'" + $mismatch + "'")
    Check '摘要不匹配时不留下载文件' (-not (Test-Path $downloaded)) ''
    Check '摘要不匹配时 exe 未变' (((Get-FileHash $appExe -Algorithm SHA256).Hash.ToLowerInvariant()) -eq $originalSha) ''

    # 关掉「更新失败」对话框（模态），否则下一轮的结果对话框弹不出来。
    $closeButton = Find-Button $settings '关闭'
    if ($closeButton) {
        $closeButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Write-Output '已关闭「更新失败」对话框'
        Start-Sleep -Milliseconds 800
    }

    # --- 4d. 换成正确摘要，重来一次 ---
    Write-Release $fakeSha
    $checkButton = Find-Button $settings '检查更新'
    $checkButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    for ($i = 0; $i -lt 80; $i++) {
        Start-Sleep -Milliseconds 250
        $texts = Get-Texts $settings
        if ($texts -match '发现新版本') { break }
    }
    Check '第二轮又查到新版本' ([bool]($texts | Where-Object { $_ -like '*发现新版本*' })) ''

    $downloadButton = Find-Button $settings '下载新版'
    if (-not $downloadButton) { throw '找不到「下载新版」按钮（第二轮）' }
    $downloadButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Output '已点击「下载新版」'

    $texts = @()
    for ($i = 0; $i -lt 480; $i++) {
        Start-Sleep -Milliseconds 250
        $texts = Get-Texts $settings
        if ($texts -match '新版已下载' -or $texts -match '失败') { break }
    }

    $done = ($texts | Where-Object { $_ -like '*新版已下载*' } | Select-Object -First 1)
    Check '显示「新版已下载」' ([bool]$done) ("'" + $done + "'")
    if (-not (Test-Path $downloaded)) {
        Write-Output '--- 没下到文件，界面上的提示 ---'
        foreach ($line in ($texts | Where-Object { $_ -match '失败|校验|下载|超时|目录' })) { Write-Output ("  '$line'") }
    }

    Check '新版下载到了 exe 旁边' (Test-Path $downloaded) $downloaded
    if (Test-Path $downloaded) {
        $newSha = (Get-FileHash $downloaded -Algorithm SHA256).Hash.ToLowerInvariant()
        Check '下载到的新版 SHA256 与 feed 一致' ($newSha -eq $fakeSha) ($newSha.Substring(0, 12) + '...')
    }

    # --- 5. 关键：便携版绝不替换自己 ---
    $afterSha = (Get-FileHash $appExe -Algorithm SHA256).Hash.ToLowerInvariant()
    Check '当前 exe 没有被替换（哈希不变）' ($afterSha -eq $originalSha) ($afterSha.Substring(0, 12) + '...')
    Check '没有留下 FileUploader.new.exe' (-not (Test-Path (Join-Path $appDir 'FileUploader.new.exe'))) ''
    Check '没有留下 .old 备份' (-not (Test-Path ($appExe + '.old'))) ''

    # --- 6. 旧版照常能启动 ---
    Get-Process FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 900
    Start-Process -FilePath $appExe | Out-Null
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        if (Get-Process FileUploader -ErrorAction SilentlyContinue) { break }
    }
    Start-Sleep -Seconds 2
    Check '旧版程序照常能启动' ([bool](Get-Process FileUploader -ErrorAction SilentlyContinue)) ''
}
finally {
    Remove-Item Env:FU_UPDATE_FEED -ErrorAction SilentlyContinue
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
}

Get-Process FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output $(if ($passed) { 'RESULT PASS' } else { 'RESULT FAIL' })
if (-not $passed) { exit 1 }
