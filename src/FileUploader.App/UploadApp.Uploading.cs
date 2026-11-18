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

    /// <summary>把命令行传入的文件读成 StorageFile 后走同一条上传队列。</summary>
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
                items.Add(await StorageFile.GetFileFromPathAsync(path));
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
