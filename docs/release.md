# 发版（GitHub Release，全自动）

推一个 `v` 开头的 tag，GitHub Actions 就会在 Windows runner 上打包**单文件便携版**并发布到 Release：

```powershell
git tag v0.1.0            # 版本号自定，tag 形如 v1.2.3 / v1.2.3-beta.1
git push origin v0.1.0    # 推送 tag 即触发
```

- 流水线：`.github/workflows/release.yml`（tag 触发；也可在 Actions 页面手动输入 tag 重发，已存在的 Release 会覆盖上传）。
- 产物：`FileUploader-<版本>-win-x64.exe`（≈89 MB，自带 .NET 10 运行时 + Windows App SDK）与同名 `.zip`。
- Release 说明自动带上下载表格、SHA256 校验值和「上一个 tag → 本次 tag」的提交清单；tag 带 `-` 的（如 `v1.2.3-beta.1`）自动标为 pre-release。
- **版本号要两边一致**：程序里的版本来自 `src/FileUploader.App/FileUploader.App.csproj` 的 `<Version>`（发版流水线会用 `-p:Version=<tag>` 覆盖成 tag 的版本）。所以打 tag 前把 csproj 的 `<Version>` 也改成同一个号，这样本地构建的版本号、Release 的 tag、检查更新的比较三者才对得上。
- 日常提交/PR 只跑 `.github/workflows/ci.yml`（编译 + 签名自测），不发版。

## 推荐的发版步骤

1. 确认 `main` 干净、`dotnet build -c Release` 0 警告、`FileUploader.Verify` ALL PASS；
2. 改 `FileUploader.App.csproj` 的 `<Version>` 成新版本号 → 提交（`chore(release): 版本号改为 x.y.z`）；
3. `git push origin main`；
4. `git tag vX.Y.Z && git push origin vX.Y.Z`；
5. 等 `Release` 工作流跑完，检查 Release 页的资产与「本次变更」清单；
6. 用上一版（便携副本）实测一次「检查更新 → 立即更新并重启」，确认自更新链路没坏（`tools/update-live-test.ps1`）。
