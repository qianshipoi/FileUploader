# 「拖拽浮现」气泡回归：上传过程中取消 / 移除任务后，气泡要自己收起。
# 复现路径：拖一个文件到右下角气泡 → 上传挂住 → 列表里点「取消」→ 点「移除」。
# 断言：气泡几秒内回到「装睡」（日志出现 no active uploads left + state -> Dormant），
#       不会停在「正在上传 0%」；移除之后也不会被勾回「正在上传」。
#
# 为了不碰真实 OSS 桶、也不动 %APPDATA%\FileUploader，脚本做了两件事：
#   1. 在本机开一个「黑洞」TCP 监听（accept 之后既不读也不回），上传请求会一直挂着，
#      正好给我们留出「上传过程中取消」的窗口；
#   2. 用便携模式跑（exe 旁放 portable.txt + data\settings.json），配置里只有这个黑洞地址，
#      密钥是现造的 DPAPI 密文，跑完把 portable.txt / data 删掉。
param(
    [switch]$KeepOpen
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

$src = @'
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class FlashCancel
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string name);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    public static IntPtr FindBubble() { return FindWindow("FileUploaderFlashDrop", null); }

    /// <summary>光标那块屏的工作区右下角（气泡在拖拽开始时会被搬到那里）。</summary>
    public static int[] WorkAreaAtCursor()
    {
        var p = new POINT();
        GetCursorPos(out p);
        var monitor = MonitorFromPoint(p, 2);
        var info = new MONITORINFO();
        info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        if (!GetMonitorInfo(monitor, ref info)) return new[] { 1920, 1080 };
        return new[] { info.rcWork.Right, info.rcWork.Bottom };
    }

    public static string ClassAt(int x, int y)
    {
        var p = new POINT();
        p.X = x;
        p.Y = y;
        var h = WindowFromPoint(p);
        if (h == IntPtr.Zero) return "(null)";
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>从 (x1,y1) 拖到 (x2,y2) 并松手（松手 = 真的放下，会触发上传）。</summary>
    public static void DragAndDrop(int x1, int y1, int x2, int y2)
    {
        SetCursorPos(x1, y1);
        Thread.Sleep(250);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);   // LEFTDOWN
        Thread.Sleep(250);
        for (int i = 1; i <= 24; i++)
        {
            SetCursorPos(x1 + ((x2 - x1) * i / 24), y1 + ((y2 - y1) * i / 24));
            Thread.Sleep(55);
        }

        Thread.Sleep(700);                            // 让 DragEnter 亮起气泡
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);   // LEFTUP
        Thread.Sleep(300);
    }

    // 黑洞服务：accept 之后既不读也不回，客户端的 PUT 就会一直等响应。
    private static TcpListener _listener;
    private static readonly List<TcpClient> Clients = new List<TcpClient>();

    public static int StartBlackhole()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        var thread = new Thread(() =>
        {
            while (true)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch { return; }
                lock (Clients) { Clients.Add(client); }
            }
        });
        thread.IsBackground = true;
        thread.Start();
        return port;
    }

    public static void StopBlackhole()
    {
        try { _listener.Stop(); } catch { }
        lock (Clients)
        {
            foreach (var c in Clients)
            {
                try { c.Close(); } catch { }
            }

            Clients.Clear();
        }
    }
}
'@
Add-Type -TypeDefinition $src

$failures = New-Object System.Collections.Generic.List[string]
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Output "  PASS  $name $detail" }
    else { Write-Output "  FAIL  $name $detail"; $failures.Add($name) }
}

$exe = "src\FileUploader.App\bin\Release\net10.0-windows10.0.22621.0\win-x64\FileUploader.exe"
if (-not (Test-Path $exe)) { Write-Output "FAIL: exe not found ($exe) - build Release first"; exit 1 }
$exePath = (Resolve-Path $exe).Path
$outDir = Split-Path -Parent $exePath
$dataDir = Join-Path $outDir "data"
$portableMarker = Join-Path $outDir "portable.txt"
$settingsFile = Join-Path $dataDir "settings.json"
$logPath = Join-Path $env:TEMP "fu-flash.log"
$workDir = Join-Path $env:TEMP "fu-flash-cancel"
$probeFile = Join-Path $workDir "cancel-probe.bin"

# 便携模式的 data 目录必须是我们新建的：已经存在就说明里面可能是别人的数据，直接退出别删。
if (Test-Path $dataDir) {
    Write-Output "FAIL: $dataDir 已存在（可能是便携版真实数据），先手动确认并清掉再跑"
    exit 1
}

Get-Process -Name FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
Remove-Item $logPath -ErrorAction SilentlyContinue

# 真拖拽会把光标留在右下角，结束时还原。
$cursor = New-Object 'FlashCancel+POINT'
[void][FlashCancel]::GetCursorPos([ref]$cursor)

