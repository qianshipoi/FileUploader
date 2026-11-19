# 打安装包：dist\FileUploader-<版本>-setup.exe（每用户安装，免管理员）
#
#   powershell -File build-installer.ps1                 # 用 csproj 里的版本号，自己 publish 单文件版
#   powershell -File build-installer.ps1 -Version 0.4.0  # 指定版本号
#   powershell -File build-installer.ps1 -SourceExe out\FileUploader.exe -SkipPublish   # CI：复用已 publish 的产物
#
# 依赖 Inno Setup 6.3+ 的 ISCC.exe。本机没装时用 -IsccPath 指定，或者跑：
#   winget install --id JRSoftware.InnoSetup -e
# （CI 里由 .github/workflows/release.yml 自己下一份临时装上，见 docs/development.md）
param(
    [string]$Version = '',
    [string]$SourceExe = '',
    [string]$IsccPath = '',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root
try {
    if (-not $Version) {
        $csproj = Join-Path $root 'src\FileUploader.App\FileUploader.App.csproj'
        $match = Select-String -Path $csproj -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
        if (-not $match) { throw "在 $csproj 里找不到 <Version>" }
        $Version = $match.Matches[0].Groups[1].Value.Trim()
    }

    Write-Host "版本号：$Version"

    if (-not $SourceExe -and -not $SkipPublish) {
        $out = Join-Path $root 'dist-portable'
        Write-Host '打包单文件便携版（约 1 分钟）…'
        & dotnet publish (Join-Path $root 'src\FileUploader.App\FileUploader.App.csproj') -c Release -o $out -r win-x64 `
            --self-contained true `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true `
            -p:Version=$Version
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（exit $LASTEXITCODE）" }
        $SourceExe = Join-Path $out 'FileUploader.exe'
    }

    if (-not $SourceExe) { $SourceExe = Join-Path $root 'dist-portable\FileUploader.exe' }
    if (-not (Test-Path $SourceExe)) { throw "找不到要打包的 exe：$SourceExe" }
    $SourceExe = (Resolve-Path $SourceExe).Path
    Write-Host ("源 exe：{0}（{1:N1} MB）" -f $SourceExe, ((Get-Item $SourceExe).Length / 1MB))

    if (-not $IsccPath) {
        $candidates = @(
            (Join-Path $env:LOCALAPPDATA 'Programs\InnoSetup7\ISCC.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs\InnoSetup6\ISCC.exe'),
            'C:\Program Files (x86)\Inno Setup 7\ISCC.exe',
            'C:\Program Files\Inno Setup 7\ISCC.exe',
            'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
            'C:\Program Files\Inno Setup 6\ISCC.exe'
        )
        $IsccPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $IsccPath) {
            $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
            if ($command) { $IsccPath = $command.Source }
        }
    }

    if (-not $IsccPath -or -not (Test-Path $IsccPath)) {
        throw '找不到 ISCC.exe（Inno Setup 的编译器）。装一个 Inno Setup 6.3+，或用 -IsccPath 指定路径。'
    }

    $iss = Join-Path $root 'installer\FileUploader.iss'
    $icon = Join-Path $root 'src\FileUploader.App\Assets\app.ico'
    Write-Host "编译器：$IsccPath"
    & $IsccPath "/DMyVersion=$Version" "/DMySourceExe=$SourceExe" "/DMyIcon=$icon" $iss
    if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败（exit $LASTEXITCODE）" }

    $setup = Join-Path $root "dist\FileUploader-$Version-setup.exe"
    if (-not (Test-Path $setup)) { throw "没有生成 $setup" }

    $hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host ''
    Write-Host ("安装包：{0}" -f $setup)
    Write-Host ("大小：{0:N1} MB" -f ((Get-Item $setup).Length / 1MB))
    Write-Host ("SHA256：{0}" -f $hash)
}
finally {
    Pop-Location
}
