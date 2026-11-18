param(
    [string]$WindowTitle = "OSS 拖拽上传",
    [string]$Query = "",
    [string]$Expect = "",
    [string]$Reject = "",
    [switch]$Clear,
    [switch]$ClickDownload,     # 点第一行的「下载」（会弹出保存对话框）
    [switch]$CancelDialog,      # 把保存对话框点「取消」关掉
    [switch]$DumpTexts,
    [switch]$DumpButtons
)

# 历史记录搜索 + 行内下载的回归脚本（全部走 UI Automation，不动鼠标）。
#
#   pwsh -File tools/uia-history-search.ps1 -Query "报告" -Expect "季度报告.pdf" -Reject "demo-video.mp4"
#   pwsh -File tools/uia-history-search.ps1 -Clear
#   pwsh -File tools/uia-history-search.ps1 -ClickDownload -CancelDialog
#
# 需要先准备一份带历史的便携测试副本（见 README「已验证」里的说明），不要对着真实配置跑。

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# 保存对话框的取消：对话框里的「取消」在 UIA 里是个没有模式的 Pane，
# 只能找到 #32770 窗口句柄后发 IDCANCEL（等价于点取消，不注入鼠标）。
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class SaveDialog {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);

    public static IntPtr Find() {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            var cls = new StringBuilder(64);
            GetClassNameW(h, cls, cls.Capacity);
            if (cls.ToString() != "#32770") return true;
            var title = new StringBuilder(256);
            GetWindowTextW(h, title, title.Capacity);
            var text = title.ToString();
            if (text.StartsWith("另存为") || text.StartsWith("Save As")) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static bool Cancel(IntPtr dialog) => PostMessageW(dialog, 0x0111 /* WM_COMMAND */, new IntPtr(2) /* IDCANCEL */, IntPtr.Zero);
}
"@

$AutomationElement = [System.Windows.Automation.AutomationElement]
$TreeScope = [System.Windows.Automation.TreeScope]
$ControlType = [System.Windows.Automation.ControlType]

$root = $AutomationElement::RootElement
$nameCondition = New-Object System.Windows.Automation.PropertyCondition($AutomationElement::NameProperty, $WindowTitle)
$window = $root.FindFirst($TreeScope::Children, $nameCondition)
if (-not $window) { throw "window not found: $WindowTitle" }

function Get-Texts {
    $condition = New-Object System.Windows.Automation.PropertyCondition($AutomationElement::ControlTypeProperty, $ControlType::Text)
    $found = $window.FindAll($TreeScope::Descendants, $condition)
    $names = @()
    foreach ($t in $found) { $names += $t.Current.Name }
    return $names
}

function Find-ByType([string]$typeName) {
    $controlType = [System.Windows.Automation.ControlType]::$typeName
    $condition = New-Object System.Windows.Automation.PropertyCondition($AutomationElement::ControlTypeProperty, $controlType)
    return $window.FindAll($TreeScope::Descendants, $condition)
}

$searchBox = $null
foreach ($e in (Find-ByType "Edit")) {
    if ($e.Current.Name -eq "按文件名搜索历史记录") { $searchBox = $e; break }
}
if (-not $searchBox) { throw "search box not found（AutomationName=按文件名搜索历史记录）" }

if (-not [string]::IsNullOrEmpty($Query)) {
    $searchBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Query)
    Write-Output ("search set: '{0}'" -f $Query)
    Start-Sleep -Milliseconds 600
}

if ($Clear) {
    $cleared = $false
    foreach ($b in (Find-ByType "Button")) {
        if ($b.Current.Name -eq "清除搜索") {
            $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            $cleared = $true
            break
        }
    }
    if (-not $cleared) { throw "clear button not found" }
    Write-Output "clicked 清除搜索"
    Start-Sleep -Milliseconds 600

    $value = $searchBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    Write-Output ("search box value after clear: '{0}'" -f $value)
}

if ($DumpTexts) {
    Write-Output "== Texts =="
    foreach ($t in (Get-Texts)) { Write-Output ("'{0}'" -f $t) }
}

if ($DumpButtons) {
    Write-Output "== Buttons =="
    foreach ($b in (Find-ByType "Button")) { Write-Output ("'{0}'" -f $b.Current.Name) }
}

$texts = Get-Texts
$pass = $true

foreach ($name in ($Expect -split ',' | Where-Object { $_ })) {
    $hit = $texts -contains $name.Trim()
    if (-not $hit) { $pass = $false }
    Write-Output ("{0}  expect '{1}'" -f $(if ($hit) { "PASS" } else { "FAIL" }), $name.Trim())
}

foreach ($name in ($Reject -split ',' | Where-Object { $_ })) {
    $hit = $texts -contains $name.Trim()
    if ($hit) { $pass = $false }
    Write-Output ("{0}  reject '{1}'" -f $(if ($hit) { "FAIL" } else { "PASS" }), $name.Trim())
}

if ($ClickDownload) {
    $target = $null
    foreach ($b in (Find-ByType "Button")) {
        if ($b.Current.Name -like "下载 *") { $target = $b; break }
    }
    if (-not $target) { throw "download button not found（AutomationName=下载 <文件名>）" }
    Write-Output ("click download: {0}" -f $target.Current.Name)
    $target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 2000

    # 保存对话框是 shell 的选择器（独立进程的 #32770 窗口），UIA 把它挂在主窗口的子树里，
    # 所以按「保存类型:」这种只有对话框才有的文字来判定它开出来了。
    $dialogOpen = (Get-Texts) -contains "保存类型:"
    if (-not $dialogOpen) {
        Write-Output "FAIL  保存对话框没有出现"
        $pass = $false
    }
    else {
        Write-Output "PASS  保存对话框已弹出"
        if ($CancelDialog) {
            # 对话框里的「取消」是 Pane，没有 InvokePattern，只能给对话框窗口发 IDCANCEL。
            $hwnd = [SaveDialog]::Find()
            if ($hwnd -eq [IntPtr]::Zero) { throw "save dialog window (#32770 另存为) not found" }
            [void][SaveDialog]::Cancel($hwnd)
            Write-Output ("posted IDCANCEL to {0}" -f $hwnd)
            Start-Sleep -Milliseconds 900

            $stillOpen = (Get-Texts) -contains "保存类型:"
            if ($stillOpen) {
                Write-Output "FAIL  取消后对话框仍然在"
                $pass = $false
            }
            else {
                Write-Output "PASS  取消后对话框已关闭"
            }
        }
    }
}

Write-Output $(if ($pass) { "RESULT PASS" } else { "RESULT FAIL" })
if (-not $pass) { exit 1 }
