using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DsDock.Anim;
using DsDock.Appearance;
using DsDock.Diagnostics;
using DsDock.Platform;
using DsDock.Storage;

namespace DsDock.Windows;

/// <summary>
/// 侧边栏：应用收起时的常驻形态。屏幕边缘一条半透明窄条（默认 8px 宽、60% 屏高、50% 透明黑），
/// 悬浮变亮 150ms，可拖动吸附到最近边缘，点击展开主界面，不支持右键菜单。
/// 独立顶层窗口：悬浮在桌面之上，不占用屏幕工作区。
/// </summary>
internal sealed class SidebarWindow : Window
{
    private readonly Options _options;
    private readonly SettingsStore _settings;
    private readonly FrameClock _clock;

    private readonly Border _bar;
    private IntPtr _handle;
    private double _dpiScale = 1.0;
    private HwndSource? _source;

    private DockEdge _edge;
    private double _offset;             // 沿边位置 0..1

    private bool _dragging;
    private int _hoverSeq;
    private NativeMethods.RECT _dragStartRect;
    private NativeMethods.POINT _dragStartCursor;

    /// <summary>
    /// 拖动目标位置：**按下时的窗口位置 + 光标总位移**。
    /// 之前用"当前窗口位置 + 总位移"会不断累加 → 窗口跑到光标前面（这就是"不跟手"）。
    /// </summary>
    public static (int X, int Y) DragTarget(NativeMethods.RECT startRect, NativeMethods.POINT startCursor, NativeMethods.POINT cursor)
        => (startRect.Left + (cursor.X - startCursor.X), startRect.Top + (cursor.Y - startCursor.Y));
    private int _followPanelLength;   // 0 = 未同步，使用占屏 60% 的默认长度
    private Point _dragCursor;
    private bool _hover;

    public SidebarWindow(Options options, SettingsStore settings, FrameClock clock)
    {
        _options = options;
        _settings = settings;
        _clock = clock;
        _edge = settings.Edge;
        _offset = Math.Clamp(settings.SidebarOffset, 0, 1);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -4000;
        Top = -4000;

        _bar = new Border
        {
            CornerRadius = Theme.TokenCorner("SidebarCorner", 4),
            Background = new SolidColorBrush(Color.FromArgb(AlphaByte, 0, 0, 0)),
            Cursor = Cursors.Hand,
            ToolTip = "点击展开桌面备忘录",
        };
        Content = _bar;

        MouseLeftButtonUp += OnClick;
        MouseEnter += (_, _) => SetHover(true);
        MouseLeave += (_, _) => SetHover(false);
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseRightButtonUp += (_, e) => e.Handled = true;   // 不支持右键菜单
    }

    /// <summary>点击侧边栏 → 展开主界面（由宿主订阅）。</summary>
    public event Action? ExpandRequested;

    /// <summary>停靠边变化（拖动吸附）时触发，宿主据此重新同步长度。</summary>
    public event Action? EdgeChanged;

    public IntPtr Handle => _handle;
    public DockEdge Edge => _edge;
    public double Offset => _offset;
    public bool IsHover => _hover;
    public long ExStyle => _handle == IntPtr.Zero ? 0 : WindowUtil.GetExStyle(_handle);
    public long StyleFlags => _handle == IntPtr.Zero ? 0 : WindowUtil.GetStyle(_handle);
    public byte CurrentAlpha => (byte)(_bar.Background is SolidColorBrush b ? b.Color.A : 128);

    private byte AlphaByte => (byte)Math.Round(255.0 * _options.SidebarAlpha / 100.0);

    public void Start()
    {
        Show();
        _handle = new WindowInteropHelper(this).Handle;
        _dpiScale = (double)NativeMethods.GetDpiForWindow(_handle) / 96.0;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);

