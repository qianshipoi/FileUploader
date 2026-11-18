# AGENTS.md

给在本仓库工作的 AI 代理（以及新加入的人）的约定。**与用户当次指令冲突时，以用户当次指令为准。**

## 1. 最高优先级：不要自动提交

- **禁止在每轮对话结束时自动 `git commit`。** 只有用户明确说「提交」（或「提交并推送」「发版」等）才允许提交。
- 完成改动后，**默认停在"工作区已修改、未提交"的状态**，然后报告：改了哪些文件、验证结果、还剩什么没做，并**问一句要不要提交**。
- **禁止自动 `git push`、自动 `git tag`、自动发 Release。** 推送与发版都必须由用户明确要求。
- 禁止 `git commit --amend` 改写已有提交、禁止 `git push --force`、禁止 `git reset --hard` 丢弃用户改动（除非用户点名要求，且先确认要丢的是哪部分）。
- 提交前发现工作区有**不是自己改的内容**，先问清楚再动，不要 `git add -A` 一把梭。

## 2. Git 提交规范

### 2.1 信息格式

Conventional Commits，**标题英文类型 + 中文描述**，正文用中文分点说明。

```
<type>(<scope>): <中文一句话说明，不超过 50 字>

- 变更点 1（说清"为什么"，不只是"改了什么"）
- 变更点 2
```

- `type`：`feat` 新功能 / `fix` 修 bug / `style` 纯外观调整 / `refactor` 重构 / `perf` 性能 / `docs` 文档 / `test` 测试 / `build` 构建脚本 / `chore` 杂项。
- `scope` 常用：`ui`、`core`、`storage`、`tools`、`ci`、`docs`。
- 标题不加句号，不写 `update`、`fix bug` 这类无信息量的描述。
- 正文不写"由 AI 生成"之类的元信息。

### 2.2 粒度

- 一个提交只做一件事：功能与它的回归脚本可以同一个提交；纯样式调整和逻辑修改尽量分开。
- 跨文件的机械改动（重命名、格式化）单独成提交，不要混进功能提交。
- 修 bug 的提交要写清**触发条件**（例如"拖离气泡后 300ms 被宽限定时器收掉"），方便以后回查。

### 2.3 提交前检查清单（缺一不可）

1. `Get-Process FileUploader | Stop-Process` 关掉正在运行的程序（否则 `dotnet build` 会出现 `MSB3026` 文件占用重试警告）。
2. `dotnet build src\FileUploader.App\FileUploader.App.csproj -c Release` → **0 警告 0 错误**。
3. `dotnet run --project tools\FileUploader.Verify -c Release` → **ALL PASS**。
4. 与改动相关的回归脚本通过（见 §5）。
5. `git status` 里没有误入的临时文件（`bin/`、`obj/`、`dist/`、`dist-portable/`、`.testdata/`、`crash.log`、`*.export.bin` 已在 `.gitignore` 中，别强行加入）。

## 3. 分支与发版

- 主分支 `main`，线性历史优先，不保留无意义的 merge 提交。
- 发版靠 tag：`git tag vX.Y.Z && git push origin vX.Y.Z`，触发 `.github/workflows/release.yml` 打包单文件版并发布到 GitHub Release。**只由用户决定版本号**。
- `ci.yml` 在 push / PR 时跑构建与自测；改 CI 文件后要提醒用户可能需要 `workflow` 权限的凭据。

## 4. 代码规范

### 4.1 技术栈与结构

- WinUI 3 + **Microsoft.UI.Reactor 0.1.0-preview.16**（声明式 C# UI，没有 XAML）；Windows App SDK 2.3.1；`net10.0-windows10.0.22621.0`，非打包（`WindowsPackageType=None`）。
- `src/FileUploader.App` 界面层，`src/FileUploader.Core` 纯逻辑（签名、存储、配置、历史），`tools/*` 验证与回归脚本。
- **新增 NuGet 依赖必须先征得用户同意**；能用手写 P/Invoke 解决的小问题不要引入第三方包。

### 4.2 C#

- 保持现有风格：文件级 `namespace`、`internal`/`sealed` 优先、`Nullable` 全开、`var` 用于类型明显的场合。
- **注释与 UI 文案用中文**；注释解释"为什么这么写"（踩过的坑要写进去），不要复述代码。
- 每加一个 P/Invoke 结构体/委托，注意：委托必须用字段持有（否则被 GC 回收导致回调崩溃）；窗口类只注册一次。
- 异常不要静默吞掉：能兜底的地方兜底，但要么写 `crash.log`，要么给出用户可见的提示。

### 4.3 原生窗口 / 互操作

