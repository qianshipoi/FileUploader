using System.ComponentModel;
using FileUploader.Core;

namespace FileUploader.App;

/// <summary>
/// 全局唯一的配置状态：主窗口和设置窗口共用同一份数据，
/// 任何一方修改后通过 <see cref="INotifyPropertyChanged"/> 通知另一方重绘。
/// 修改会带 400ms 防抖自动落盘，也可以调用 <see cref="SaveNow"/> 立即保存。
/// </summary>
public sealed class SettingsState : INotifyPropertyChanged
{
    private readonly object _gate = new();
    private AppSettings _value;
    private CancellationTokenSource? _saveCts;

    private SettingsState()
    {
        _value = SettingsStore.Load(out var warning);
        LoadWarning = warning;
    }

    /// <summary>进程内唯一实例。</summary>
    public static SettingsState Current { get; } = new();

    /// <summary>启动时读取配置遇到的问题（如有）。</summary>
    public static string? LoadWarning { get; private set; }

    /// <summary>当前配置快照。</summary>
    public AppSettings Value => _value;

    /// <summary>是否有还没写进磁盘的修改。</summary>
    public bool IsDirty { get; private set; }

    /// <summary>最近一次成功写盘的时间。</summary>
    public DateTimeOffset? LastSavedAt { get; private set; }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>修改配置（不可变记录，传入变换函数）。</summary>
    public void Update(Func<AppSettings, AppSettings> transform)
    {
        var next = transform(_value);
        if (next == _value)
        {
            return;
        }

        _value = next;
        IsDirty = true;
        Raise(nameof(Value));
        Raise(nameof(IsDirty));
        ScheduleSave(next);
    }

    /// <summary>立即保存（「保存」按钮、关窗口等场景）。</summary>
    public void SaveNow()
    {
        lock (_gate)
        {
            _saveCts?.Cancel();
            _saveCts = null;
        }

        TrySave(_value);
    }

    private void ScheduleSave(AppSettings snapshot)
    {
        CancellationToken token;
        lock (_gate)
        {
            _saveCts?.Cancel();
            _saveCts = new CancellationTokenSource();
            token = _saveCts.Token;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(400, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                TrySave(snapshot);
            },
            token);
    }

    private void TrySave(AppSettings snapshot)
    {
        try
        {
            SettingsStore.Save(snapshot);
            IsDirty = false;
            LastSavedAt = DateTimeOffset.Now;
            Raise(nameof(IsDirty));
            Raise(nameof(LastSavedAt));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写盘失败保持 IsDirty = true，界面会继续提示「未保存」。
            Raise(nameof(IsDirty));
        }
    }

    private void Raise(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
