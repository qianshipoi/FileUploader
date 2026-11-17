# 生成单文件便携版：dist-portable\FileUploader.exe（约 89 MB，含 .NET 运行时与 Windows App SDK）
dotnet publish src\FileUploader.App\FileUploader.App.csproj -c Release -o dist-portable -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true

Write-Host "已生成 dist-portable\FileUploader.exe"
