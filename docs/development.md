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

# 安装包 → dist\FileUploader-<版本>-setup.exe（需要 Inno Setup 6.3+ 的 ISCC.exe）
powershell -File build-installer.ps1
```

| 目录 | 内容 | 适用 |
|---|---|---|
| `dist\FileUploader-<版本>-setup.exe` | **安装包**（每用户安装到 `%LOCALAPPDATA%\Programs\FileUploader`，开始菜单 + 卸载项，≈83 MB） | 发给普通用户，更新点一下就静默升级 |
| `dist-portable\FileUploader.exe` | **单文件便携版**（≈89 MB，自带 .NET 10 运行时 + Windows App SDK） | 拷过去双击就用，更新只下载 |
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
  UpdateChecker.cs                  检查更新：读 GitHub Release / 版本比较 / 提取「本次变更」/ 资产与 SHA256
                                     （同时认单文件便携版 `-win-x64.exe` 与安装包 `-setup.exe`）
  ObjectKey.cs                      对象键构造 + RFC 3986 编码
  Storage/ObjectUrlBuilder.cs       直链 / 预签名链接（OSS V1、SigV4）+ 下载用地址
  ContentTypes.cs                   扩展名 → Content-Type
  Storage/Signers.cs                OSS V1 / SigV4 签名
  Storage/ObjectStorageClient.cs    PUT Object、分片上传（Initiate/UploadPart/Complete/Abort）、HEAD/PUT Bucket、进度上报、错误解析
  Storage/MultipartSession.cs       分片上传会话（uploadId / 分片大小 / 已传分片）与落盘（uploads\*.json）
  Storage/MultipartUploader.cs      大文件分片上传与断点续传（阈值、分片切法、续传、单片重试）
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
  SettingsWindow.cs                 独立的设置窗口（左侧分类：连接 / 上传 / 启动 / 关于；输入框点「保存」才落盘）
  StartupRegistration.cs            开机自启（HKCU Run 启动项：写入 / 删除 / 启动时校正）
  AppVersion.cs                     当前版本号（来自 csproj 的 <Version>，发版时由 tag 覆盖）
  SingleInstance.cs                 同一份 exe 只跑一份（锁名带 exe 路径哈希，副本之间互不影响）
  WindowForeground.cs               显示/激活窗口的 Win32 封装（含前台锁定绕法，托盘与单实例共用）
  ThemeBrushes.cs                   主题画刷的安全取法（取不到时用兜底颜色，别让 Render 抛异常）
  InstallLocation.cs                安装版/便携版判定（注册表 InstallPath + portable.txt + exe 目录）
  Updater.cs                        更新：安装版下载 setup.exe 静默升级、便携版只下载；SHA256 校验、可写性预检
  SettingsState.cs                  跨窗口共享配置（INotifyPropertyChanged + 防抖落盘）
  DropExpander.cs                   拖入项 → 文件列表（文件夹递归；有路径时走 System.IO 快路径）
  UploadModels.cs                   上传条目 / 提示条模型
tools/FileUploader.Verify/          签名 / 上传 / 分片续传 / 预签名链接 / 下载 / zip / 检查更新自测
installer/FileUploader.iss           Inno Setup 安装包脚本（每用户安装、开始菜单、注册表 InstallPath）
build-installer.ps1                  打安装包：publish 单文件版 → ISCC 编译 → dist\*-setup.exe
tools/OssSdkCompare/                与阿里云官方 SDK 对比签名（回归用）
docs/                               文档与截图
```

## 自测与回归

```powershell
# 离线自测：签名向量 / 对象键 / URL / 配置往返 / DPAPI
dotnet run --project tools\FileUploader.Verify -c Release

# 其它子命令：exchange / presign / download / zip / multipart / update / upload / settings / delete-object
dotnet run --project tools\FileUploader.Verify -c Release -- zip

# 分片上传 + 断点续传（离线跑本机假 S3；设了 FU_ENDPOINT 会额外跑真存储）
dotnet run --project tools\FileUploader.Verify -c Release -- multipart
```

回归脚本清单与约定见 [AGENTS.md](../AGENTS.md) §5（全部在 `tools/` 下，优先 UI Automation 或直接消息断言，不依赖真实鼠标）。

设置页的保存策略回归（会临时改 `settings.json` 里的 `KeyPrefix`，跑完自动改回去）：

```powershell
# 输入框不落盘 → 点保存才写盘 → 关窗提示 → 不保存丢草稿（16 项）
pwsh -NoProfile -File tools\settings-save-test.ps1
```

更新的两条端到端回归：

```powershell
# 便携版：检查更新 → 下载新版 → 校验 → 只下载、不替换自己（用本机假 feed）
pwsh -NoProfile -File tools\update-e2e-test.ps1

# 安装版：真安装 → 本机假 feed 说"有 9.9.9" → 静默升级 → 安装器把程序拉起来 → 真卸载
pwsh -NoProfile -File tools\update-installed-e2e-test.ps1
```

## 打安装包（Inno Setup）

