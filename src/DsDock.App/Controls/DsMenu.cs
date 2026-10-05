using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DsDock.Anim;
using DsDock.Diagnostics;
using DsDock.Platform;

namespace DsDock.Controls;

/// <summary>
/// Self drawn context menu (spec: 自绘菜单, 圆角, 阴影, 强调色高亮, 不带图标, 出现时淡入并轻微缩放 100ms,
/// 超出屏幕边缘时自动翻转). It is a borderless topmost window rather than a WPF ContextMenu so the
/// look stays fully under our control and matches the container.
/// </summary>
internal sealed class DsMenu : Window
{
    private readonly FrameClock _clock = new();
    private readonly StackPanel _stack = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly List<Entry> _entries = new();
    private readonly double _fontSize;
    private readonly Color _accent;
    private readonly int _animationMs;

    private HwndSource? _source;
    private bool _closing;
    private NativeMethods.RECT _menuRect;
    private int _outsideTicks;

    private sealed class Entry
    {
        public string Text = "";
        public Action? Action;
        public bool Enabled = true;
        public bool Separator;
    }

    public DsMenu(double fontSize, Color accent, int animationMs = 100)
    {
        _fontSize = fontSize;
        _accent = accent;
        _animationMs = animationMs;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowActivated = true;

        _stack.Margin = new Thickness(5);

        Content = new Border
        {
            Margin = new Thickness(14), // room for the drop shadow
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0xF7, 0x12, 0x18, 0x24)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.55, Color = Colors.Black },
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _scale,
            Child = _stack,
        };
    }

    public IntPtr Handle { get; private set; }
    public bool IsOpen => !_closing && IsVisible;
    public double LastAnimationMs { get; private set; }
    public int MenuItemCount => _entries.FindAll(e => !e.Separator).Count;
    public IReadOnlyList<string> ItemLabels
    {
        get
        {
            var labels = new List<string>();
            foreach (Entry e in _entries)
                if (!e.Separator) labels.Add(e.Text);
            return labels;
        }
    }

    public void AddItem(string text, Action? action, bool enabled = true)
        => _entries.Add(new Entry { Text = text, Action = action, Enabled = enabled });

    public void AddSeparator() => _entries.Add(new Entry { Separator = true });

    /// <summary>Shows the menu at a screen point, flipping it to stay inside the work area.</summary>
    public void ShowAt(int screenX, int screenY)
    {
        BuildItems();

        Opacity = 0;
        _scale.ScaleX = 0.96;
        _scale.ScaleY = 0.96;
        Show();                       // creates the HWND and measures the content

        Handle = new WindowInteropHelper(this).Handle;
        long ex = WindowUtil.GetExStyle(Handle) | NativeMethods.WS_EX_TOOLWINDOW;
        WindowUtil.SetExStyle(Handle, ex);
        _source = HwndSource.FromHwnd(Handle);
        _source?.AddHook(WndProc);

        int scaleDpi = (int)Math.Round((double)NativeMethods.GetDpiForWindow(Handle));
        var work = WindowUtil.GetWorkArea(Handle);
        int w = (int)Math.Ceiling(ActualWidth * scaleDpi / 96.0);
        int h = (int)Math.Ceiling(ActualHeight * scaleDpi / 96.0);

        int x = screenX;
        int y = screenY;
        bool flippedX = false;
        bool flippedY = false;
        if (x + w > work.Right) { x = Math.Max(work.Left, screenX - w); flippedX = true; }
        if (y + h > work.Bottom) { y = Math.Max(work.Top, screenY - h); flippedY = true; }
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - h));

        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        SetForegroundWindowSafe();

        _menuRect = new NativeMethods.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        Log.Info($"菜单显示: 请求({screenX},{screenY}) 实际({x},{y}) {w}x{h} 水平翻转={flippedX} 垂直翻转={flippedY} 工作区{work}");

        // fade in + slight scale (spec: 100ms)
        _clock.Stop();
        _clock.Start();
        _clock.Animate(_animationMs, Ease.CubicOut, t =>
        {
            Opacity = t;
            double s = 0.96 + 0.04 * t;
            _scale.ScaleX = s;
            _scale.ScaleY = s;
        });
        LastAnimationMs = _animationMs;
    }

    private void SetForegroundWindowSafe()
    {
        // needed so that clicking somewhere else dismisses the menu
        uint target = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        uint self = NativeMethods.GetCurrentThreadId();
        bool attached = target != self && NativeMethods.AttachThreadInput(self, target, true);
        NativeMethods.SetForegroundWindow(Handle);
        if (attached) NativeMethods.AttachThreadInput(self, target, false);
    }

    public void MoveToForTest(int screenX, int screenY)
    {
        if (Handle == IntPtr.Zero) return;
        var work = WindowUtil.GetWorkArea(Handle);
        int w = _menuRect.Width;
        int h = _menuRect.Height;
        int x = screenX, y = screenY;
        if (x + w > work.Right) x = Math.Max(work.Left, screenX - w);
        if (y + h > work.Bottom) y = Math.Max(work.Top, screenY - h);
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - h));
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        _menuRect = new NativeMethods.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }

    public NativeMethods.RECT MenuRect() => _menuRect;

    /// <summary>Pure geometry part of "click outside closes", so it can be asserted in a self test.</summary>
    public bool WouldCloseForCursor(int x, int y)
        => x < _menuRect.Left - 2 || x > _menuRect.Right + 2 || y < _menuRect.Top - 2 || y > _menuRect.Bottom + 2;

    public void InvokeForTest(string label)
    {
        Entry? entry = _entries.Find(e => !e.Separator && e.Text == label);
        if (entry?.Action == null)
        {
            Log.Info($"菜单项 {label} 不存在或没有动作");
            return;
        }
        Log.Info($"自检触发菜单项: {label}");
        Action action = entry.Action;
        CloseMenu();
        action();
    }

    public void CloseMenu()
    {
        if (_closing) return;
        _closing = true;
        Log.Info("菜单关闭");
        try { _clock.Stop(); } catch { /* ignore */ }
        try { _source?.RemoveHook(WndProc); } catch { /* ignore */ }
        _source = null;
        try { Close(); } catch { /* ignore */ }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_KEYDOWN && wParam.ToInt32() == NativeMethods.VK_ESCAPE)
        {
            Log.Info("菜单收到 Esc → 关闭");
            CloseMenu();
            handled = true;
        }
        return IntPtr.Zero;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        // a borderless topmost popup has no other reliable "clicked elsewhere" signal
        if (IsOpen) CloseMenu();
    }

    private void OnMenuTick()
    {
        if (!IsOpen) return;

        // fallback for the case where the menu never got foreground rights: a left click clearly
        // outside the menu means the user clicked away
        bool leftDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
        if (!leftDown || !NativeMethods.GetCursorPos(out var pt))
        {
            _outsideTicks = 0;
            return;
        }

        if (!WouldCloseForCursor(pt.X, pt.Y))
        {
            _outsideTicks = 0;
            return;
        }

        if (++_outsideTicks >= 3)
        {
            Log.Info($"检测到菜单外部左键按下 ({pt.X},{pt.Y}) → 关闭");
            CloseMenu();
        }
    }

    private void BuildItems()
    {
        _stack.Children.Clear();
        foreach (Entry entry in _entries)
        {
            if (entry.Separator)
            {
                _stack.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(8, 4, 8, 4),
                    Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                });
                continue;
            }

            var text = new TextBlock
            {
                Text = entry.Text,
                FontSize = _fontSize,
                Foreground = new SolidColorBrush(entry.Enabled
                    ? Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)
                    : Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 14, 0),
            };

            var row = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Padding = new Thickness(0, 7, 0, 7),
                MinWidth = 140,
                Child = text,
                Cursor = entry.Enabled ? Cursors.Hand : Cursors.Arrow,
                IsHitTestVisible = entry.Enabled,
            };

            if (entry.Enabled)
            {
                var hover = new SolidColorBrush(Color.FromArgb(0x45, _accent.R, _accent.G, _accent.B));
                row.MouseEnter += (_, _) =>
                {
                    row.Background = hover;
                    text.Foreground = Brushes.White;
                };
                row.MouseLeave += (_, _) =>
                {
                    row.Background = Brushes.Transparent;
                    text.Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
                };
                row.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    Log.Info($"菜单项点击: {entry.Text}");
                    Action? action = entry.Action;
                    CloseMenu();
                    action?.Invoke();
                };
            }

            _stack.Children.Add(row);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _clock.OnTick += OnMenuTick;
    }
}
