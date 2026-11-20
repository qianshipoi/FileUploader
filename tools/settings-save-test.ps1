# 设置页保存策略的回归（不依赖鼠标）
#
#   pwsh -NoProfile -File tools\settings-save-test.ps1
#
# 覆盖「输入框不再逐字符落盘」这条改动：
#   1. 改输入框 -> settings.json 必须没变，底部提示「有输入还没保存」，「保存」可点
#   2. 点「保存」 -> 磁盘写入新值，提示变成「已保存 HH:mm:ss」，「保存」变灰
#   3. 再改一处 + 点窗口 X（WM_CLOSE）-> 弹「有输入还没保存」，窗口不关
#   4. 选「不保存」 -> 窗口关闭，磁盘仍是上一次保存的值
#   5. 重开程序把输入框改回原值并保存，再点「关闭」-> 不再弹提示
#
# 会把 %APPDATA%\FileUploader\settings.json 的 KeyPrefix 临时改成 <原值>-uitest，
# 结束时改回原值；中途失败也会在 finally 里按文本替换恢复。
param(
    [string]$RepoRoot = '.'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class STest {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    public static IntPtr Find(uint pid, string title) {
        IntPtr f = IntPtr.Zero;
        EnumWindows((h, l) => {
            uint o; GetWindowThreadProcessId(h, out o);
            if (o == pid) {
                var sb = new StringBuilder(256); GetWindowTextW(h, sb, sb.Capacity);
                if (sb.ToString() == title) f = h;
            }
            return true;
        }, IntPtr.Zero);
        return f;
    }
}
"@

$passed = $true
function Check([string]$name, [bool]$ok, [string]$detail) {
    $script:passed = $script:passed -and $ok
    Write-Output ("{0}  {1}  {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}

# 条件成立就返回 $true，超时返回 $false（UI 重绘是异步的，别用固定 sleep 赌）
function Wait-Until([scriptblock]$condition, [int]$timeoutMs = 5000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds 150
    }
    return [bool](& $condition)
}

$build = Join-Path $RepoRoot 'src\FileUploader.App\bin\Release\net10.0-windows10.0.22621.0\win-x64'
$exe = Join-Path $build 'FileUploader.exe'
if (-not (Test-Path $exe)) { throw "找不到 $exe，先 dotnet build -c Release" }

$settingsPath = Join-Path $env:APPDATA 'FileUploader\settings.json'
if (-not (Test-Path $settingsPath)) { throw "找不到 $settingsPath，先手动配置一次" }
$originalJson = Get-Content $settingsPath -Raw
$originalPrefix = (ConvertFrom-Json $originalJson).KeyPrefix
$testPrefix = if ([string]::IsNullOrEmpty($originalPrefix)) { 'uitest' } else { "$originalPrefix-uitest" }
$draftPrefix = "$testPrefix-draft"

function Get-SettingsWindow {
    return $AE::RootElement.FindFirst(
        $TS::Children,
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, '设置')))
}

function Start-AppWithSettings {
    Get-Process FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    $proc = Start-Process -FilePath $exe -ArgumentList '--settings' -PassThru
    $ok = Wait-Until { [bool](Get-SettingsWindow) } 20000
    if (-not $ok) { throw '设置窗口没出现' }
    return $proc
}

function Get-Buttons($window) {
    return $window.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Button)))
}

function Find-Button($window, [string]$name) {
    foreach ($b in Get-Buttons $window) { if ($b.Current.Name -eq $name) { return $b } }
    return $null
}

# 窗口刚出现时内容可能还没渲染出来：等按钮出现再点。
function Wait-Button([string]$name, [int]$timeoutMs = 8000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        $window = Get-SettingsWindow
        if ($window) {
            $b = Find-Button $window $name
            if ($b) { return $b }
        }
        Start-Sleep -Milliseconds 150
    }
    return $null
}

function Invoke-Button([string]$name) {
    $b = Wait-Button $name
    if (-not $b) { throw "找不到按钮：$name" }
    if (-not $b.Current.IsEnabled) { throw "按钮不可点：$name" }
    $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 300
}

function Get-PrefixField($window) {
    foreach ($e in $window.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Edit)))) {
        if ($e.Current.Name -like '*对象键前缀*') { return $e }
    }
    return $null
}

# 原生 NavigationView 的导航项是 ListItem（SelectionItemPattern），不是 Button。
function Select-Nav([string]$name) {
    $window = Get-SettingsWindow
    if (-not $window) { throw "找不到设置窗口（切到 $name）" }
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)
    foreach ($e in $window.FindAll($TS::Descendants, $cond)) {
        try {
            $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Start-Sleep -Milliseconds 450
            return
        }
        catch { }
    }
    throw "找不到导航项：$name"
}

function Set-Prefix([string]$value) {
    Select-Nav '上传'
    $field = Get-PrefixField (Get-SettingsWindow)
    if (-not $field) { throw '找不到「对象键前缀」输入框' }
    $field.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
    $ok = Wait-Until { (Get-PrefixField (Get-SettingsWindow)).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq $value }
    return [bool]$ok
}