安装包不是 NuGet 依赖，是一个 `.iss` 脚本 + Inno Setup 的编译器 `ISCC.exe`：

```powershell
# 本机：装一份 Inno Setup（winget，或从 GitHub Releases 下 innosetup-*-x64.exe 静默装）
winget install --id JRSoftware.InnoSetup -e
powershell -File build-installer.ps1                    # → dist\FileUploader-<版本>-setup.exe
powershell -File build-installer.ps1 -SourceExe out\FileUploader.exe -SkipPublish   # CI 里复用已 publish 的产物
```

`installer/FileUploader.iss` 的关键点：

| 位置 | 作用 |
|---|---|
| `DefaultDirName={localappdata}\Programs\FileUploader` + `PrivilegesRequired=lowest` | 每用户安装，装与升级都不要管理员（否则每次更新弹 UAC） |
| `[Registry] HKCU\Software\FileUploader InstallPath` | 应用据此判断"安装版"；exe 被拷走就对不上 → 自动退化成便携版 |
| `[Icons] {group}` | 开始菜单「OSS 拖拽上传」+「卸载」 |
| `[Run] ... Check: WizardSilent` | **静默升级后把程序重新拉起来**（用户点完「立即更新」不该什么都不剩） |
| `[Run] ... postinstall skipifsilent unchecked` | 交互式安装完成页的「运行」勾选项，默认不勾 |
| `[UninstallDelete] {localappdata}\FileUploader` | 只清升级缓存；`%APPDATA%\FileUploader`（配置与历史）卸载不动 |

CI 里由 `.github/workflows/release.yml` 自己下一份 `innosetup-7.1.1-x64.exe` 静默安装到 `%RUNNER_TEMP%`，再用 `-SourceExe out\FileUploader.exe -SkipPublish` 复用同一次 publish 的产物。

## 本地用 Docker + RustFS 验证

不花钱、不碰真实 OSS 也能跑通整条链路。**用命名卷，别用 Windows 绑挂目录**（原因见下方踩坑表）：

```powershell
docker run -d --name fu-rustfs -p 9100:9000 -p 9101:9001 `
  -e RUSTFS_ACCESS_KEY=testadmin -e RUSTFS_SECRET_KEY=testadmin123 `
  rustfs/rustfs:latest
```

设置窗口里填：协议 `S3 兼容（SigV4）`、Endpoint `127.0.0.1:9100`、桶 `demo`、AK `testadmin`、SK `testadmin123`，取消「使用 HTTPS」（路径风格保持勾选）。

命令行验证签名与上传（`multipart` 会把分片上传、续传、Abort 全跑一遍）：

```powershell
# 真实上传一次，并打印生成的链接
$env:FU_ENDPOINT='127.0.0.1:9100'; $env:FU_BUCKET='demo'
$env:FU_AK='testadmin'; $env:FU_SK='testadmin123'
$env:FU_PROTOCOL='s3'; $env:FU_PATHSYLE='true'; $env:FU_HTTPS='false'
dotnet run --project tools\FileUploader.Verify -- upload
dotnet run --project tools\FileUploader.Verify -- multipart
```

### 断点续传的界面回归（`tools/uia-resume-test.ps1`）

要一份便携副本 + 一个几百 MB 的文件，把上传打断、再点「继续」：

```powershell
# 1. 便携副本（把构建输出拷出来，放个 portable.txt 就走便携模式，不碰真实配置）
$e2e = "$env:TEMP\fu-e2e"
Copy-Item -Recurse src\FileUploader.App\bin\Release\net10.0-windows10.0.22621.0\win-x64\* "$e2e\app"
's' | Set-Content "$e2e\app\portable.txt"
dotnet run --project tools\FileUploader.Verify -- settings "$e2e\app\data\settings.json"   # 写入测试配置

# 2. 传一个大文件，几秒后杀掉进程 → data\uploads 里留下分片进度
$p = Start-Process "$e2e\app\FileUploader.exe" -ArgumentList "`"$e2e\files\巨大文件.bin`"" -PassThru
Start-Sleep -Seconds 5; Stop-Process -Id $p.Id -Force

