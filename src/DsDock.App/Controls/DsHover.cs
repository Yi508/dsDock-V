using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DsDock.Appearance;

namespace DsDock.Controls;

/// <summary>
/// 统一的按钮悬停高亮：鼠标进入时按钮边框变成强调色，离开恢复，150ms 过渡。
/// 用法（新增按钮只需一行，样式自动一致）：
///     var button = new Border { ... };
///     DsHover.Enable(button);
/// 或 XAML/代码里挂附加属性：DsHover.SetBorderHighlight(button, true);
/// 卡片插件不能引用宿主，卡片内部按钮请在卡片里做同样的两行（见《卡片开发规范》）。
/// </summary>
internal static class DsHover
{
    private const string BaseColorKey = "DsHoverBaseColor";
    private const string BaseThicknessKey = "DsHoverBaseThickness";

    public static readonly DependencyProperty BorderHighlightProperty =
        DependencyProperty.RegisterAttached(
            "BorderHighlight",
            typeof(bool),
            typeof(DsHover),
            new PropertyMetadata(false, OnBorderHighlightChanged));

    public static void SetBorderHighlight(DependencyObject element, bool value)
        => element.SetValue(BorderHighlightProperty, value);

    public static bool GetBorderHighlight(DependencyObject element)
        => (bool)element.GetValue(BorderHighlightProperty);

    private static void OnBorderHighlightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border) return;
        if ((bool)e.NewValue) Enable(border);
    }

    /// <summary>为按钮启用悬停高亮（幂等）。</summary>
    public static void Enable(Border button)
    {
        if (!button.Resources.Contains(BaseColorKey))
        {
            Color baseColor = (button.BorderBrush as SolidColorBrush)?.Color ?? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);
            button.Resources[BaseColorKey] = baseColor;
            button.Resources[BaseThicknessKey] = button.BorderThickness;
            button.BorderBrush = new SolidColorBrush(baseColor);
        }

        button.MouseEnter -= OnEnter;
        button.MouseLeave -= OnLeave;
        button.MouseEnter += OnEnter;
        button.MouseLeave += OnLeave;

        if (!GetBorderHighlight(button)) SetBorderHighlight(button, true);   // 记录"已接入统一样式"
    }

    private static void OnEnter(object sender, MouseEventArgs e) => Highlight((Border)sender, true);

    private static void OnLeave(object sender, MouseEventArgs e) => Highlight((Border)sender, false);

    /// <summary>高亮/恢复。自检直接调用这条路径（不需要合成鼠标事件）。</summary>
    public static void Highlight(Border button, bool on)
    {
        if (button.BorderThickness.Left <= 0)
            button.BorderThickness = new Thickness(1);   // 原本没边框的按钮，高亮时也给它一条

        Color target = on
            ? Theme.Accent
            : button.Resources[BaseColorKey] is Color baseColor ? baseColor : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);

        var brush = new SolidColorBrush((button.BorderBrush as SolidColorBrush)?.Color ?? Colors.Transparent)
        {
            Color = target,   // 即时切换：与菜单悬停一致，也便于自检断言
        };
        button.BorderBrush = brush;
    }
}
