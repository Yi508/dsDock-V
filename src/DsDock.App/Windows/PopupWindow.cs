using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DsDock.Anim;
using DsDock.Appearance;
using DsDock.Card.Abstractions;
using DsDock.Diagnostics;
using DsDock.Platform;

namespace DsDock.Windows;

/// <summary>
/// 宿主弹出窗口：置顶、不显示在任务栏、可拖动、点击外部或 Esc 关闭、淡入 + 上浮 150ms。
/// 卡片通过 ICardContext.ShowPopup 把自绘内容放进来 —— 窗口装饰归宿主，内容归卡片。
/// </summary>
internal sealed class PopupWindow : Window, IEditorPopup
{
    private readonly FrameClock? _clock;
    private IntPtr _handle;
    private bool _dragging;
    private Point _dragCursor;
    private int _targetTop;

    public PopupWindow(string title, UIElement content, FrameClock? clock)
    {
        _clock = clock;
        Title = title;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowActivated = true;
        Opacity = 0;

        var header = new Grid { Height = 32, Cursor = Cursors.SizeAll };
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = Theme.FontSize,
            Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            IsHitTestVisible = false,
        });
        var close = new Border
        {
            Width = 24, Height = 22,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = "✕", FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        close.MouseLeftButtonDown += (_, e) => { e.Handled = true; Close(); };
        header.Children.Add(close);
        header.MouseLeftButtonDown += OnHeaderDown;
        header.MouseMove += OnHeaderMove;
        header.MouseLeftButtonUp += (_, _) => _dragging = false;

        var dock = new DockPanel();
        DockPanel.SetDock(header, System.Windows.Controls.Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(content);

        Content = new Border
        {
            Margin = new Thickness(14),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0xF7, 0x12, 0x18, 0x24)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 16, ShadowDepth = 3, Opacity = 0.55, Color = Colors.Black,
            },
            Child = dock,
        };

        // Esc 关闭（点外关闭见 OnDeactivated）
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    public bool IsOpen => IsVisible;

    public void ShowNear(int x, int y)
    {
        Show();
        _handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        WindowUtil.SetExStyle(_handle, WindowUtil.GetExStyle(_handle) | NativeMethods.WS_EX_TOOLWINDOW);

        var work = WindowUtil.GetWorkArea(_handle);
        int dpi = (int)Math.Round((double)NativeMethods.GetDpiForWindow(_handle));
        int w = (int)Math.Ceiling(ActualWidth * dpi / 96.0);
        int h = (int)Math.Ceiling(ActualHeight * dpi / 96.0);
        int px = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
        int py = y;
        if (py + h > work.Bottom && y - h - 24 >= work.Top) py = y - h - 24;   // 下方放不下→翻到上方
        py = Math.Clamp(py, work.Top, Math.Max(work.Top, work.Bottom - h));
        _targetTop = py;

        // 必须激活窗口：NOACTIVATE 会让它"只能看不能输入"
        NativeMethods.SetWindowPos(_handle, NativeMethods.HWND_TOPMOST, px, py + 14, w, h,
            NativeMethods.SWP_SHOWWINDOW);
        NativeMethods.SetForegroundWindow(_handle);
        Activate();

        // 淡入 + 上浮 150ms（spec）
        BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(150)));
        double from = py + 14;
        if (_clock != null)
        {
            _clock.Animate(150, Ease.CubicOut, t => Top = from + (_targetTop - from) * t, () => Top = _targetTop);
        }
        else
        {
            Top = _targetTop;   // 没有共享时钟时只做淡入
        }
        Log.Info($"弹出窗口显示: {Title} @({px},{py}) {w}x{h}");
    }

    private void OnHeaderDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragCursor = PointToScreen(e.GetPosition(this));
    }

    private void OnHeaderMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _handle == IntPtr.Zero) return;
        Point cursor = PointToScreen(e.GetPosition(this));
        var rect = WindowUtil.GetScreenRect(_handle);
        NativeMethods.SetWindowPos(_handle, NativeMethods.HWND_TOPMOST,
            rect.Left + (int)Math.Round(cursor.X - _dragCursor.X),
            rect.Top + (int)Math.Round(cursor.Y - _dragCursor.Y), 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (IsVisible) Close();   // spec：点击弹窗外部自动关闭
    }
}
