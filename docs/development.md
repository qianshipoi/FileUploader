# 开发指南

## 技术栈

- WinUI 3 + [Microsoft.UI.Reactor](https://github.com/microsoft/microsoft-ui-reactor) `0.1.0-preview.16`（声明式 C# UI，**没有 XAML / ViewModel / 数据绑定**：一棵 `Element` 树 + `UseState` 状态钩子）
- Windows App SDK 2.3.1，`net10.0-windows10.0.22621.0`，**非打包**（`WindowsPackageType=None`），x64
- 新增 NuGet 依赖要先征得同意；能用手写 P/Invoke 解决的小问题不要引第三方包

## 构建 / 运行 / 发布

```powershell
# 构建（Debug / Release 都要求 0 警告）
dotnet build src\FileUploader.App\FileUploader.App.csproj -c Release

# 直接跑
dotnet run --project src\FileUploader.App

# 发布成可分发目录（dist\FileUploader.exe）
dotnet publish src\FileUploader.App\FileUploader.App.csproj -c Release -o dist

# 单文件便携版（也可以用 publish-portable.ps1）
dotnet publish src\FileUploader.App\FileUploader.App.csproj -c Release -o dist-portable -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true
```

| 目录 | 内容 | 适用 |
|---|---|---|
| `dist-portable\FileUploader.exe` | **单文件便携版**（≈89 MB，自带 .NET 10 运行时 + Windows App SDK） | 直接发给别人，拷过去双击就用 |
| `dist\` | 目录版（多文件，需要目标机器装 .NET 10 运行时） | 本机开发自用 |

> 调试时注意：`dotnet build` 会写 `bin\`/`obj\`，如果程序正在运行会报 `MSB3026` 文件占用，先 `Get-Process FileUploader | Stop-Process`。

## 项目结构

```
src/FileUploader.Core/              与 UI 无关的存储层
  AppSettings.cs                    配置模型与校验
  AppPaths.cs                       配置/历史目录（%APPDATA% 或便携模式 exe\data）
  SettingsStore.cs                  settings.json 读写（密钥 DPAPI 加密）
  HistoryStore.cs                   history.json（最近 200 条）
  ZipPackager.cs                    多个文件打成一个 zip（进度 / 跳过坏文件 / 命名）
  UpdateChecker.cs                  检查更新：读 GitHub Release / 版本比较 / 提取「本次变更」/ SHA256
  ObjectKey.cs                      对象键构造 + RFC 3986 编码
  Storage/ObjectUrlBuilder.cs       直链 / 预签名链接（OSS V1、SigV4）+ 下载用地址
  ContentTypes.cs                   扩展名 → Content-Type
  Storage/Signers.cs                OSS V1 / SigV4 签名
  Storage/ObjectStorageClient.cs    PUT Object、HEAD/PUT Bucket、进度上报、错误解析
  Storage/ObjectDownloader.cs       GET 流式下载到本地文件（进度 / 临时文件 / 错误说明）
  Storage/StorageHttp.cs            上传与下载共用的 HttpClient
src/FileUploader.App/               WinUI 3 + Reactor 界面
  App.cs                            入口（单实例、文件参数、--settings）
  UploadApp.cs                      主窗口：头部、提示条、布局、跨窗口状态订阅
  UploadApp.DropZone.cs             拖拽区域（整块点击选题 + DragEnter/Over/Leave/Drop）
  UploadApp.UploadList.cs           上传历史列表（文件名搜索 / 下载 / 复制 / 重试 / 移除 / 清空）
  NativeBubbleWindow.cs             简洁模式：分层窗口 + GDI+ 绘制的圆形气泡
  FlashDropWindow.cs                拖拽浮现：隐形分层窗口 + 落点 + 淡入淡出状态机
  DropTargetInterop.cs              IDropTarget / CF_HDROP 互操作（拖放数据里取文件路径）
  DragWatcher.cs                    拖拽开始/结束探测（系统拖拽事件 + 低级鼠标钩子兜底）
  DragFlashController.cs            拖拽浮现气泡的装配与开关
  UploadQueue.cs                    全局上传队列（主窗口与气泡共用）
  AppWindows.cs                     窗口切换：主窗口 / 设置 / 气泡 / 托盘图标与菜单
  UploadApp.Uploading.cs            上传队列（并发 3、进度、取消、历史落盘）
  SettingsWindow.cs                 独立的设置窗口（存储 + 上传与链接 + 启动与托盘 + 关于与更新）
  StartupRegistration.cs            开机自启（HKCU Run 启动项：写入 / 删除 / 启动时校正）
  AppVersion.cs                     当前版本号（来自 csproj 的 <Version>，发版时由 tag 覆盖）
  SingleInstance.cs                 同一份 exe 只跑一份（锁名带 exe 路径哈希，副本之间互不影响）
  WindowForeground.cs               显示/激活窗口的 Win32 封装（含前台锁定绕法，托盘与单实例共用）
  ThemeBrushes.cs                   主题画刷的安全取法（取不到时用兜底颜色，别让 Render 抛异常）
  Updater.cs                        自更新：下载 + SHA256 校验 + 替换 exe + 重启 + 失败回滚
  SettingsState.cs                  跨窗口共享配置（INotifyPropertyChanged + 防抖落盘）
  DropExpander.cs                   拖入项 → 文件列表（文件夹递归；有路径时走 System.IO 快路径）
  UploadModels.cs                   上传条目 / 提示条模型
tools/FileUploader.Verify/          签名 / 上传 / 预签名链接 / 下载 / zip / 检查更新自测
tools/OssSdkCompare/                与阿里云官方 SDK 对比签名（回归用）
docs/                               文档与截图
```

## 自测与回归

```powershell
# 离线自测：签名向量 / 对象键 / URL / 配置往返 / DPAPI
dotnet run --project tools\FileUploader.Verify -c Release

# 其它子命令：exchange / presign / download / zip / update / upload / settings / delete-object
dotnet run --project tools\FileUploader.Verify -c Release -- zip
```

回归脚本清单与约定见 [AGENTS.md](../AGENTS.md) §5（全部在 `tools/` 下，优先 UI Automation 或直接消息断言，不依赖真实鼠标）。

## 本地用 Docker + RustFS 验证

不花钱、不碰真实 OSS 也能跑通整条链路：

```powershell
docker run -d --name fu-rustfs -p 9100:9000 -p 9101:9001 `
  -v ${PWD}\.testdata\rustfs:/data `
  -e RUSTFS_ACCESS_KEY=testadmin -e RUSTFS_SECRET_KEY=testadmin123 `
  rustfs/rustfs:latest
```

设置窗口里填：协议 `S3 兼容（SigV4）`、Endpoint `127.0.0.1:9100`、桶 `demo`、AK `testadmin`、SK `testadmin123`，取消「使用 HTTPS」（路径风格保持勾选）。

命令行验证签名与上传：

```powershell
# 真实上传一次，并打印生成的链接
$env:FU_ENDPOINT='127.0.0.1:9100'; $env:FU_BUCKET='demo'
$env:FU_AK='testadmin'; $env:FU_SK='testadmin123'
$env:FU_PROTOCOL='s3'; $env:FU_PATHSYLE='true'; $env:FU_HTTPS='false'
dotnet run --project tools\FileUploader.Verify -- upload
```

## 调试开关

启动参数（只为测试/演示，不要改变默认行为）：

| 参数 | 作用 |
|---|---|
| `--settings` | 启动直接打开设置窗口 |
| `--compact` | 以简洁模式（圆形气泡）启动 |
| `--menu-preview` | 启动即弹一次托盘菜单（UIA 回归用） |
| `--flash-preview` | 依次演示拖拽浮现气泡的四个状态（截图/回归用） |
| `--bring-preview` | 延迟把主窗口叫到前台一次（回归托盘激活用，延迟可用 `FU_BRING_PREVIEW_MS` 覆盖） |

环境变量（默认全部静默）：

| 变量 | 作用 |
|---|---|
| `FU_FLASH_DEBUG=1` | 拖拽浮现气泡的诊断日志写 `%TEMP%\fu-flash.log` |
| `FU_UPDATE_FEED=<url>` | 把「检查更新」的 feed 指到本地 JSON，回归脚本靠它跑真下载/真替换而不必发版 |
| `FU_ENDPOINT` / `FU_BUCKET` / `FU_AK` / `FU_SK` / `FU_PROTOCOL` / `FU_PATHSYLE` / `FU_HTTPS` / `FU_PREFIX` | `FileUploader.Verify` 的自测连接参数 |

## 踩过的坑

- **Reactor 的内置元素要"调一次工厂"才注册 handler**：直接 `new ContentDialogElement(...)` 会在 `Render` 里抛 `No handler is registered`，整个窗口变成错误页。启动时先调一次 `Factories.ContentDialog(...)` 登记类型（见 `App.RegisterDialogElementType`）。
- **`ThemeResource.Brush(key)` 找不到资源时是抛异常**，不是返回 null —— `?? 兜底` 写不到兜底里。统一走 `ThemeBrushes`（见该文件注释）。
- **运行中的 exe 可以改名但不能覆盖写**：自更新用「拷成 `.new` → 当前 exe 改名 `.old` → `.new` 顶替 → 启动新版」。
- **后台进程抢不到前台**（托盘点击）：`SetForegroundWindow` 会被前台锁定规则静默拒绝，需要 `AttachThreadInput` 挂到当前前台线程（见 `WindowForeground`）。
- **单实例与自更新会打架**：自更新「先起新版再退自己」前必须交还实例锁，否则新版会当成第二份而退出。
