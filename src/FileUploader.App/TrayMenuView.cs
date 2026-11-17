using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;

namespace FileUploader.App;

/// <summary>
/// 菜单卡片本体：圆角卡片 + 信息行 + 分隔线 + 可点击项。
/// <para>
/// 每一项都用原生 <see cref="ButtonElement"/>（<c>Button(Element, Action)</c> 重载）承载：
/// 悬停 / 按下的高亮由 WinUI 控件自己的视觉状态渲染，不依赖 Reactor 的指针事件修饰符
/// （后者在这类弹层窗口里不一定触发），顺带白得键盘焦点与无障碍语义。
/// </para>
/// </summary>
internal sealed class TrayMenuView : Component
{
    private readonly IReadOnlyList<TrayMenu.Item> _items;
    private readonly Action<TrayMenu.Item> _pick;

    internal TrayMenuView(IReadOnlyList<TrayMenu.Item> items, Action<TrayMenu.Item> pick)
    {
        _items = items;
        _pick = pick;
    }

    /// <inheritdoc />
    public override Element Render()
    {
        var rows = new List<Element>(_items.Count);

        foreach (var item in _items)
        {
            if (item.IsSeparator)
            {
                rows.Add(
                    Border(null)
                        .Height(1)
                        .Margin(8, 4, 8, 4)
                        .Background(Theme.DividerStroke));
                continue;
            }

            if (item.IsHeader)
            {
                rows.Add(
                    TextBlock(item.Label)
                        .FontSize(11.5)
                        .Foreground(Theme.TertiaryText)
                        .TextTrimming(TextTrimming.CharacterEllipsis)
                        .Padding(12, 0, 12, 0)
                        .Height(TrayMenu.HeaderHeight)
                        .VAlign(VerticalAlignment.Center));
                continue;
            }

            var captured = item;
            rows.Add(
                Button(
                        TextBlock(captured.Label)
                            .FontSize(13.5)
                            .Foreground(captured.IsEnabled ? Theme.PrimaryText : Theme.TertiaryText),
                        () =>
                        {
                            if (captured.IsEnabled)
                            {
                                _pick(captured);
                            }
                        })
                    .SubtleButton()
                    .IsEnabled(captured.IsEnabled)
                    .AutomationName(captured.Label)
                    .HAlign(HorizontalAlignment.Stretch)
                    .Height(TrayMenu.ItemHeight)
                    .MinWidth(0)
                    .MinHeight(0)
                    .Padding(10, 0, 10, 0)
                    // 菜单项是左对齐的文字；Button 默认把内容居中。
                    // 另外关掉系统焦点框：菜单一打开第一项就会拿到焦点，那个黑色矩形很扎眼；
                    // 悬停/按下仍有 SubtleButton 自己的浅灰底，够用。
                    .Set(button =>
                    {
                        button.HorizontalContentAlignment = HorizontalAlignment.Left;
                        button.UseSystemFocusVisuals = false;
                    }));
        }

        return Border(
                Border(VStack(0, rows.ToArray()))
                    .Background(Theme.CardBackground)
                    .CornerRadius(4)
                    .Padding(TrayMenu.CardPadding))
            // 外层只负责 1px 描边：内层卡片留 1px 边距，露出来的就是边框。
            .Background(Theme.CardStroke)
            .CornerRadius(5);
    }
}
