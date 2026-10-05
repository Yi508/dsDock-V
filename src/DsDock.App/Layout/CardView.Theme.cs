using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DsDock.Appearance;
using DsDock.Diagnostics;

namespace DsDock.Layout;

/// <summary>占位卡片：主题相关的绘制（边框/文字/强调色）单独放这里，卡片主体在 CardView.cs。</summary>
internal sealed partial class CardView
{
    private TextBlock? _titleText;
    private TextBlock? _subtitleText;

    public void ApplyTheme()
    {
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        Background = Theme.AccentBrush(0x1F);

        if (Child is StackPanel stack)
        {
            foreach (object child in stack.Children)
            {
                if (child is not TextBlock text) continue;
                text.FontSize = text.Text.StartsWith("占位") ? Theme.FontSize : Math.Max(9, Theme.FontSize - 4);
                text.Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
                if (text.Text.StartsWith("格")) _subtitleText = text;
                else _titleText = text;
            }
        }
    }

    public string ThemeDebug => $"card={Id} size={ActualWidth:F0}x{ActualHeight:F0} accent={Theme.ToHex(Theme.Accent)} font={Theme.FontSize:F0}";
}