        long ex = WindowUtil.GetExStyle(_handle) | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        WindowUtil.SetExStyle(_handle, ex);
        Log.Info($"侧边栏已显示: handle=0x{_handle.ToInt64():X} dpi={_dpiScale:F2} 样式={WindowUtil.FlagsOf(StyleFlags, ex)}");
        ApplyGeometry(animate: false);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WS_EX_NOACTIVATE 的窗口必须回 MA_NOACTIVATE，否则这次点击会被"激活尝试"吃掉
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }

        if (msg == 0x02E0)   // WM_DPICHANGED
        {
            int newDpi = (int)((wParam.ToInt64() >> 16) & 0xFFFF);
            if (newDpi > 0)
            {
                _dpiScale = newDpi / 96.0;
                Log.Info($"侧边栏 DPI 变化: {newDpi} ({_dpiScale:F2}x) → 重新贴合");
                ApplyGeometry(false);
            }
        }
        return IntPtr.Zero;
    }

    // ---------------------------------------------------------------- geometry

    public NativeMethods.RECT WorkArea()
        => _handle == IntPtr.Zero ? WindowUtil.GetWorkAreaForRect(new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 }) : WindowUtil.GetWorkArea(_handle);

    /// <summary>侧边栏的目标矩形：宽/高按设置，贴到指定边，沿边位置由 offset 决定。</summary>
    public NativeMethods.RECT RectFor(DockEdge edge, double offset)
    {
        var work = WorkArea();
        int thickness = Math.Max(2, (int)Math.Round(_options.SidebarWidth * _dpiScale));
        int axis = edge.IsVertical() ? work.Height : work.Width;
        // 长边下限：屏幕对应方向的 10%（主界面小时侧边栏也不会细成一条缝）
        int minLength = Math.Max(20, (int)Math.Round(axis * 0.10));
        // 长度跟随主界面（未同步时退回默认的 60% 屏长）
        int length = _followPanelLength > 0
            ? Math.Clamp(_followPanelLength, minLength, axis)
            : Math.Max(minLength, (int)Math.Round(axis * 0.6));
        int w = edge.IsVertical() ? thickness : length;
        int h = edge.IsVertical() ? length : thickness;
        int maxOffset = (edge.IsVertical() ? work.Height : work.Width) - length;
        int shift = (int)Math.Round(Math.Clamp(offset, 0, 1) * Math.Max(0, maxOffset));

        return edge switch
        {
            DockEdge.Right => new NativeMethods.RECT { Left = work.Right - w, Top = work.Top + shift, Right = work.Right, Bottom = work.Top + shift + h },
            DockEdge.Left => new NativeMethods.RECT { Left = work.Left, Top = work.Top + shift, Right = work.Left + w, Bottom = work.Top + shift + h },
            DockEdge.Top => new NativeMethods.RECT { Left = work.Left + shift, Top = work.Top, Right = work.Left + shift + w, Bottom = work.Top + h },
            _ => new NativeMethods.RECT { Left = work.Left + shift, Top = work.Bottom - h, Right = work.Left + shift + w, Bottom = work.Bottom },
        };
    }

    public NativeMethods.RECT PanelRect() => WindowUtil.GetScreenRect(_handle);

    /// <summary>侧边栏长度跟随主界面在停靠方向上的长度（像素；0 = 回到占屏 60% 的默认值）。</summary>
    public void FollowPanelLength(int panelLengthPx, bool animate = false)
    {
        _followPanelLength = panelLengthPx;
        ApplyGeometry(animate);
    }

    public void ApplyGeometry(bool animate)
    {
        if (_handle == IntPtr.Zero) return;

        var target = RectFor(_edge, _offset);
        // 尺寸（宽或高）随边缘变化：先改窗口尺寸，再定位
        int dpiW = target.Width;
        int dpiH = target.Height;
        NativeMethods.SetWindowPos(_handle, NativeMethods.HWND_TOPMOST, target.Left, target.Top, dpiW, dpiH,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        Log.Info($"侧边栏几何: 边={_edge} offset={_offset:F2} rect={WindowUtil.GetScreenRect(_handle)}");
    }

    public void SnapTo(DockEdge edge, double offset, bool animate = true)
    {
        _edge = edge;
        _offset = Math.Clamp(offset, 0, 1);
        EdgeChanged?.Invoke();
        ApplyGeometry(animate);
        _settings.SidebarEdge = edge.ToString();
        _settings.SidebarOffset = _offset;
        _settings.SidebarWidth = _options.SidebarWidth;
        _settings.SidebarAlpha = _options.SidebarAlpha;
        _settings.SidebarMonitor = WindowUtil.MonitorId(_handle);
        _settings.Save();
    }

    public void ApplyAppearance()
    {
        _bar.CornerRadius = Theme.TokenCorner("SidebarCorner", 4);
        double target = _hover ? Math.Min(1.0, _options.SidebarAlpha / 100.0 + 0.6) : _options.SidebarAlpha / 100.0;
        double from = _bar.Opacity;
        // 绝不调用 _clock.CancelAll()：那会把主界面的展开/收回动画一起杀掉，动画停在半途
        // 就表现为"主界面少一半"，收回动画被杀则主界面永远不隐藏、残条盖住侧边栏。
        _hoverSeq++;
        int seq = _hoverSeq;
        double ms = Theme.TokenDuration("SidebarHoverMs", 150);
        _clock.Animate(ms, Ease.CubicOut,
            t => { if (seq == _hoverSeq) _bar.Opacity = from + (target - from) * t; },
            () => { if (seq == _hoverSeq) _bar.Opacity = target; });
    }

    private void SetHover(bool hover)
    {
        _hover = hover;
        Log.Info($"侧边栏悬浮: {hover}");
        // 高亮加强：除了更亮，边框也变成强调色（对比明显）
        _bar.BorderBrush = new SolidColorBrush(hover
            ? Theme.Accent
            : Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
        _bar.BorderThickness = new Thickness(hover ? 2 : 1);
        ApplyAppearance();
    }

    /// <summary>自检用：当前不透明度 / 边框色。</summary>
    public double BarOpacityForTest => _bar.Opacity;
    public Color BarBorderColorForTest => (_bar.BorderBrush as SolidColorBrush)?.Color ?? Colors.Transparent;
    public void SetHoverForTest(bool hover) => SetHover(hover);

    /// <summary>自检用：悬停/常态的目标不透明度（不受光标是否恰好停在侧边栏上影响）。</summary>
    public double OpacityTargetForTest(bool hover)
        => hover ? Math.Min(1.0, _options.SidebarAlpha / 100.0 + 0.6) : _options.SidebarAlpha / 100.0;

    /// <summary>
    /// 结束拖动：按最近的屏幕边缘吸附并落盘。MouseUp 与"按键已松开"的兜底路径共用，
    /// 保证松手一定会吸附（侧边栏只有 10px 宽，事件很容易丢）。
    /// </summary>
    public void FinishDrag(string reason)
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
        _dragging = false;

        var rect = PanelRect();
        var work = WindowUtil.GetWorkAreaForRect(rect);
        DockEdge edge = DockEdges.Nearest(rect, work);
        double offset = edge.IsVertical()
            ? (work.Height > rect.Height ? (double)(rect.Top - work.Top) / (work.Height - rect.Height) : 0)
            : (work.Width > rect.Width ? (double)(rect.Left - work.Left) / (work.Width - rect.Width) : 0);
        Log.Info($"侧边栏拖动结束（{reason}）: 当前={rect} 工作区={work} → 吸附边={edge} offset={offset:F2}");
        SnapTo(edge, offset);
        Log.Info($"侧边栏吸附完成: 边={_edge} rect={PanelRect()}");
    }

    /// <summary>自检用：把侧边栏挪到指定位置（模拟"拖到这里"）。</summary>
    public void MoveForTest(int x, int y) => NativeMethods.SetWindowPos(_handle, NativeMethods.HWND_TOPMOST,
        x, y, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

    /// <summary>自检用：执行与"松手"完全相同的吸附逻辑。</summary>
    public void SnapNearestForTest() => FinishDrag("自检");

    // ---------------------------------------------------------------- interaction

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) return;
        Log.Info($"侧边栏被点击 → 请求切换主界面（当前{(IsHover ? "悬浮" : "常态")}）");
        ExpandRequested?.Invoke();
        e.Handled = true;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _dragging = false;
        _dragStartRect = PanelRect();
        NativeMethods.GetCursorPos(out _dragStartCursor);
        _dragCursor = PointToScreen(e.GetPosition(this));

        // 必须捕获鼠标：侧边栏只有 10px 宽，拖动时指针几乎立刻移出窗口，
        // 不捕获就收不到 MouseUp → 吸附逻辑永远不会执行。
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _handle == IntPtr.Zero)
        {
            // 兜底：捕获丢失或 MouseUp 没送到时，也必须在这里完成吸附，
            // 否则"拖到一半松手"会停在半路 —— 用户看到的就是"不自动吸附"。
            if (_dragging) FinishDrag("兜底（未收到 MouseUp）");
            else if (IsMouseCaptured) ReleaseMouseCapture();
            _dragging = false;
            return;
        }

        NativeMethods.GetCursorPos(out var cursor);
        Point local = PointToScreen(e.GetPosition(this));
        double dx = local.X - _dragCursor.X;
        double dy = local.Y - _dragCursor.Y;
        if (!_dragging && Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;
        _dragging = true;

        // 1:1 跟手：目标位置 = 按下时的窗口位置 + 光标总位移
        (int x, int y) = DragTarget(_dragStartRect, _dragStartCursor, cursor);
        NativeMethods.SetWindowPos(_handle, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        FinishDrag("松手");
        e.Handled = true;
    }

    public void SetVisible(bool visible)
    {
        if (_handle == IntPtr.Zero) return;

        if (visible)
        {
            if (!IsVisible) Show();
            NativeMethods.ShowWindow(_handle, NativeMethods.SW_SHOWNORMAL);
        }
        else
        {
            if (IsVisible) Hide();
            NativeMethods.ShowWindow(_handle, 0);
        }

        _settings.SidebarVisible = visible;
    }
}
