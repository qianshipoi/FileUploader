using FileUploader.Core;
using FileUploader.Core.Storage;
using Windows.Storage;

namespace FileUploader.App;

public sealed partial class UploadApp
{
    private async Task StartupAsync()
    {
        Microsoft.UI.Reactor.ReactorApplication.OnUnhandledException = ex =>
        {
            CrashLog.Write("ReactorApplication.OnUnhandledException", ex);
            return true;
        };

        AppWindows.EnsureTray();
        AppWindows.EnsureDragFlash();

        // 静默启动：主窗口在挂载后才会被 Reactor 显示出来，所以隐藏带几次延迟收敛。
        AppWindows.ApplyStartupVisibility();

        // 开机自启：以注册表里的实际状态为准（exe 换目录会重写；被任务管理器禁用则把设置同步成关闭）。
        switch (StartupRegistration.Sync(SettingsState.Current.Value, out var startupError))
        {
            case StartupSyncResult.Removed:
                SettingsState.Current.Update(s => s with { StartWithWindows = false });
                _setNotice(Notice.Info("开机自启的启动项已经不在了（可能是在任务管理器里禁用或被清理工具删掉），设置已同步为关闭。"));
                break;
            case StartupSyncResult.Failed:
                CrashLog.Write("开机自启注册失败", new InvalidOperationException(startupError));
                _setNotice(Notice.Error($"开机自启没有注册成功：{startupError}"));
                break;
        }

        // 开发辅助：--flash-preview 走一遍「拖拽浮现」气泡的四个状态，方便截图与回归。
        if (Program.FlashPreviewOnStartup)
        {
            await Task.Delay(900);
            DragFlashController.Preview();
        }

        if (Program.OpenSettingsOnStartup)
        {
            AppWindows.OpenSettings();
        }

        // 开发辅助：--menu-preview 启动即弹一次托盘菜单，方便用 UI 自动化回归菜单（不需要鼠标）。
        if (Environment.GetCommandLineArgs().Contains("--menu-preview", StringComparer.OrdinalIgnoreCase))
        {
            await Task.Delay(600);
            AppWindows.ShowTrayMenuForPreview();
        }

        // 开发辅助：--bring-preview 延迟把主窗口叫到前台（和点托盘图标同一条路径），
        // 方便回归「窗口压在别的窗口下面时能不能被激活」（见 tools/tray-activate-test.ps1）。
        // FU_BRING_PREVIEW_MS 可以改延迟，给回归脚本留出摆遮挡物的时间。
        if (Program.BringPreviewOnStartup)
        {
            var delay = int.TryParse(Environment.GetEnvironmentVariable("FU_BRING_PREVIEW_MS"), out var ms) && ms > 0
                ? ms
                : 5000;
            await Task.Delay(delay);
            AppWindows.ShowMainWindowForPreview();
        }

        if (Program.OpenCompactOnStartup)
        {
            AppWindows.EnterCompact();
            await Task.Delay(300);
        }

        if (Program.AutoVerifyPending)
        {
            await VerifyImportedSettingsAsync();
        }

        await UploadStartupFilesAsync();
    }

    /// <summary>
    /// 导入同目录配置后自动测一次连接（含建桶），**测通了才删除导出文件**；
    /// 测不通就把文件留着，用户修好后重新打开可以再导入一次。
    /// </summary>
    private async Task VerifyImportedSettingsAsync()
    {
        var settings = SettingsState.Current.Value;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var client = new ObjectStorageClient(settings);
            var result = await client.EnsureBucketAsync(cts.Token).ConfigureAwait(false);
            if (result.Success)
            {
                SettingsExchange.DeleteImportFile();
                _setNotice(Notice.Info($"已导入配置并连接成功（{settings.Bucket} · {settings.NormalizedEndpoint}），导出文件已删除。"));
            }
            else
            {
                _setNotice(Notice.Error($"已导入配置，但连接测试失败：{result.Describe()}。导出文件保留在 exe 同目录，修好后重新打开会再次导入。"));
            }
        }
        catch (OperationCanceledException)
        {
            _setNotice(Notice.Error("已导入配置，但连接测试超时（20 秒）。导出文件保留在 exe 同目录。"));
        }
        catch (Exception ex)
        {
            _setNotice(Notice.Error($"已导入配置，但连接测试出错：{ex.Message}。导出文件保留在 exe 同目录。"));
        }
    }

    /// <summary>把命令行传入的文件 / 文件夹读成 StorageItem 后走同一条上传队列。</summary>
    private async Task UploadStartupFilesAsync()
    {
        var paths = Program.StartupFiles;
        if (paths.Count == 0)
        {
            return;
        }

        var items = new List<IStorageItem>();
        foreach (var path in paths)
        {
            try
            {
                // 文件夹直接当 StorageFolder 交给队列：展开、打包成 zip 都走和拖拽一样的路径。
                items.Add(Directory.Exists(path)
                    ? await StorageFolder.GetFolderFromPathAsync(path)
                    : await StorageFile.GetFileFromPathAsync(path));
            }
            catch (Exception ex)
            {
                _setNotice(Notice.Error($"无法读取 {path}：{ex.Message}"));
            }
        }

        if (items.Count > 0)
        {
            await UploadQueue.Current.EnqueueAsync(items);
        }
    }
}
