using Microsoft.UI.Reactor;
using Windows.ApplicationModel.DataTransfer;

namespace FileUploader.App;

/// <summary>剪贴板写入（统一切到 UI 线程，失败静默）。</summary>
public static class ClipboardHelper
{
    /// <summary>把文本写入剪贴板。</summary>
    public static void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        ReactorApp.UIDispatcher?.TryEnqueue(() =>
        {
            try
            {
                var package = new DataPackage();
                package.SetText(text);
                Clipboard.SetContent(package);
            }
            catch (Exception)
            {
                // 剪贴板被其他程序占用时忽略。
            }
        });
    }
}