function Get-MainWindow {
    $proc = Get-Process -Name FileUploader -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $proc) { return $null }
    $pidCond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)
    foreach ($w in $AE::RootElement.FindAll($TS::Children, $pidCond)) {
        if ($w.Current.Name -eq 'OSS 拖拽上传') { return $w }
    }
    return $null
}

# 列表里的「取消 / 移除」按钮：只按名字找，找到就返回（找不到返回 $null）
function Find-Button([string]$name, [int]$timeoutSec) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $win = Get-MainWindow
        if ($win) {
            $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)
            $btn = $win.FindFirst($TS::Descendants, $cond)
            if ($btn) { return $btn }
        }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Invoke-Button($btn) {
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Wait-Log([string]$pattern, [int]$timeoutSec) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ((Get-Content $logPath -Raw -ErrorAction SilentlyContinue) -match $pattern) { return $true }
        Start-Sleep -Milliseconds 150
    }
    return $false
}

try {
    # ── 1. 黑洞端口 + 便携配置 + 探针文件 ──
    $port = [FlashCancel]::StartBlackhole()
    Write-Output "黑洞监听: http://127.0.0.1:$port"

    New-Item -ItemType Directory -Force -Path $workDir | Out-Null
    $bytes = New-Object byte[] (4 * 1024 * 1024)
    (New-Object Random 1).NextBytes($bytes)
    [System.IO.File]::WriteAllBytes($probeFile, $bytes)

    # 密钥用 DPAPI 现造（和程序自己存盘时同一把 entropy），不碰用户真实配置里的密钥。
    Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
    $entropy = [System.Text.Encoding]::UTF8.GetBytes('FileUploader.OSS.Settings.v1')
    $blob = [System.Security.Cryptography.ProtectedData]::Protect(
        [System.Text.Encoding]::UTF8.GetBytes('test-secret'),
        $entropy,
        [System.Security.Cryptography.DataProtectionScope]::CurrentUser)

    $settings = [ordered]@{
        Protocol       = 1
        Endpoint       = "http://127.0.0.1:$port"
        Bucket         = 'fu-cancel-test'
        AccessKeyId    = 'test'
        Secret         = [Convert]::ToBase64String($blob)
        Region         = 'us-east-1'
        KeyPrefix      = ''
        UseDateFolder  = $false
        PathStyle      = $true
        UseHttps       = $false
        UrlMode        = 1
        AutoCopyUrl    = $false
        ZipBatchUpload = $false
        FlashDropEnabled = $true
        StartSilently  = $false
        StartWithWindows = $false
    }

    New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
    Set-Content -Path $portableMarker -Value "test" -Encoding UTF8
    Set-Content -Path $settingsFile -Value ($settings | ConvertTo-Json) -Encoding UTF8

    # ── 2. 启动（便携模式 + 调试日志）──
    $env:FU_FLASH_DEBUG = "1"
    Start-Process -FilePath $exePath -WorkingDirectory $outDir

    $hwnd = [IntPtr]::Zero
    for ($i = 0; $i -lt 80; $i++) {
        Start-Sleep -Milliseconds 150
        $hwnd = [FlashCancel]::FindBubble()
        if ($hwnd -ne [IntPtr]::Zero) { break }
    }
    Check "闪现气泡窗口已创建" ($hwnd -ne [IntPtr]::Zero) "(hwnd=0x$($hwnd.ToInt64().ToString('X')))"
    if ($hwnd -eq [IntPtr]::Zero) { throw "气泡窗口没创建，看看 $logPath" }

    $main = $null
    for ($i = 0; $i -lt 60; $i++) {
        $main = Get-MainWindow
        if ($main) { break }
        Start-Sleep -Milliseconds 250
    }
    Check "主窗口已打开" ($main -ne $null)
    if (-not $main) { throw "主窗口没出现" }

    # ── 3. 从资源管理器真拖一个文件到气泡上（松手 = 真上传，目标是黑洞）──
    Start-Process explorer.exe -ArgumentList $workDir
    Start-Sleep -Seconds 2
    $cabinetCond = New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, "CabinetWClass")
    $win = $null
    for ($i = 0; $i -lt 40; $i++) {
        foreach ($w in $AE::RootElement.FindAll($TS::Children, $cabinetCond)) {
            if ($w.Current.Name -eq 'fu-flash-cancel') { $win = $w; break }
        }
        if ($win) { break }
        Start-Sleep -Milliseconds 250
    }
    Check "资源管理器窗口已打开" ($win -ne $null)
    if (-not $win) { throw "没找到资源管理器窗口" }

    # 资源管理器必须真的在前台，否则鼠标按下落在别的窗口上，根本不会开始拖拽。
    $explorerHwnd = [IntPtr]$win.Current.NativeWindowHandle
    [void][FlashCancel]::SetForegroundWindow($explorerHwnd)
    Start-Sleep -Milliseconds 600
    $fg = [FlashCancel]::GetForegroundWindow()
    Check "资源管理器在前台（否则拖拽起不来）" ($fg -eq $explorerHwnd) "(fg=0x$($fg.ToInt64().ToString('X')) 期望=0x$($explorerHwnd.ToInt64().ToString('X')))"

    $itemCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $item = $null
    foreach ($c in $win.FindAll($TS::Descendants, $itemCond)) {
        if ($c.Current.Name -like "cancel-probe*") { $item = $c; break }
    }
    Check "资源管理器里找到探针文件" ($item -ne $null)
    if (-not $item) { throw "找不到探针文件项" }

    $ir = $item.Current.BoundingRectangle
    $sx = [int]($ir.X + $ir.Width / 2)
    $sy = [int]($ir.Y + $ir.Height / 2)

    # 落点按「光标那块屏的右下角」算：拖拽一开始气泡就会被搬到那里（Place / EnsureOnCursorMonitor），
    # 拿拖拽前的窗口矩形会算到屏幕中间去，松手就落在资源管理器窗口里了。
    [void][FlashCancel]::SetCursorPos($sx, $sy)
    Start-Sleep -Milliseconds 200
    $work = [FlashCancel]::WorkAreaAtCursor()
    $tx = $work[0] - 80
    $ty = $work[1] - 40

    Write-Output "拖拽: ($sx,$sy) -> ($tx,$ty)  该屏工作区右下角=$($work[0]),$($work[1])"
    [FlashCancel]::DragAndDrop($sx, $sy, $tx, $ty)
    Start-Sleep -Milliseconds 300

    Check "拖到气泡上后进入「正在上传」" (Wait-Log 'state -> Uploading' 12)
    $logNow = Get-Content $logPath -Raw -ErrorAction SilentlyContinue
    if (-not ($logNow -match 'state -> Uploading')) {
        Write-Output "--- 拖拽相关日志 ---"
        ($logNow -split "`r?`n" | Where-Object { $_ -match 'DragEnter|DragLeave|Drop|spy|hittest|state' }) | Select-Object -Last 15 | ForEach-Object { "    $_" }
    }

    # ── 4. 上传挂住 → 列表里点「取消」──
    $cancel = Find-Button "取消" 20
    Check "列表里出现「取消」按钮（这条任务确实在传）" ($cancel -ne $null)
    if (-not $cancel) { throw "没等到「取消」按钮，上传可能没起来" }

    Write-Output "== 点「取消」=="
    Invoke-Button $cancel
    $hid = Wait-Log 'state -> Dormant' 6
    $log = Get-Content $logPath -Raw -ErrorAction SilentlyContinue
    Check "取消后气泡自己收起（不再停在「正在上传 0%」）" $hid `
        $(if ($hid) { "" } else { "（日志里没有 state -> Dormant）" })
    Check "收起之前先判定为「没有活动任务了」" ([bool]($log -match 'no active uploads left'))

    [void][FlashCancel]::SetCursorPos($tx, $ty)
    Start-Sleep -Milliseconds 500
    $hit = [FlashCancel]::ClassAt($tx, $ty)
    Check "收起后气泡装睡（点击穿透，也不挡拖放）" ($hit -ne 'FileUploaderFlashDrop') "(hit=$hit)"

    # ── 5. 再点「移除」，气泡不能被勾回来 ──
    Write-Output "== 点「移除」=="
    $remove = Find-Button "移除" 10
    Check "列表里出现「移除」按钮" ($remove -ne $null)
    if ($remove) { Invoke-Button $remove }
    Start-Sleep -Milliseconds 1500

    $log = Get-Content $logPath -Raw -ErrorAction SilentlyContinue
    $afterDormant = ""
    if ($log -match '(?s)state -> Dormant(.*)$') { $afterDormant = $matches[1] }
    Check "移除后气泡没有被勾回「正在上传」" (($afterDormant -ne "") -and -not [bool]($afterDormant -match 'state -> Uploading'))
    Check "全程没有异常记录" (-not [bool]($log -match 'EXCEPTION'))
}
finally {
    if (-not $KeepOpen) {
        Get-Process -Name FileUploader -ErrorAction SilentlyContinue | Stop-Process -Force
    }

    [FlashCancel]::StopBlackhole()
    Get-Process explorer -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowTitle -eq 'fu-flash-cancel' } |
        ForEach-Object { [void]$_.CloseMainWindow() }

    Remove-Item $portableMarker -ErrorAction SilentlyContinue
    Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue
    [void][FlashCancel]::SetCursorPos($cursor.X, $cursor.Y)
    Remove-Item Env:\FU_FLASH_DEBUG -ErrorAction SilentlyContinue
}

Write-Output ""
if ($failures.Count -eq 0) {
    Write-Output "ALL PASS"
    exit 0
}

Write-Output "FAILED: $($failures -join ' / ')"
exit 1
