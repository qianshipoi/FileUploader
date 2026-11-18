# OSS 拖拽上传

一个 Windows 桌面小工具：配好阿里云 OSS（或任意 S3 兼容服务）之后，**把文件拖进窗口就能上传，并直接拿到可访问的链接**。

界面完全用 [Microsoft.UI.Reactor](https://github.com/microsoft/microsoft-ui-reactor)（WinUI 3 上的声明式 C# UI 框架）写成：没有 XAML、没有 ViewModel、没有数据绑定 —— 一棵 `Element` 树 + `UseState` 状态钩子。

![上传完成](docs/screenshot-upload.png)

## 功能一览

**上传**
- 拖拽即上传，支持多文件；拖文件夹递归展开（最多 2000 个文件）；整块区域点击也能选文件
- 并发 3 路上传、实时进度、失败重试 / 取消
- 「多个文件打包成 ZIP 上传」：一次拖入多个文件时先打成一个 zip 再传
- 对象键可配：前缀 + 日期目录 + 防重名段 + 原文件名（同一天重名不覆盖，URL 末段仍是原名）
- 两种签名协议：阿里云 OSS V1 / AWS SigV4（MinIO、RustFS 等）；桶不存在会自动建

**链接**
- 默认私有桶 + 预签名链接（默认 7 天，每次复制都重新签名）；也可切直链 + 自定义（CDN）域名
- 上传历史持久化（最近 200 条），按时间倒序、可按文件名搜索、可直接「下载」回本地

**窗口与常驻**
- 托盘常驻（关闭 = 收进托盘，只有「退出」才真正结束进程）
- 简洁模式：收成右下角圆形气泡，拖文件上去就传
- 拖拽浮现：平时隐形，一开始拖文件才在右下角浮现，拖上去松手即传
- 静默启动（只进托盘，带一次「已在托盘运行」提示）、开机自启（当前用户启动项，免管理员）
- 单实例：重复双击只把已有窗口叫到前台，不会堆出多份后台进程

**其它**
- 检查更新（手动）：读 GitHub Release，列出「更新了哪些地方」，一键下载 → 校验 SHA256 → 替换 exe → 重启
- 命令行/拖到 exe 图标上即可上传；配置可导出成分发文件，对方首次启动自动导入

## 运行环境

- Windows 10 1809+ / Windows 11，x64
- 单文件便携版自带 .NET 10 运行时与 Windows App SDK，目标机器**什么都不用装**

## 快速开始

```powershell
# 构建（Debug / Release 都要求 0 警告）
dotnet build src\FileUploader.App\FileUploader.App.csproj -c Release

# 直接运行（调试）
dotnet run --project src\FileUploader.App

# 发布单文件便携版 → dist-portable\FileUploader.exe
powershell -File publish-portable.ps1
```

| 产物 | 内容 | 适用 |
|---|---|---|
| `dist-portable\FileUploader.exe` | 单文件便携版（≈89 MB，自带运行时） | 发给别人，拷过去双击就用 |
| `dist\` | 目录版（多文件，需要目标机器装 .NET 10 运行时） | 本机开发自用 |

首次使用：右上角「设置」→ 填 Protocol / Endpoint / 桶名 / AK / SK → 「测试连接」→ 回主窗口拖文件。

## 文档

| 文档 | 内容 |
|---|---|
| [使用手册](docs/usage.md) | 界面分工、设置项、历史搜索与下载、ZIP 打包、链接类型、简洁模式、拖拽浮现、托盘、静默启动、单实例、开机自启、检查更新、命令行、数据文件 |
| [分发与部署](docs/deployment.md) | 便携版怎么发、导出配置给别人的流程与加密格式 |
| [开发指南](docs/development.md) | 技术栈、构建/发布、项目结构、自测与回归、Docker + RustFS 本地验证、调试开关、踩过的坑 |
| [发版](docs/release.md) | tag 触发流水线、版本号要两边一致、推荐步骤 |
| [已验证清单](docs/verification.md) | 真的跑过的验证结论（回归对照用） |
| [排查与常见问题](docs/troubleshooting.md) | 双击没窗口、白屏/红字、检查更新与自更新失败、已知限制 |
| [AGENTS.md](AGENTS.md) | 给在本仓库工作的 AI 代理/协作者的约定（提交规范、回归脚本、代码风格） |

## 发版

推一个 `v` 开头的 tag，GitHub Actions 会打包单文件便携版并发布到 Release。细节与注意事项见 [发版](docs/release.md)。

## 已知限制

- 只做单次 `PUT Object`，没有分片/断点续传（OSS 与 S3 简单上传上限 5 GB）
- 检查更新要能访问 `api.github.com`；自更新只能替换有写权限目录下的 exe
- 只支持 Windows，界面文字为中文；Reactor 仍是 preview 版

详见 [排查与常见问题](docs/troubleshooting.md#已知限制)。
