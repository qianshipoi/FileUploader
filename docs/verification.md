# 已验证清单

这里记录**真的跑过**的验证结论（命令输出摘要），用于回归对照。发版前至少要保证 `dotnet build`、`FileUploader.Verify` 全绿，以及与本改动相关的回归脚本通过。


- OSS V1 `StringToSign` 与官方文档示例逐字符一致；SigV4 `Authorization` 与 AWS 官方 GET Object 示例签名完全一致（`tools/FileUploader.Verify` 的 `vectors` 用例全绿）。
- **预签名链接与阿里云官方 SDK 逐字段一致**：同一 AK/SK/有效期下，本项目生成的 `Signature` 与 `Aliyun.OSS.SDK.NetCore` 的 `GeneratePresignedUri` 完全相同（`tools/OssSdkCompare`）。
- 预签名链接实测（RustFS）：签名链接 GET → 200 且内容一致；同一对象的直链 GET → 403（对象确实是私有的）；把签名时间拨到 8 天前 → 403（过期失效）。
- UI 生成的签名链接直接可用：界面上传两个文件后，用历史里的链接匿名 GET → 200 / 1900 字节、200 / 29 字节。
- 对本机 Docker RustFS 端到端：拖入两个文件（含中文名/中文内容）→ `NoSuchBucket` 自动建桶并重试 → 上传成功 → 落盘字节与源文件逐字节一致。
- 历史持久化：关掉程序重开，两条记录与链接仍在（截图即重启后的界面）。
- 设置窗口用 UI 自动化（`tools/uia-settings.ps1`）实测过完整编辑链路：文本/密码/下拉/勾选都能保存到 `settings.json`，重开窗口回读正常，不点「保存」时 400ms 后也会自动落盘并显示「已保存 HH:mm:ss」。
- 失败态：网络不可达时红色提示条 + 行内错误 + 「重试/移除」。
- 整块点击热区实测：在拖拽区中心点一下，只弹 1 个系统文件对话框（`_isPicking` 连点保护），取消后无异常提示且不产生上传记录。
- 简洁模式反复进出 4 次（每次新建/销毁原生气泡窗口）进程稳定，无 `crash.log`；气泡窗口意外消失时主窗口会自动恢复。
- 拖拽浮现气泡（`tools/flash-drop-test.ps1`）：窗口贴在右下角工作区、预览显示期间可命中自己、装睡时点击穿透到下层窗口、`RegisterDragDrop` 返回 0、无 `crash.log`。
- 设置窗口与主窗口共享同一份配置：任一侧修改，另一侧即时刷新；密钥 DPAPI 加解密往返、落盘无明文。
- 新设置页布局用 UI 自动化验证过（`tools/uia-settings.ps1`，窗口标题改为「设置」）：Endpoint / 桶名 / AccessKey ID 按标题找到、协议下拉、HTTPS 与「静默启动」开关都能点，保存后 `settings.json` 里 `StartSilently` 为 `true`。
- 「静默启动」四种启动路径实测（便携模式，不碰真实配置）：开着 + 已配置 → 主窗口 `IsWindowVisible=False` 且进程存活；关掉 → 主窗口可见；开着但没配置 → 主窗口可见；开着且命令行带文件 → 主窗口可见。20ms 采样下窗口可见仅 1 帧（≈20ms），没有明显闪烁。
- 「开机自启」实测（便携副本，测完已删掉启动项）：UIA 打开开关 → `HKCU\...\Run` 出现 `FileUploader`，值是带引号的 exe 全路径且配置同步为 `true`；再点一下 → 启动项消失、配置回到 `false`；把启动项改指一条旧路径后重启 → 按当前 exe 重写、配置保持 `true`；直接把启动项删掉后重启 → 配置同步成 `false` 且不会偷偷加回来；其余 7 个用户启动项全程未被动过。
- 历史列表**倒序 + 按文件名搜索**用 UI 自动化实测（`tools/uia-history-search.ps1`，便携副本里放 4 条记录）：关键字「报告」只剩 1 行；`SCREEN` 这种大写关键字也能命中 `screenshot-settings.png`（不区分大小写）；无匹配时显示「0 / 4 条匹配」且一行都不留；点「清除」后搜索框清空、4 行全部回来。
- 「下载」端到端实测（便携副本 + 本机 HTTP 服务冒充对象存储，不碰真实桶）：点「下载」→ 系统「另存为」对话框弹出（文件名已填原名、位置在「下载」、类型 `原文件类型 (*.txt)`）→ 确认后文件落到 `Downloads\local-download.txt`，大小 37 B 与内容（含中文）逐字节一致，提示条显示保存路径；点「取消」则窗口关闭且不产生任何文件。
- `ObjectDownloader` 离线回归（`FileUploader.Verify download`，本机 TcpListener 冒充存储）：200 内容逐字节一致、进度回调到最终字节数、临时 `.part` 不残留；403/404 给出可读原因且不落文件；无效链接直接失败；取消抛 `OperationCanceledException`。
- 下载地址选择（`FileUploader.Verify presign`）：配置齐全时一律现签（直链模式也一样，私有桶才下得动），未配置时退回历史里的旧链接，两者都没有时返回空（界面提示先去配置）。
- 打包这一步用 `FileUploader.Verify zip` 离线自测（真文件 → 真 zip → 读回来比对）：压缩包内路径 `A/sub/b.txt`、内容逐字节一致、已压缩扩展名走得是 stored（压缩前后等长）、读不到的文件被跳过并计数、进度逐个上报、取消抛 `OperationCanceledException` 且不留半成品、全部读不到时报失败且不留文件。
- 「多个文件打包成 ZIP 上传」端到端实测（便携副本 + 本机假存储，不碰真实桶）：拖入 `季度资料`（3 个文件含中文名/中文内容）→ 只发一次 `PUT`，服务端收到的 `季度资料.zip` 里是 `季度资料/a.txt`、`季度资料/c.jpg`、`季度资料/子目录/b.txt`，内容逐字节一致；历史里只有 1 条记录；`%TEMP%\FileUploader` 里没有残留。
- 600 个文件 / 58.6 MB 的文件夹：只发一次 `PUT`（62 MB 的 zip，600 个条目），临时 zip 传完即删；打包中途点「取消」（UIA 在「打包中 25%」时点）→ 行变「已取消」、没有 PUT、没有历史、没有残留临时文件。
- 回归确认默认行为没变：关掉开关后，同一个文件夹仍是 3 个文件各自上传 3 次 `PUT`。
- 单个文件不打包：`FileUploader.exe a.txt` 直接上传 `a.txt`；两个散文件 → 一个 `打包.zip`（里面 `a.txt` / `b.txt` 在根目录）。
- 设置页开关用 UIA 实测（`tools/uia-settings.ps1 -ToggleZip`）：切换后 `settings.json` 里的 `ZipBatchUpload` 随之翻转（True → False），关窗口时自动落盘。
- 顺带修掉一个真实卡顿：拖入 600 个文件的文件夹时，WinRT 逐个 `GetBasicPropertiesAsync` 读大小要 ≈55 秒（拖进去像卡死）；改成有真实路径时走 `System.IO` 快路径后，同一文件夹 2.0 秒就出现「打包中」那一行。
- 修掉「窗口压在别的窗口下面时，点/双击托盘没反应」：根因是点托盘图标时用户输入是发给 shell 的，系统不认为本进程拿到最后一次输入，`SetForegroundWindow` 被前台锁定规则**静默拒绝**（实测返回 `false`：窗口一动不动，用「临时置顶再取消」也还是 `false`）。现在的做法是 `AttachThreadInput` 挂到当前前台线程 → `BringWindowToTop` → `SetForegroundWindow`（实测 `true`），最小化时先 `SW_RESTORE`；万一还是没拿到前台就写一条 `crash.log`（含两个 hwnd），不再静默失败。
- 托盘激活的回归脚本 `tools/tray-activate-test.ps1`（`--bring-preview` 触发和点托盘完全相同的那条路径）：记事本当遮挡物 → 断言本程序窗口可见但不是前台 → 触发 → 断言前台变成 `OSS 拖拽上传`。**同一脚本在「注释掉修复」的构建上会 FAIL**（前台仍是记事本），说明它真的抓得住这个 bug。
- 检查更新的离线自测（`FileUploader.Verify update`，26 项）：版本比较（v 前缀、同版本、预发布、`beta.10 > beta.9` 的字典序陷阱、非法版本号）、从 Release 说明里只抽「本次变更」那一节、SHA256 按资产名优先匹配、解析真实形状的 release JSON（选中 win-x64 exe、`digest` 归一化成小写、`size`、发布页链接）。
- 检查更新的**真实网络**实测：便携副本（版本 0.2.0）→ UIA 点「检查更新」→ 打 `api.github.com` 拿到最新 release `v0.2.0` → 显示「已经是最新版本（0.2.0）」。
- 自更新端到端（`tools/update-e2e-test.ps1`，本机假 feed + 假新版 exe，不碰真实 GitHub）：界面列出「发现新版本 9.9.9」和两条变更 → **摘要故意给错时拒绝替换并提示「校验失败…已丢弃，没有替换任何文件」**（exe 哈希不变）→ 换成正确摘要后真下载、真替换：旧进程退出、新进程起来、磁盘上的 exe SHA256 与 feed 一致、没有留下 `.new` 中间文件、缓存在 `%TEMP%\FileUploader\update-9.9.9\`，重启后 `.old` 被自动清理且程序正常运行。
- **真实 GitHub 自更新实测（v0.2.0 → v0.2.1）**：拿留档的 0.2.0 便携副本点「检查更新」→ 显示「发现新版本 0.2.1（当前 0.2.0） · 发布于 2026-11-18」并列出 15 条变更 → 点「立即更新并重启」→ 15 秒下完 93 MB（进度 0%→100%）→ 旧进程退出、新进程起来；更新后 exe 的 SHA256 与 Release 资产 digest 完全一致、设置页显示「当前版本 0.2.1」。
- 检查更新改成对话框后重跑整条回归（`tools/update-e2e-test.ps1`，17 项）：详情对话框列出「发现新版本 9.9.9」+ 变更条目、摘要不对时在「更新失败」对话框里说明原因且不替换、关闭后重新检查、真下载真替换真重启，全部 PASS；另外手工验证了下载中点「取消」→ 进程存活、exe 未被替换、`.part` 半成品不残留。
- 修掉「下载的 Release 一打开就是红字 Render error `Theme resource 'ControlStrokeColorDefaultBrush' not found`」：`ThemeResource.Brush(key)` 找不到资源时是**抛异常**而不是返回 null，原来的 `?? 兜底` 根本走不到。现在统一走 `ThemeBrushes`（多级 key 兜底 + try/catch + 固定颜色兜底，取不到时写一次 crash.log），并把 `CrashLog` 的路径改成跟随便携模式的 `data\`。
- 兜底的自测：临时把 4 处主题 key 全改成不存在的名字重新构建 → 主窗口照常渲染（10 个文本、无 Render error），同时 `crash.log` 里出现"主题资源取不到，已用兜底颜色：NopeStrokeBrush"；改回正式 key 后重新构建，虚线圈与图标颜色正常。
- 单实例与静默启动提示的回归（`tools/single-instance-test.ps1`）：同一份 exe 启动两次 → 只有一个进程、第二次把已有窗口叫到前台并自己退出（先拿记事本当遮挡物，断言前台真的换成了本程序）；不同目录的两份副本 → 可以各跑一份（锁名带 exe 路径哈希，回归脚本能同时跑多份）；静默启动（`StartSilently=true` + 已配置）→ 主窗口保持隐藏，右下角气泡浮现「已在托盘运行 / 点托盘图标打开」（截图确认）。
- **真实 GitHub 自更新实测（v0.2.2 → v0.2.3，含单实例改造）**：留档的 0.2.2 便携副本点「检查更新」→ 详情对话框列出 2 条变更 → 15 秒下完 93 MB → 旧进程退出、新进程接管（验证了自更新前主动交还实例锁，否则新版会把自己当成第二份而退出）→ 更新后 exe 的 SHA256 与 Release 资产 digest 一致、版本号 0.2.3。
- dotnet build（Debug/Release）与 dotnet publish 均无警告无错误。