# 3. 重新打开（不带参数）：列表里出现「已中断 · 已传 xx%」，脚本点「继续」并等到「已上传」
Start-Process "$e2e\app\FileUploader.exe"
pwsh -File tools\uia-resume-test.ps1 -DumpTexts -DumpButtons
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
- **判定"安装版"不能只看 `portable.txt`**：用户把便携 exe 拷到桌面直接跑（没有 portable.txt），按"没标记就是安装版"判就会去自动替换那个 exe。现在只有"安装包写的 `HKCU\Software\FileUploader\InstallPath` == 当前 exe 目录、且没有 portable.txt"才算安装版。
- **便携版不再自我替换**（0.4.0 起）：便携版就是用户手上的一个文件，自替换在 U 盘/只读目录/多副本场景下只会失败，而且用户没法留旧版回滚。现在便携版只「下载新版 + 给出路径和 SHA256」，替换留给人。
- **静默升级要有人把程序拉起来**：应用启动安装器后自己就退出了，`[Run]` 里配 `Flags: nowait; Check: WizardSilent` —— 只在静默（=自动更新）时执行，交互式安装仍由用户在完成页勾选「运行」。
- **从工作区里启动的 exe 会被沙箱化（低完整性）**：这类进程写不了 `%TEMP%`（写 `%TEMP%\FileUploader\update-x` 直接 `Access denied`），更新会失败。这是环境限制不是程序 bug —— 把 exe 放到工作区外（例如 `%LOCALAPPDATA%`）再测就正常；用 `Get-Process` 拿不到，需要读进程令牌的完整性级别才能看出来。
- **Inno 卸载时"文件被占用"是常态**：卸载瞬间 exe 可能还被上一个进程的句柄占着，日志里是 `Failed to delete the file; it may be in use (5)`，Inno 会安排重启时删除；回归脚本里对这一点做了重试。
- **后台进程抢不到前台**（托盘点击）：`SetForegroundWindow` 会被前台锁定规则静默拒绝，需要 `AttachThreadInput` 挂到当前前台线程（见 `WindowForeground`）。
- **单实例与自更新会打架**：自更新「先起新版再退自己」前必须交还实例锁，否则新版会当成第二份而退出。
- **分片上传的签名要带上子资源**：`?uploads` / `?partNumber=N&uploadId=...` / `?uploadId=...` 都要进待签名字符串。OSS V1 只把**子资源**按名字排序拼进 CanonicalizedResource、**值不编码**（`?uploads` 连 `=` 都不要）；SigV4 则要求所有参数排序 + 严格编码、空值写成 `uploads=`。两者不能共用一套拼法（见 `ObjectStorageClient.BuildOssCanonicalResource` / `BuildSigV4CanonicalQuery`，`FileUploader.Verify multipart` 里有逐字符断言）。
- **OSS 的 CompleteMultipartUpload 出错也回 200**：错误放在响应体里（`<Error>…`）。只看状态码会把"没合并成功"当成成功，链接打开是 404 —— 现在成功分支也会扫一遍响应体。
- **同一份 HttpClient 复用的请求体必须自己截断**：`ProgressStreamContent` 原来一直读到 `Stream` 末尾，分片上传时底层是同一个文件的 FileStream，第二片会把后面几片一起塞进去（Content-Length 对不上，连接被服务端掐断）。现在按 `length` 读到够数就停。
- **续传必须沿用原来的对象键和分片大小**：键一变就是另一个对象，分片大小一变分片边界就跟服务端已经收下的那些对不上。所以会话里存了键和 `PartSize`，`UploadQueue` 在入队时先用 `MultipartUploader.FindPending` 找有没有可续传的会话并沿用它的键。
- **RustFS 1.0.6 + Windows 绑挂目录（`-v ${PWD}\...:/data`）能初始化、能传分片，但 CompleteMultipartUpload 一定回 404 `NoSuchKey`**：`multipart/` 和 `multipart-index-v1/` 都在，`.mpu-completions/…/original.xl.meta` 也写了，就是合并不成功；换成 Docker 命名卷（或 Linux 主机）立刻正常。所以本地验证分片上传用命名卷；简单的 `PUT Object` 不受影响，这就是当初用绑挂目录也能跑通上传、下载、预签名链接的原因。
- **RustFS 的 Abort 之后目录还在**：`.rustfs.sys/multipart/<hash>` 可能留一个空壳，但语义上已经没了（再拿这个 uploadId 去 Complete 会回 `NoSuchUpload`）。别拿"目录还在"当成没清掉。
- **`ReactorWindow.Close()` 在按钮/对话框回调里不生效**：实测窗口不关，`Closing` 和关窗守卫一次都没被调用。改成把关闭投递到 UI 队列（`NativeWindow.DispatcherQueue.TryEnqueue(() => window.Close())`），走的是和点窗口 × 一样的路径。
- **关窗守卫是"钩子"**：`UseClosingGuard(predicate)` 必须直接在 `Render` 里无条件调用；塞进 `UseEffect` 会被分析器拦下（`REACTOR_HOOKS_001`，钩子顺序不稳定），而且它返回 `void`，不用自己注销。
- **UIA 的 `ValuePattern.SetValue` 落到受控 TextBox 上偶尔会粘出脏值**（重绘时挨着插入，出现过 `cfostmp-x` 这种）：回归脚本 SetValue 之后必须读回来比对，不一致就当场报错。
- **点「保存」会重建页脚按钮**：紧接着的程序化点击偶尔落在旧按钮上而"没反应"（真人手速不会这么快）。回归脚本因此在保存后等一帧再继续点，关窗那一步允许重试一次。
- **设置页的导航用原生 `NavigationView`**（`Factories.NavigationView(items, content)` + `with { SelectedTag = ... }`）：自己用 Button 拼一排导航，怎么调都差一口气——选中条、悬停、键盘导航、窄窗口收成图标栏这些系统行为都得手写。注意它在 UIA 里是 **ListItem + SelectionItemPattern**，不是 Button，回归脚本按名字找元素后要优先用 `SelectionItemPattern.Select()`。
