# 分发与部署

## 两种分发方式

| | 安装版（推荐给普通用户） | 便携版 |
|---|---|---|
| 产物 | `FileUploader-<版本>-setup.exe` | `FileUploader-<版本>-win-x64.exe`（单文件） |
| 装到哪 | 当前用户的 `%LOCALAPPDATA%\Programs\FileUploader`（**免管理员**） | 你放哪就在哪（桌面、D 盘、U 盘…） |
| 快捷方式 | 开始菜单「OSS 拖拽上传」+ 卸载项（控制面板可见） | 无 |
| 配置与历史 | `%APPDATA%\FileUploader`（卸载不删） | 默认同上；放 `portable.txt` 就跟着 exe 走 `data\` |
| 更新 | 点「立即更新并重启」→ 下载安装包 → **静默升级并自动重启** | 点「下载新版」→ 只把新版 exe 下到旁边，**不替换自己** |

判定是哪种，靠安装包在 `HKCU\Software\FileUploader\InstallPath` 写下的安装目录 + exe 旁边有没有 `portable.txt`：
只有"注册表里的安装目录 == 当前 exe 所在目录、且没有 portable.txt"才算安装版；把 exe 拷走自己用（或放 U 盘）的人一律按便携版处理，绝不会被自动替换。

## 便携版（发给别人用）

把 `dist-portable\FileUploader.exe` 单独一个文件拷给对方即可，目标机器**不需要装 .NET、也不需要装 Windows App Runtime**。

- 系统要求：Windows 10 1809+ / Windows 11，x64。
- 默认配置写在 `%APPDATA%\FileUploader`（多用户互不影响）。
- 想让配置跟着 exe 走：在 exe 同级目录放一个空的 **`portable.txt`**，配置与历史就写到 `data\settings.json`、`data\history.json`，整个文件夹拷到 U 盘也能带走。
- exe 没有代码签名，别人首次运行可能弹「Windows 已保护你的电脑」→ 点「更多信息 → 仍要运行」；正式分发建议自己签名。
- 放在有写权限的目录（桌面、D 盘、U 盘）；别放 `C:\Program Files` 下（便携模式的 data 目录需要可写，下载新版也需要）。
- 便携版的更新是"**只下载**"：新版 exe 落在 `exe目录\update-<版本>\`，用户自己决定什么时候覆盖。

## 安装版（推荐）

```powershell
powershell -File build-installer.ps1            # → dist\FileUploader-<版本>-setup.exe
```

- 每用户安装，**不需要管理员权限**：装到 `%LOCALAPPDATA%\Programs\FileUploader`，注册表只写 `HKCU\Software\FileUploader`。
- 卸载：开始菜单「卸载 OSS 拖拽上传」，或者设置 → 应用 → 已安装的应用里找到它；卸载会清掉程序目录、快捷方式、注册表标记和升级缓存（`%LOCALAPPDATA%\FileUploader`），**配置与历史留着**（`%APPDATA%\FileUploader`）。
- 自动更新为什么必须每用户安装：静默升级要能改写程序文件，装在 `Program Files` 的话每次更新都得弹 UAC，"无感"就没了。
- 静默升级由应用自己发起：下载 `FileUploader-<版本>-setup.exe` → 校验 SHA256 → `/VERYSILENT` 运行 → 安装器换文件、刷新快捷方式、写卸载项，然后把程序重新拉起来。

## 发给别人：导出配置 → 对方首次启动自动导入

因为密钥用 DPAPI 加密、换机器解不开，所以分发靠一个**导出文件**：

1. 自己机器上先配好、点「测试连接」确认可用。
2. 设置窗口 → **关于** → 点 **「导出配置」** → 在 exe 同目录生成 `FileUploader.export.bin`。
3. 把 `FileUploader.exe` + `FileUploader.export.bin` 放进同一个目录发给对方。
4. 对方双击 exe：
   - 自动导入配置并写入本机存储（密钥重新用 DPAPI 加密）；
   - 自动测一次连接（含建桶）；
   - **测通后才删除**同目录的导出文件；
   - 测不通则保留该文件、界面提示原因，修好后重新打开会再次导入。
5. 任何时候只要同目录还有导出文件，设置窗口顶部都会出现提示和 **「删除它」** 按钮。

导出文件的格式与强度：

| 项 | 说明 |
|---|---|
| 格式 | 二进制：`FUX1` 魔数 + 版本 + 12 字节 nonce + 16 字节 GCM tag + 密文；记事本/编辑器打开是乱码，不包含任何字段名或明文（只接受这一种格式，明文 JSON 会被拒绝） |
| 加密 | AES-256-GCM（认证加密，改一个字节都解不开），密钥由程序内置常量经 PBKDF2-SHA256 派生 |
| 强度定位 | **防窥不防逆向**：能挡住"双击打开看一眼"，挡不住有意的逆向分析。真要强保护，请用临时 AK + 最小权限，或分发后再改密钥 |