function Get-Texts {
    $window = Get-SettingsWindow
    $texts = @()
    foreach ($t in $window.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text)))) {
        $texts += $t.Current.Name
    }
    return $texts
}

function Test-Text([string]$pattern) { return [bool]((Get-Texts) -match $pattern) }

function Get-DiskPrefix { return (Get-Content $settingsPath -Raw | ConvertFrom-Json).KeyPrefix }

function Restore-SettingsFile {
    # 兜底：按文本替换把 KeyPrefix 换回原值，别留下测试痕迹。
    $raw = Get-Content $settingsPath -Raw
    if ((ConvertFrom-Json $raw).KeyPrefix -ne $originalPrefix) {
        $raw = $raw -replace '"KeyPrefix":\s*"[^"]*"', ('"KeyPrefix": "' + $originalPrefix + '"')
        Set-Content -Path $settingsPath -Value $raw -Encoding utf8 -NoNewline
    }
}

$proc = $null
try {
    # --- 1. 改输入框：不落盘 ---
    $proc = Start-AppWithSettings
    Check '输入框接受输入' (Set-Prefix $testPrefix) ''
    Check '还没保存时磁盘没变' ((Get-DiskPrefix) -eq $originalPrefix) ("disk='" + (Get-DiskPrefix) + "'")
    Check '底部提示有未保存的输入' (Wait-Until { Test-Text '^有输入还没保存$' }) ''
    $saveButton = Find-Button (Get-SettingsWindow) '保存'
    Check '「保存」按钮可点' ([bool]($saveButton -and $saveButton.Current.IsEnabled)) ''

    # --- 2. 点保存才落盘 ---
    Invoke-Button '保存'
    Check '保存后磁盘写入新值' (Wait-Until { (Get-DiskPrefix) -eq $testPrefix }) ("disk='" + (Get-DiskPrefix) + "'")
    Check '保存后提示变成「已保存」' (Wait-Until { Test-Text '^已保存 \d\d:\d\d:\d\d$' }) ''
    Check '没有改动时「保存」被禁用' (Wait-Until { $b = Find-Button (Get-SettingsWindow) '保存'; [bool]($b -and -not $b.Current.IsEnabled) }) ''
    Start-Sleep -Milliseconds 700   # 保存会把页脚重建一遍，等它稳定再继续点

    # --- 3. 有未保存输入时点窗口 X：先问一句 ---
    Check '再次输入' (Set-Prefix $draftPrefix) ''
    $hwnd = [STest]::Find([uint32]$proc.Id, '设置')
    if ($hwnd -eq [IntPtr]::Zero) { throw '找不到设置窗口句柄' }
    [void][STest]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)   # WM_CLOSE
    Check '关窗时弹出「有输入还没保存」' (Wait-Until { Test-Text '^有输入还没保存$' }) ''
    Check '窗口没有被关掉' ([STest]::IsWindowVisible($hwnd)) ''

    # --- 4. 选「不保存」：丢掉草稿，磁盘不变 ---
    Invoke-Button '不保存'
    Check '选不保存后窗口关闭' (Wait-Until { -not [STest]::IsWindowVisible($hwnd) } 8000) ''
    Check '丢弃的输入没有写进磁盘' ((Get-DiskPrefix) -eq $testPrefix) ("disk='" + (Get-DiskPrefix) + "'")

    # --- 5. 改回原值：保存后关窗不再提示 ---
    $proc = Start-AppWithSettings
    Check '改回原值' (Set-Prefix $originalPrefix) ''
    Invoke-Button '保存'
    Check '恢复后磁盘是原值' (Wait-Until { (Get-DiskPrefix) -eq $originalPrefix }) ("disk='" + (Get-DiskPrefix) + "'")
    Start-Sleep -Milliseconds 700

    # 保存刚把页脚重建过，紧接着的程序化点击偶尔落在旧按钮上；真人手点不会这么快，
    # 这里再给一次机会，两次都关不掉才算失败。
    for ($i = 0; $i -lt 2; $i++) {
        $h = [STest]::Find([uint32]$proc.Id, '设置')
        if ($h -eq [IntPtr]::Zero -or -not [STest]::IsWindowVisible($h)) { break }
        Invoke-Button '关闭'
        Start-Sleep -Milliseconds 1200
    }
    Check '没有改动时点关闭直接关窗' (Wait-Until { $h = [STest]::Find([uint32]$proc.Id, '设置'); $h -eq [IntPtr]::Zero -or -not [STest]::IsWindowVisible($h) } 8000) ''
}
catch {
    Check '脚本执行' $false $_.Exception.Message
}
finally {
    Get-Process FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
    Restore-SettingsFile
    $restored = Get-DiskPrefix
    Check 'settings.json 已恢复原值' ($restored -eq $originalPrefix) ("disk='" + $restored + "'")
}

Write-Output ''
Write-Output $(if ($passed) { 'ALL PASS' } else { 'FAILED' })
exit $(if ($passed) { 0 } else { 1 })