- 需要逐像素透明、无焦点、置顶的界面用**原生分层窗口**（`WS_EX_LAYERED` + `UpdateLayeredWindow`），不要试图用 WinUI 窗口硬凑。
- 拖放落点用 `RegisterDragDrop` + `IDropTarget`（能拿到 `DragEnter` 与数据），`DragAcceptFiles` 只在"松手即可"的场景用。
- 需要"点不到"的窗口用 `WS_EX_TRANSPARENT` 开关；**不要指望逐像素 alpha 分层窗口的 `WM_NCHITTEST` 生效**（实测 `WindowFromPoint` 不调用它）。
- 全局钩子（`WH_MOUSE_LL` / WinEvent）装在**专用 STA 线程**上，回调里只做字段判断，任何 UI/IO 都投递回窗口线程。

### 4.4 调试开关

- 临时诊断日志必须用环境变量开关，默认静默，例如 `FU_FLASH_DEBUG=1` 才会写 `%TEMP%\fu-flash.log`。
- 开发用的预览入口用启动参数（`--menu-preview`、`--flash-preview`、`--bring-preview`、`--compact`），不要为了测试改默认行为。`--bring-preview` 的延迟可用环境变量 `FU_BRING_PREVIEW_MS` 覆盖（回归脚本要在这之前把遮挡物摆好）。

## 5. 构建、运行与回归

```powershell
# 构建（Debug / Release 都要求 0 警告）
dotnet build src\FileUploader.App\FileUploader.App.csproj -c Release

# 直接跑
dotnet run --project src\FileUploader.App

# 单文件便携版
powershell -File publish-portable.ps1     # → dist-portable\FileUploader.exe

# 自测（签名向量、对象键、URL、配置往返、DPAPI）
dotnet run --project tools\FileUploader.Verify -c Release
```

回归脚本（`tools/`）：

| 脚本 | 覆盖内容 |
|---|---|
| `uia-settings.ps1` | 设置窗口编辑链路（UI Automation，不依赖鼠标） |
| `uia-history-search.ps1` | 历史记录：按文件名搜索 / 清除搜索 / 「下载」弹出的保存对话框（不依赖鼠标） |
| `uia-tray-menu.ps1` | 托盘菜单结构与点击 |
| `tray-activate-test.ps1` | 托盘激活：窗口被别的窗口压住时点托盘要能把它叫到前台（用 `--bring-preview` 触发同一条路径） |
| `uia-header-gear.ps1` / `uia-click.ps1` / `uia-pick.ps1` / `uia-invoke-test.ps1` | 主窗口控件交互 |
| `compact-race-test.ps1` | 简洁模式快速进出、气泡与主窗口竞态 |
| `bubble-placement-test.ps1` | 气泡拖拽与位置记忆 |
| `tray-menu-dismiss-test.ps1` | 托盘菜单点外关闭 |
| `menu-shadow-probe.ps1` | 菜单阴影几何 |
| `flash-drop-test.ps1` | 拖拽浮现气泡：位置 / 命中穿透 / 落点注册 / 拖离后不消失 / 设置开关 / 拖选文字不误报 |
| `real-drag-onto-bubble.ps1` | 真实拖拽验证：资源管理器拖文件到气泡（ESC 取消，不会上传） |
| `capture*.ps1`、`make-icon.py` | 截图与图标生成 |

约定：

- **优先 UI Automation 或直接消息断言**，避免依赖真实鼠标/屏幕；必须用鼠标时（如真拖拽），结束后要恢复用户光标位置。
- 涉及真实上传的测试会**真的写入用户的 OSS 桶**，做之前先问用户。
- 桌面被锁屏时截屏与输入注入会失败（`GetForegroundWindow` 为 0），这不是代码 bug，先确认桌面状态再排查。

## 6. 环境事实

- 目标系统 Windows 10 1809+ / Windows 11；当前开发机为 Windows 10 22H2（19045），单屏 2560×1440 + 副屏 1920×1080。
- 运行时数据目录：`%APPDATA%\FileUploader`（`settings.json`、`history.json`、`bubble.json`、`app.ico`、`crash.log`）；便携模式下跟随 `exe\data`。
- `gh` 访问 github.com 需要代理：`$env:HTTPS_PROXY='http://127.0.0.1:12000'`；`git push` 已配全局代理。

## 7. 安全与隐私

- AK/SK 只以 DPAPI 加密形式落盘；**任何日志、异常信息、提交内容里都不能出现明文密钥**。
- 导出的配置（`FileUploader.export.bin` 等）含密钥，已被 `.gitignore` 排除，不要提交、也不要打印内容。
- 涉及删除/覆盖用户数据（历史记录、配置文件、远端对象）的操作，先说明后果再执行。

## 8. 文档

- 面向用户的行为变化要同步 `README.md`（中文），保持「功能列表 + 分节说明 + 已验证」三段结构；技术细节写在引用块里。
- 新增文件后在 README 的目录树里补一行说明。
- 截图放 `docs/`，命名 `screenshot-<场景>.png`。

## 9. 沟通方式

- 动手前先给一句计划（改哪些文件、怎么验证）；改动范围超出用户描述时先确认。
- 完成后报告三件事：**改了什么 / 验证了什么 / 还剩什么**，并附上验证命令的真实输出摘要。
- 不确定就问；不要替用户做产品决策（默认行为、版本号、是否推送）。
