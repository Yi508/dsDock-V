using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Shapes;
using DsDock.Anim;
using DsDock.Card.Abstractions;
using DsDock.Plugins;
using DsDock.Appearance;
using DsDock.Controls;
using DsDock.Diagnostics;
using DsDock.Layout;
using DsDock.Platform;
using DsDock.Storage;

namespace DsDock.Windows;

/// <summary>
/// 主界面（容器）：顶部按钮栏 + 2 列 × 1..6 行卡片网格。
/// 完全置顶；从侧边栏所在边"长出来"、收回时缩回侧边栏；点击主界面外部与 Esc 都不收回。
/// </summary>
internal sealed partial class PanelWindow : Window
{
    public const double TopBarHeight = 36;
    public const double CellSize = 160;
    public const double Gap = 8;
    public const double Pad = 12;
    public const int Columns = 2;

    /// <summary>框外右侧"外挂槽"宽度：窗口宽 = 内容宽 + 槽，箭头切换按钮放槽内（恒在圆角框右边框之外）。</summary>
    public const double GutterDip = 28;

    private readonly Options _options;
    private readonly SettingsStore _settings;
    private readonly FrameClock _clock;
    private readonly WindowAnimator _animator;
    private readonly GridModel _grid;

    private readonly List<Placement> _placements = new();
    private readonly List<(Placement Placement, CardView Card)> _cards = new();

    private Border? _frame;
    private Grid? _layers;
    private Grid? _content;
    private Grid? _topBar;
    private Canvas? _canvas;
    private TextBlock? _hud;
    private Border? _columnsButton;
    private StackPanel? _topButtons;
    private UIElement? _backdrop;

    private IntPtr _handle;
    private double _dpiScale = 1.0;
    private HwndSource? _source;

    private int _rows;
    private int _columns;
    private bool _locked;
    private bool _expanded;
    private DockEdge _edge;
    private DsMenu? _menu;

    private bool _dragging;
    private bool _dragMoved;
    private Point _dragCursor;
    private NativeMethods.RECT _dragStart;
    private int _cardSeq;

    public PanelWindow(Options options, SettingsStore settings, FrameClock clock, WindowAnimator animator)
    {
        _options = options;
        _settings = settings;
        _clock = clock;
        _animator = animator;
        _rows = settings.Rows;
        _columns = settings.ContainerColumns is 4 ? 4 : 2;   // 只支持 2/4，其它值按 2 处理
        _locked = settings.Locked;
        _edge = settings.Edge;
        _grid = new GridModel(_columns, LayoutEngine.MaxRows, CellSize, Gap, Pad, visibleRows: _rows);

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

        BuildUi();
    }

    public event Action? SettingsRequested;
    public event Action? LibraryRequested;
    public event Action? CollapseRequested;
    public event Action? ClearedCards;

    /// <summary>主界面右键"退出应用"（托盘不可用时的退出通道）。</summary>
    public event Action? ExitRequested;

    /// <summary>显示设置变化（WM_DISPLAYCHANGE）：显示器拔插 / 分辨率或 DPI 改变。</summary>
    public event Action? DisplayChanged;

    /// <summary>DPI（系统缩放）变化：宿主据此重算侧边栏几何与长度。</summary>
    public event Action? DpiChanged;

    /// <summary>尺寸挡位变化时触发（侧边栏据此跟随长度）。</summary>
    public event Action? RowsChanged;

    public IntPtr Handle => _handle;
    public bool IsExpanded => _expanded;
    public bool Locked => _locked;
    public int Rows => _rows;
    public int ActiveColumns => _columns;

    /// <summary>列数切换（2⇄4）时触发（右下角按钮/HUD 用）。</summary>
    public event Action? ColumnsChanged;

    /// <summary>本应用失去焦点（点击了本应用之外的地方，WM_ACTIVATEAPP=false）。</summary>
    public event Action? AppDeactivated;

    /// <summary>"待展开恢复"的卡片数（收回时排不下、只留布局记录的扩展列卡片）。</summary>
    public int PendingCount
    {
        get
        {
            var hosted = _hosts.Select(h => h.InstanceId).ToHashSet();
            return _placements.Count(p => !hosted.Contains(p.InstanceId));
        }
    }

    private int? _shiftedLeft;   // D1：展开时为避让屏幕右缘整体左移的原左边缘

    /// <summary>收回态下"本位在扩展列"卡片的本位（Q1=B：仅展开态的拖动会改它）。</summary>
    private readonly Dictionary<string, (int Col, int Row)> _homePositions = new();

    private double WidthForColumns(int columns) => columns * CellSize + (columns - 1) * Gap + 2 * Pad + GutterDip;

    /// <summary>自检用：直接切换列数（不带动画）。</summary>
    public void SetColumnsForTest(int columns) => SetColumns(columns, animate: false);

    /// <summary>
    /// 容器 2⇄4 列切换。几何与内容分先后，保证视觉是"裁切生长"而不是闪跳：
    ///   展开：内容先变宽（新区域暂被窗口裁住）→ 窗口动画变宽 → 恢复"待恢复"卡片
    ///   收回：窗口动画先收窄（扩展列被裁掉）→ 内容变窄 → 扩展列卡片按行序搬移
    /// D1：左边缘固定向右生长；溢出工作区则整体左移，收回时移回原位。
    /// </summary>
    public void SetColumns(int target, bool animate)
    {
        if (target is not (2 or 4) || target == _columns) return;

        bool widening = target > _columns;
        bool visible = _handle != IntPtr.Zero && IsVisible;
        NativeMethods.RECT from = PanelRect();
        int durationMs = (int)Theme.TokenDuration("SlideMs", 200);

        if (widening)
        {
            ApplyColumnState(target);
            if (visible && animate)
            {
                AnimateColumnsWidth(from, target, true, durationMs, () => { RestoreHomeCards(); RestorePendingCards(); });
            }
            else
            {
                if (visible) AnimateColumnsWidth(from, target, false, durationMs, null);
                RestoreHomeCards();
                RestorePendingCards();
            }
        }
        else
        {
            void Finish() { ApplyColumnState(target); RelocateExtCards(); }
            if (visible && animate) AnimateColumnsWidth(from, target, true, durationMs, Finish);
            else { if (visible) AnimateColumnsWidth(from, target, false, durationMs, null); Finish(); }
        }

        _settings.ContainerColumns = _columns;
        _settings.Save();
        SaveLayout();
        Log.Info($"容器列数切换为 {_columns}：宽 {ContentWidthDip:F0} DIP，卡片 {_hosts.Count} 张，待恢复 {PendingCount} 张");
        ColumnsChanged?.Invoke();
        UpdateColumnsButton();
    }

    /// <summary>右下角按钮：2⇄4 列切换（锁定时按钮隐藏，这里再兜一层）。</summary>
    private void ToggleColumns()
    {
        if (_locked) return;
        int target = _columns == 2 ? 4 : 2;
        SetColumns(target, animate: true);
        Log.Info($"右下角列切换按钮 → {target} 列");
    }

    private void UpdateColumnsButton()
    {
        if (_columnsButton?.Child is not TextBlock label) return;
        label.Text = _columns >= 4 ? "←" : "→";
        _columnsButton.ToolTip = _columns >= 4 ? "收回两列（容器回到 2 列）" : "展开右侧两列（容器变 4 列）";
        _columnsButton.Visibility = _locked ? Visibility.Collapsed : Visibility.Visible;   // D4：锁定时隐藏
    }

    /// <summary>自检用：顶栏**最右侧**按钮（▦）的屏幕矩形 —— 断言 UI 随列数平移到新右缘（量第一个按钮会多出后面按钮的宽度）。</summary>
    public (int Left, int Top, int Right, int Bottom) TopButtonRectForTest()
    {
        if (_topButtons == null || _topButtons.Children.Count == 0 || _handle == IntPtr.Zero) return default;
        if (_topButtons.Children[_topButtons.Children.Count - 1] is not FrameworkElement button) return default;
        Point tl = button.PointToScreen(new Point(0, 0));
        Point br = button.PointToScreen(new Point(button.ActualWidth, button.ActualHeight));
        return ((int)tl.X, (int)tl.Y, (int)br.X, (int)br.Y);
    }

    /// <summary>自检用：模拟"本应用失焦"（真实来源是 WndProc 的 WM_ACTIVATEAPP）。</summary>
    public void RaiseAppDeactivatedForTest() => AppDeactivated?.Invoke();

    /// <summary>自检用：框外箭头按钮的屏幕矩形 —— 断言它在圆角框右边框之外的槽内。</summary>
    public (int Left, int Top, int Right, int Bottom) ColumnsButtonRectForTest()
    {
        if (_columnsButton == null || _handle == IntPtr.Zero) return default;
        Point tl = _columnsButton.PointToScreen(new Point(0, 0));
        Point br = _columnsButton.PointToScreen(new Point(_columnsButton.ActualWidth, _columnsButton.ActualHeight));
        return ((int)tl.X, (int)tl.Y, (int)br.X, (int)br.Y);
    }

    /// <summary>自检用：右下角切换按钮的文案与可见性。</summary>
    public string ColumnsButtonTextForTest => _columnsButton?.Child is TextBlock tb ? tb.Text : "";

    public bool ColumnsButtonVisibleForTest => _columnsButton != null && _columnsButton.Visibility == Visibility.Visible;

    private void ApplyColumnState(int target)
    {
        _columns = target;
        _grid.SetColumns(target);
        ApplyContentSize();
    }

    private void AnimateColumnsWidth(NativeMethods.RECT from, int targetColumns, bool animate, int durationMs, Action? onDone)
    {
        var work = WorkArea();
        int w = (int)Math.Round(WidthForColumns(targetColumns) * _dpiScale);
        int left = from.Left;

        if (targetColumns > 2)
        {
            if (left + w > work.Right)
            {
                int fitted = Math.Max(work.Left, work.Right - w);
                _shiftedLeft ??= from.Left;      // 记住原位置，收回时平移回来
                left = fitted;
            }
        }
        else if (_shiftedLeft is int original && original >= work.Left && original + w <= work.Right)
        {
            left = original;
            _shiftedLeft = null;
        }

        var to = new NativeMethods.RECT { Left = left, Top = from.Top, Right = left + w, Bottom = from.Bottom };
        if (animate) _animator.Animate(_handle, from, to, durationMs, onDone);
        else SetRect(to);
    }

    /// <summary>收回（4→2）：扩展列卡片按**行序**在 2 列找位；放不下的只留布局记录、摘实例。</summary>
    private void RelocateExtCards()
    {
        var ext = _placements
            .Where(p => p.Col + p.Columns > _columns)
            .OrderBy(p => p.Row).ThenBy(p => p.Col)
            .ToList();

        int placed = 0, deferred = 0;
        foreach (Placement p in ext)
        {
            CardHost? host = _hosts.FirstOrDefault(h => h.InstanceId == p.InstanceId);
            if (host == null) continue;   // 已是"隐藏待恢复"：本位即记录，保持不动

            var slot = LayoutEngine.FindSlot(_placements, p.Columns, p.Rows, _rows, p.InstanceId, _columns);
            if (slot == null)
            {
                deferred++;
                HideHostKeepingPlacement(p.InstanceId);
                continue;
            }

            // 本位语义：layout 记录本位（扩展列），当前位置只是收回态的临时显示
            _homePositions[p.InstanceId] = (p.Col, p.Row);
            _placements[_placements.IndexOf(p)] = p with { Col = slot.Value.Col, Row = slot.Value.Row };
            host.SetCell(slot.Value.Col, slot.Value.Row);
            placed++;
        }

        if (ext.Count > 0)
            Log.Info($"收回搬移：扩展列 {ext.Count} 张 → 挪入 {placed} 张、排不下保留位置 {deferred} 张");
    }

    /// <summary>摘掉宿主但**保留布局记录**（卡片对容器不可见，展开时按记录恢复）。</summary>
    private void HideHostKeepingPlacement(string instanceId)
    {
        CardHost? host = _hosts.FirstOrDefault(h => h.InstanceId == instanceId);
        if (host == null) return;
        host.Card.OnDetached();
        _hosts.Remove(host);
        Runtime.Remove(host.InstanceId);
        _canvas?.Children.Remove(host);
    }

    /// <summary>展开（2→4）：把"本位在扩展列"的卡片搬回本位（本位被占则 FindSlot 退化，落到 2 列即成新本位）。</summary>
    private void RestoreHomeCards()
    {
        if (_homePositions.Count == 0) return;

        int restored = 0;
        foreach (KeyValuePair<string, (int Col, int Row)> pair in _homePositions.ToList())
        {
            CardHost? host = _hosts.FirstOrDefault(h => h.InstanceId == pair.Key);
            Placement? p = _placements.FirstOrDefault(x => x.InstanceId == pair.Key);
            if (host == null || p == null) { _homePositions.Remove(pair.Key); continue; }

            (int Col, int Row) target = pair.Value;
            if (!LayoutEngine.CanPlace(_placements, target.Col, target.Row, p.Columns, p.Rows, _rows, pair.Key, _columns))
            {
                var fallback = LayoutEngine.FindSlot(_placements, p.Columns, p.Rows, _rows, pair.Key, _columns);
                if (fallback != null) target = fallback.Value;
            }

            _placements[_placements.IndexOf(p)] = p with { Col = target.Col, Row = target.Row };
            host.SetCell(target.Col, target.Row);
            _homePositions.Remove(pair.Key);
            restored++;
        }
        if (restored > 0) Log.Info($"本位回迁: {restored} 张回到展开态本位");
    }

    /// <summary>展开（2→4）：把收回时没摆下的记录按原格恢复（原格被占则全网格找位）。</summary>
    private void RestorePendingCards()
    {
        var hosted = _hosts.Select(h => h.InstanceId).ToHashSet();
        var pending = _placements
            .Where(p => !hosted.Contains(p.InstanceId))
            .OrderBy(p => p.Row).ThenBy(p => p.Col)
            .ToList();
        if (pending.Count == 0) return;

        int restored = 0, failed = 0;
        foreach (Placement p in pending)
        {
            var slot = LayoutEngine.CanPlace(_placements, p.Col, p.Row, p.Columns, p.Rows, _rows, p.InstanceId, _columns)
                ? (p.Col, p.Row)
                : LayoutEngine.FindSlot(_placements, p.Columns, p.Rows, _rows, p.InstanceId, _columns);
            if (slot == null) { failed++; continue; }

            Placement updated = p with { Col = slot.Value.Col, Row = slot.Value.Row };
            _placements[_placements.IndexOf(p)] = updated;

            ICard? card = Runtime.Create(CardIdOf(p.InstanceId), p.InstanceId, new CardSize(p.Columns, p.Rows), out string error);
            if (card == null)
            {
                // 卡片类型已卸载/加载失败 → 记录一并删除，避免孤儿
                _placements.Remove(updated);
                failed++;
                Log.Info($"展开恢复失败，删除孤儿记录: {p.InstanceId} — {error}");
                continue;
            }
            AddHost(updated, card);
            restored++;
        }
        Log.Info($"展开恢复：待恢复 {pending.Count} 张 → 恢复 {restored} 张、失败 {failed} 张");
    }
    public int CardCount => _cards.Count;
    public double DpiScale => _dpiScale;
    public long ExStyle => _handle == IntPtr.Zero ? 0 : WindowUtil.GetExStyle(_handle);
    public long StyleFlags => _handle == IntPtr.Zero ? 0 : WindowUtil.GetStyle(_handle);
    public double LastAnimationMs => _animator.LastDurationMs;
    public IReadOnlyList<Placement> Placements => _placements;
    public int RequiredRows => LayoutEngine.RequiredRows(_placements);

    /// <summary>当前屏幕/DPI 允许的最大尺寸挡位（≤2×12，随分辨率自适应）。</summary>
    public int MaxRows
    {
        get
        {
            var work = WorkArea();
            double workDip = _dpiScale > 0 ? work.Height / _dpiScale : work.Height;
            return LayoutEngine.MaxRowsFor(workDip, TopBarHeight, CellSize, Gap, Pad);
        }
    }

    public NativeMethods.RECT PanelRect() => WindowUtil.GetScreenRect(_handle);
    public NativeMethods.RECT WorkArea() => _handle == IntPtr.Zero
        ? WindowUtil.GetWorkAreaForRect(new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 })
        : WindowUtil.GetWorkArea(_handle);

    public double ContentWidthDip => _columns * CellSize + (_columns - 1) * Gap + 2 * Pad;
    public double ContentHeightDip(int rows) => rows * CellSize + Math.Max(0, rows - 1) * Gap + 2 * Pad;
    public double WindowWidthDip => ContentWidthDip + GutterDip;
    public double WindowHeightDip => TopBarHeight + ContentHeightDip(_rows);

    public NativeMethods.RECT SizeForRows(int rows)
    {
        int w = (int)Math.Round(WindowWidthDip * _dpiScale);
        int h = (int)Math.Round((TopBarHeight + ContentHeightDip(rows)) * _dpiScale);
        return new NativeMethods.RECT { Left = 0, Top = 0, Right = w, Bottom = h };
    }

    // ---------------------------------------------------------------- UI

    private void BuildUi()
    {
        _frame = new Border
        {
            CornerRadius = new CornerRadius(Theme.Corner),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)),
        };

        _layers = new Grid();   // 圆角统一由 _frame(边框) + 背景层负责，避免双重裁切造成四角不一致

        _content = new Grid { Background = Brushes.Transparent };
        _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TopBarHeight) });
        _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _topBar = BuildTopBar();
        Grid.SetRow(_topBar, 0);
        _content.Children.Add(_topBar);

        _canvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
        Grid.SetRow(_canvas, 1);
        _content.Children.Add(_canvas);

        _hud = new TextBlock
        {
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(10, 0, 10, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
        };
        Grid.SetRow(_hud, 1);
        _content.Children.Add(_hud);

        // 右下角：容器 2⇄4 列切换。锚定内容右缘 → 内容宽度变化时它跟着移到新的右下角
        _columnsButton = new Border
        {
            Width = 20,
            Height = 26,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 6, 8),
            ToolTip = "展开右侧两列（容器变 4 列）",
            Child = new TextBlock
            {
                FontSize = 16,
                Foreground = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        DsHover.Enable(_columnsButton);
        _columnsButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; ToggleColumns(); };
        UpdateColumnsButton();

        _layers.Children.Add(_content);
        _frame.Child = _layers;

        // 窗口根：圆角框（内容宽，左锚）+ 框外右侧槽内的箭头按钮
        var root = new Grid { Background = Brushes.Transparent };
        root.Children.Add(_frame);
        root.Children.Add(_columnsButton);
        Content = root;

        ApplyContentSize();
    }

    private Grid BuildTopBar()
    {
        var bar = new Grid { Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
        bar.Children.Add(new TextBlock
        {
            Name = "Title",
            Text = "桌面备忘录",
            FontSize = Theme.FontSize,
            Foreground = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(11, 0, 0, 0),
            IsHitTestVisible = false,
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };

        void AddButton(string glyph, string tip, Action action)
        {
            var button = new Border
            {
                Width = 26,
                Height = 22,
                Margin = new Thickness(2, 0, 2, 0),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
                Cursor = Cursors.Hand,
                ToolTip = tip,
                Child = new TextBlock
                {
                    Text = glyph,
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            DsHover.Enable(button);   // 统一悬停高亮
            button.MouseLeftButtonUp += (_, e) => { e.Handled = true; Log.Info($"按钮: {tip}"); action(); };
            buttons.Children.Add(button);
        }

        AddButton("⚙", "设置", () => SettingsRequested?.Invoke());
        AddButton("⌄", "收纳为侧边栏", () => CollapseRequested?.Invoke());
        AddButton("▦", "卡片库", () => LibraryRequested?.Invoke());
        _topButtons = buttons;
        bar.Children.Add(buttons);

        bar.MouseLeftButtonDown += OnTopBarDown;
        bar.MouseMove += OnTopBarMove;
        bar.MouseLeftButtonUp += OnTopBarUp;
        bar.MouseRightButtonUp += OnRightClick;
        return bar;
    }

    private void ApplyContentSize()
    {
        _grid.VisibleRows = _rows;
        double w = WindowWidthDip;
        double h = WindowHeightDip;

        if (_frame != null)
        {
            _frame.Width = ContentWidthDip;   // 框 = 内容宽（槽在框外）
            _frame.Height = h;
            _frame.CornerRadius = new CornerRadius(Theme.Corner);
            // 统一左锚：动画只改窗口矩形、内容不重排；且保证槽恒在框的右侧
            _frame.HorizontalAlignment = HorizontalAlignment.Left;
            _frame.VerticalAlignment = _edge switch
            {
                DockEdge.Top => VerticalAlignment.Bottom,
                DockEdge.Bottom => VerticalAlignment.Top,
                _ => VerticalAlignment.Top,
            };
        }

        if (_layers != null)
            _layers.Clip = Theme.Corner > 0 ? new RectangleGeometry(new Rect(0, 0, ContentWidthDip, h), Theme.Corner, Theme.Corner) : null;

        if (_canvas != null)
        {
            _canvas.Width = ContentWidthDip;
            _canvas.Height = ContentHeightDip(_rows);
        }
    }

    public void Start()
    {
        Show();
        _handle = new WindowInteropHelper(this).Handle;
        _dpiScale = (double)NativeMethods.GetDpiForWindow(_handle) / 96.0;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);

        long ex = WindowUtil.GetExStyle(_handle) | NativeMethods.WS_EX_TOOLWINDOW;
        WindowUtil.SetExStyle(_handle, ex);
        Theme.Changed += OnThemeChanged;

        // 卡片运行时的回调：卡片请求移除自己 / 改尺寸
        Runtime.PopupRequested = OpenCardPopup;
        Runtime.RemoveRequested = instanceId => Dispatcher.BeginInvoke(new Action(() => RemoveCardInstance(instanceId)));
        Runtime.ResizeRequested = (instanceId, size) => Dispatcher.BeginInvoke(new Action(
            () => Log.Info($"卡片 {instanceId} 请求改尺寸为 {size}（尺寸轮换在 M3 的卡片菜单里接）")));

        Log.Info($"主界面已显示: handle=0x{_handle.ToInt64():X} dpi={_dpiScale:F2} rows={_rows} 样式={WindowUtil.FlagsOf(StyleFlags, ex)}");

        ApplyAppearance();
        SetRect(PlaceExpandedRect());
        _expanded = true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == NativeMethods.SC_MINIMIZE)
        {
            Log.Info("拦截最小化请求 SC_MINIMIZE");
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == 0x02E0)   // WM_DPICHANGED
        {
            int newDpi = (int)((wParam.ToInt64() >> 16) & 0xFFFF);
            if (newDpi > 0)
            {
                _dpiScale = newDpi / 96.0;
                var suggested = Marshal.PtrToStructure<NativeMethods.RECT>(lParam);
                Log.Info($"DPI 变化: {newDpi} ({_dpiScale:F2}x) → 重新排版并应用建议矩形 {suggested}");
                ApplyContentSize();
                if (suggested.Width > 0) SetRect(suggested); else SetRect(PlaceExpandedRect());
                DpiChanged?.Invoke();
            }
        }

        // WM_ACTIVATEAPP(0x001C) wParam=0 → 本应用失焦：交宿主决定是否"点击外部自动收纳"
        if (msg == 0x001C && wParam == IntPtr.Zero)
        {
            AppDeactivated?.Invoke();
        }

        if (msg == 0x007E)   // WM_DISPLAYCHANGE
        {
            Log.Info("显示设置变化（WM_DISPLAYCHANGE）");
            DisplayChanged?.Invoke();
        }
        return IntPtr.Zero;
    }

    private void OnThemeChanged()
    {
        ApplyContentSize();
        ApplyAppearance();
    }

    // ---------------------------------------------------------------- appearance

    public void ApplyAppearance()
    {
        if (_layers == null || _frame == null) return;

        if (_backdrop != null)
        {
            _layers.Children.Remove(_backdrop);
            _backdrop = null;
        }

        UIElement backdrop;
        if (_options.Look == "snapshot")
        {
            var made = BackdropFactory.Create(_handle, PanelRect(), ContentWidthDip, WindowHeightDip, _dpiScale,
                Theme.Frost, Theme.PanelTint, Theme.Alpha / 100.0, out string detail);
            backdrop = made ?? new Border
            {
                CornerRadius = new CornerRadius(Theme.Corner),
                Background = new SolidColorBrush(Theme.PanelTint),
                Opacity = Theme.Alpha / 100.0,
            };
            Log.Info("背景(静态壁纸快照，不实时): " + detail);
        }
        else
        {
            // 纯 WPF 逐像素 alpha：窗口颜色带透明度，实时合成窗口后面的真实画面。
            // 刻意不调用任何 DWM 效果（亚克力/模糊会给窗口铺一层不透明底色，把 alpha 完全挡住）。
            // 自带圆角：只靠父级 Clip 裁圆时四角表现不一致（实测只有左上角圆）
            backdrop = new Border
            {
                CornerRadius = new CornerRadius(Theme.Corner),
                Background = new SolidColorBrush(Theme.PanelTint),
                Opacity = Theme.Alpha / 100.0,
            };
            Log.Info($"背景(实时透明): 不透明度 {Theme.Alpha}% → WPF 层 opacity={Theme.Alpha / 100.0:F2}，无 DWM 效果");
        }

        _backdrop = backdrop;
        _layers.Children.Insert(0, backdrop);

        _frame.BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
        if (_topBar?.Children[0] is TextBlock title) title.FontSize = Theme.FontSize;

        // 强调色用于按钮边框与卡片边框
        if (_topBar != null)
        {
            foreach (object child in _topBar.Children)
            {
                if (child is not StackPanel panel) continue;
                foreach (object item in panel.Children)
                    if (item is Border button)
                        button.BorderBrush = Theme.AccentBrush(0x40);
            }
        }

        foreach ((Placement _, CardView card) in _cards)
            card.ApplyTheme();

        NotifyCardsTheme();

        Log.Info($"外观应用: alpha={Theme.Alpha} frost={Theme.Frost} corner={Theme.Corner} font={Theme.FontSize:F0} accent={Theme.ToHex(Theme.Accent)}");
    }

    private void SetRect(NativeMethods.RECT rect) => _animator.Set(_handle, rect);

    // ---------------------------------------------------------------- placement

    /// <summary>
    /// 主界面的"常用位置"：优先用记住的位置（拖到哪儿就回哪儿，夹回工作区）；
    /// 从未记录过时退回"贴边默认位置"。收纳、全屏自动隐藏、显示变化恢复都走这里。
    /// </summary>
    public NativeMethods.RECT HomeRect()
        => _settings.PanelLeft >= 0 && _settings.PanelTop >= 0
            ? PlaceExpandedRect(null, null)   // 记住的位置
            : PlaceExpandedRect(_edge, null); // 贴边默认

    /// <summary>自检用：模拟"用户把主界面拖到这里"。</summary>
    /// <summary>自检用：直接设定 DPI 缩放，验证像素换算（不改系统设置）。</summary>
    public void SetDpiScaleForTest(double scale)
    {
        _dpiScale = scale;
        ApplyContentSize();
    }

    public void SetHomePositionForTest(int x, int y)
    {
        _settings.PanelLeft = x;
        _settings.PanelTop = y;
        _settings.Save();
        Log.Info($"自检设置主界面常用位置: ({x},{y})");
    }

    /// <summary>主界面展开时的矩形：贴侧边栏所在边，沿边位置与侧边栏对齐。</summary>
    public NativeMethods.RECT PlaceExpandedRect(DockEdge? edgeOverride = null, NativeMethods.RECT? sidebarRect = null)
    {
        DockEdge edge = edgeOverride ?? _edge;
        var work = WorkArea();
        int w = (int)Math.Round(WindowWidthDip * _dpiScale);
        int h = (int)Math.Round(WindowHeightDip * _dpiScale);

        int anchorShift = 0;
        if (sidebarRect is { } sb)
        {
            anchorShift = edge.IsVertical()
                ? sb.Top - work.Top
                : sb.Left - work.Left;
        }

        int left, top;
        switch (edge)
        {
            case DockEdge.Right:
                left = work.Right - w;
                top = Math.Clamp(work.Top + anchorShift, work.Top, Math.Max(work.Top, work.Bottom - h));
                break;
            case DockEdge.Left:
                left = work.Left;
                top = Math.Clamp(work.Top + anchorShift, work.Top, Math.Max(work.Top, work.Bottom - h));
                break;
            case DockEdge.Top:
                left = Math.Clamp(work.Left + anchorShift, work.Left, Math.Max(work.Left, work.Right - w));
                top = work.Top;
                break;
            default:
                left = Math.Clamp(work.Left + anchorShift, work.Left, Math.Max(work.Left, work.Right - w));
                top = work.Bottom - h;
                break;
        }

        if (_settings.PanelLeft >= 0 && _settings.PanelTop >= 0 && edgeOverride == null && sidebarRect == null)
        {
            left = Math.Clamp(_settings.PanelLeft, work.Left, Math.Max(work.Left, work.Right - w));
            top = Math.Clamp(_settings.PanelTop, work.Top, Math.Max(work.Top, work.Bottom - h));
        }

        return new NativeMethods.RECT { Left = left, Top = top, Right = left + w, Bottom = top + h };
    }

    /// <summary>从侧边栏所在边长出（animate=false 用于自检的几何断言）。</summary>
    public void Expand(DockEdge edge, NativeMethods.RECT sidebarRect, bool animate = true)
    {
        if (_handle == IntPtr.Zero) return;
        // 无论是否动画都要真正显示：非动画路径以前只 SetRect，
        // 在"隐藏后恢复"（全屏退出）时窗口就不会重新出现。
        SetVisible(true);
        _edge = edge;
        ApplyContentSize();

        // 收纳 / 自动隐藏后要回到用户原来的位置（不再是每次都被拉到贴边位置）
        var target = HomeRect();
        var from = animate ? sidebarRect : target;
        _expanded = true;
        Log.Info($"主界面展开: 边={edge} 从={from} 到={target} 动画={animate}");

        if (animate)
        {
            NativeMethods.SetWindowPos(_handle, NativeMethods.HWND_TOPMOST, from.Left, from.Top, from.Width, from.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            _animator.Animate(_handle, from, target, Theme.TokenDuration("SlideMs", 200));
        }
        else
        {
            SetRect(target);
        }

        _settings.PanelLeft = target.Left;
        _settings.PanelTop = target.Top;
        _settings.Save();
    }

    /// <summary>收回：缩回侧边栏并隐藏（点击外部与 Esc 都不收回，只能由收纳按钮/托盘触发）。</summary>
    public void Collapse(NativeMethods.RECT sidebarRect, bool animate = true)
    {
        if (_handle == IntPtr.Zero) return;
        _expanded = false;
        var from = PanelRect();
        Log.Info($"主界面收回: 从={from} 到={sidebarRect} 动画={animate}");

        if (animate)
        {
            _animator.Animate(_handle, from, sidebarRect, Theme.TokenDuration("SlideMs", 200), () =>
            {
                SetVisible(false);   // WPF 与 Win32 一起隐藏，状态保持一致
                Log.Info("主界面已隐藏（保留在侧边栏）");
            });
        }
        else
        {
            SetRect(sidebarRect);
            NativeMethods.ShowWindow(_handle, 0);
        }
    }

    /// <summary>
    /// 全屏让位时的滑出目标：只在左右边框之间选**最近**的一侧，水平滑出屏幕（垂直位置不变）。
    /// 纯几何，便于穷举自检。
    /// </summary>
    public static NativeMethods.RECT SlideOutTarget(NativeMethods.RECT rect, NativeMethods.RECT work, out bool toLeft)
    {
        int centerX = rect.Left + rect.Width / 2;
        int workCenterX = work.Left + work.Width / 2;
        toLeft = centerX < workCenterX;
        return toLeft
            ? new NativeMethods.RECT { Left = work.Left - rect.Width, Top = rect.Top, Right = work.Left, Bottom = rect.Bottom }
            : new NativeMethods.RECT { Left = work.Right, Top = rect.Top, Right = work.Right + rect.Width, Bottom = rect.Bottom };
    }

    /// <summary>全屏让位：滑出到最近的左右边框（200ms 缓动），到边后隐藏。</summary>
    public bool SlideOutAndHide(bool animate = true, int durationMs = 200)
    {
        if (_handle == IntPtr.Zero) return false;
        var from = PanelRect();
        var target = SlideOutTarget(from, WorkArea(), out bool toLeft);
        Log.Info($"主界面滑出（{(toLeft ? "左" : "右")}边，{durationMs}ms）: {from} → {target}");

        if (!animate)
        {
            SetRect(target);
            SetVisible(false);
            return toLeft;
        }

        _animator.Animate(_handle, from, target, durationMs, () => SetVisible(false));
        return toLeft;
    }

    /// <summary>退出全屏：从当前（屏幕外）位置滑回原来的位置（200ms 缓动）。</summary>
    public void SlideIn(bool animate = true, int durationMs = 200)
    {
        if (_handle == IntPtr.Zero) return;
        SetVisible(true);

        var target = HomeRect();
        var from = PanelRect();
        Log.Info($"主界面滑回（{durationMs}ms）: {from} → {target}");

        if (!animate)
        {
            SetRect(target);
            return;
        }

        _animator.Animate(_handle, from, target, durationMs);
    }

    /// <summary>把窗口重新夹回当前工作区（显示器拔插 / 分辨率变化后调用）。</summary>
    public NativeMethods.RECT EnsureVisible()
    {
        if (_handle == IntPtr.Zero) return default;
        var rect = PanelRect();
        var work = WorkArea();
        int w = rect.Width > 0 ? rect.Width : (int)Math.Round(WindowWidthDip * _dpiScale);
        int h = rect.Height > 0 ? rect.Height : (int)Math.Round(WindowHeightDip * _dpiScale);
        int x = Math.Clamp(rect.Left, work.Left, Math.Max(work.Left, work.Right - w));
        int y = Math.Clamp(rect.Top, work.Top, Math.Max(work.Top, work.Bottom - h));
        var target = new NativeMethods.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        if (x != rect.Left || y != rect.Top) Log.Info($"显示变化后主界面重新贴合: {rect} → {target}");
        SetRect(target);
        _settings.PanelLeft = x;
        _settings.PanelTop = y;
        _settings.Save();
        return target;
    }

    private bool _hudVisible;

    /// <summary>调试信息（FPS/尺寸）显示开关。</summary>
    public void SetHudVisible(bool visible)
    {
        _hudVisible = visible;
        if (_hud != null) _hud.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible && _hud != null) _hud.Text = "";
    }

    public bool HudVisible => _hudVisible;

    public void SetVisible(bool visible)
    {
        if (_handle == IntPtr.Zero) return;

        // 用 WPF 的 Show/Hide 而不是只调 Win32：只调 ShowWindow 会让 WPF 的 IsVisible
        // 与实际窗口状态不一致（后续 Show() 会变成空操作、状态判断也会错）。
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

        _expanded = visible;
    }

    // ---------------------------------------------------------------- rows / cards

    /// <summary>尺寸挡位 2×1..2×6，受当前卡片布局约束（缩不到放不下现有卡片）。</summary>
    public int ApplyRows(int requested)
    {
        int clamped = LayoutEngine.ClampRows(_placements, requested, MaxRows);
        _rows = clamped;
        _settings.Rows = clamped;
        _settings.Save();
        ApplyContentSize();
        RelayoutCards();
        if (_expanded) SetRect(PlaceExpandedRect());
        Log.Info($"尺寸挡位={clamped}（请求 {requested}，下限 {LayoutEngine.RequiredRows(_placements)}，本屏上限 {MaxRows}）" +
                 $" 窗口={WindowWidthDip:F0}x{WindowHeightDip:F0} DIP");
        RowsChanged?.Invoke();
        SaveLayout();
        return clamped;
    }

    public bool TryAddPlaceholderCard()
    {
        int? needed = LayoutEngine.RowsNeededToAdd(_placements, 1, 1, MaxRows);
        if (needed == null)
        {
            Log.Info("空间不足，无法添加（2×6 仍放不下）");
            return false;
        }
        if (needed.Value > _rows) ApplyRows(needed.Value);

        var slot = LayoutEngine.FindSlot(_placements, 1, 1, _rows);
        if (slot == null) return false;

        _cardSeq++;
        var placement = new Placement("card-" + _cardSeq, slot.Value.Col, slot.Value.Row, 1, 1);
        _placements.Add(placement);
        AddCardView(placement);
        Log.Info($"添加占位卡片 {placement.InstanceId} 到 ({placement.Col},{placement.Row})，挡位={_rows}");
        return true;
    }

    private void AddCardView(Placement placement)
    {
        if (_canvas == null) return;
        var card = new CardView(placement.InstanceId, _grid, _clock, _canvas, _cards.Count);
        card.SetCell(placement.Col, placement.Row);
        card.ApplyTheme();
        card.LogMessage += m => Log.Info(m);
        card.PlacementChanged += () => SyncPlacementsFromCards();
        _cards.Add((placement, card));
        _canvas.Children.Add(card);
    }

    private void SyncPlacementsFromCards()
    {
        for (int i = 0; i < _cards.Count; i++)
        {
            var (placement, card) = _cards[i];
            _placements[i] = placement with { Col = card.Cell.Col, Row = card.Cell.Row };
        }
    }

    private void RelayoutCards()
    {
        foreach ((Placement placement, CardView card) in _cards)
        {
            var slot = LayoutEngine.FindSlot(_placements, placement.Columns, placement.Rows, _rows, placement.InstanceId);
            if (slot == null) continue;
            card.SetCell(slot.Value.Col, slot.Value.Row);
            _placements[_placements.FindIndex(p => p.InstanceId == placement.InstanceId)] =
                placement with { Col = slot.Value.Col, Row = slot.Value.Row };
        }
    }

    /// <summary>主界面右键菜单"清除所有卡片"绑定的处理：走真实卡片清空。</summary>
    private void OnMenuClearCards() => ClearAllCards();

    /// <summary>自检用：走与右键菜单完全相同的那条路径。</summary>
    public void ClearCardsFromMenuForTest() => OnMenuClearCards();

    /// <summary>自检用：当前调试信息文本。</summary>
    public string HudText => _hud?.Text ?? "";

    /// <summary>清除所有卡片：清空容器，但卡片数据保留（M1 用内存归档表示）。</summary>
    public void ClearCards()
    {
        foreach ((Placement _, CardView card) in _cards)
            _canvas?.Children.Remove(card);
        Log.Info($"清除所有卡片：容器清空 {_cards.Count} 张，数据保留（归档 {_archive.Count + _cards.Count} 条）");
        _archive.AddRange(_placements);
        _cards.Clear();
        _placements.Clear();
        _homePositions.Clear();
        ClearedCards?.Invoke();
    }

    private readonly List<Placement> _archive = new();
    public int ArchivedCount => _archive.Count;

    public void SetLocked(bool locked)
    {
        _locked = locked;
        _settings.Locked = locked;
        _settings.Save();
        UpdateColumnsButton();   // D4：锁定 = 冻结布局，切换按钮一并隐藏
        Log.Info($"锁定主界面={locked}");
    }

    // ---------------------------------------------------------------- interaction

    private void OnTopBarDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (_locked)
        {
            Log.Info("主界面已锁定，忽略拖动");
            return;
        }
        if (e.OriginalSource is DependencyObject src && IsWithinButton(src)) return;

        _dragging = true;
        _dragMoved = false;
        _dragCursor = PointToScreen(e.GetPosition(this));
        _dragStart = PanelRect();
        _animator.Cancel();
        _topBar?.CaptureMouse();
        e.Handled = true;
    }

    private bool IsWithinButton(DependencyObject node)
    {
        if (_topBar == null) return false;
        foreach (object child in _topBar.Children)
        {
            if (child is not StackPanel buttons) continue;
            DependencyObject? cur = node;
            while (cur != null)
            {
                if (ReferenceEquals(cur, buttons)) return true;
                cur = cur is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(cur) : null;
            }
        }
        return false;
    }

    private void OnTopBarMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        Point cursor = PointToScreen(e.GetPosition(this));
        double dx = cursor.X - _dragCursor.X;
        double dy = cursor.Y - _dragCursor.Y;
        if (Math.Abs(dx) > 2 || Math.Abs(dy) > 2) _dragMoved = true;

        var r = new NativeMethods.RECT
        {
            Left = _dragStart.Left + (int)Math.Round(dx),
            Top = _dragStart.Top + (int)Math.Round(dy),
            Right = _dragStart.Right + (int)Math.Round(dx),
            Bottom = _dragStart.Bottom + (int)Math.Round(dy),
        };
        SetRect(r);
    }

    private void OnTopBarUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _topBar?.ReleaseMouseCapture();
        e.Handled = true;
        if (!_dragMoved) return;

        var rect = PanelRect();
        var work = WorkArea();
        int x = Math.Clamp(rect.Left, work.Left, Math.Max(work.Left, work.Right - rect.Width));
        int y = Math.Clamp(rect.Top, work.Top, Math.Max(work.Top, work.Bottom - rect.Height));
        if (x == rect.Left && y == rect.Top)
        {
            _settings.PanelLeft = rect.Left;
            _settings.PanelTop = rect.Top;
            _settings.Save();
            return;
        }

        Log.Info($"主界面边界回弹: ({rect.Left},{rect.Top}) -> ({x},{y})");
        var target = new NativeMethods.RECT { Left = x, Top = y, Right = x + rect.Width, Bottom = y + rect.Height };
        _animator.Animate(_handle, rect, target, 200);
        _settings.PanelLeft = x;
        _settings.PanelTop = y;
        _settings.Save();
    }

    /// <summary>主界面右键菜单：清除所有卡片 / 锁定（解锁）主界面。</summary>
    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        Point screen = PointToScreen(e.GetPosition(this));
        _menu?.CloseMenu();

        var menu = new DsMenu(Theme.FontSize, Theme.Accent, (int)Theme.TokenDuration("MenuFadeMs", 100));
        menu.AddItem("清除所有卡片", OnMenuClearCards);   // 必须走真实卡片清空（ClearCards 是 M1 遗留的内存清理）
        menu.AddSeparator();
        menu.AddItem(_locked ? "解锁主界面" : "锁定主界面", () => SetLocked(!_locked));
        menu.AddSeparator();
        menu.AddItem("退出应用", () => ExitRequested?.Invoke());
        menu.ShowAt((int)screen.X, (int)screen.Y);
        _menu = menu;
        e.Handled = true;
        Log.Info($"主界面右键菜单: 清除所有卡片 | {(_locked ? "解锁主界面" : "锁定主界面")}");
    }

    public void OpenContextMenuAt(int x, int y)
    {
        _menu?.CloseMenu();
        var menu = new DsMenu(Theme.FontSize, Theme.Accent, (int)Theme.TokenDuration("MenuFadeMs", 100));
        menu.AddItem("清除所有卡片", OnMenuClearCards);   // 必须走真实卡片清空（ClearCards 是 M1 遗留的内存清理）
        menu.AddSeparator();
        menu.AddItem(_locked ? "解锁主界面" : "锁定主界面", () => SetLocked(!_locked));
        menu.AddSeparator();
        menu.AddItem("退出应用", () => ExitRequested?.Invoke());
        menu.ShowAt(x, y);
        _menu = menu;
    }

    public void UpdateHud()
    {
        if (_hud == null || !_hudVisible || _clock.TickCount % 15 != 0) return;
        var rect = PanelRect();
        _hud.Text = $"{(_expanded ? "展开" : "收起")} · 挡位 {_columns}×{_rows} · 卡片 {_hosts.Count} · 锁定 {(_locked ? "是" : "否")}{(PendingCount > 0 ? $" · 待恢复 {PendingCount}" : "")}\n" +
                    $"FPS {_clock.MeasuredTickFps:F1} (渲染 {_clock.MeasuredRenderFps:F1})\n" +
                    $"{rect.Width}x{rect.Height}px · 字号 {Theme.FontSize:F0}";
    }

    /// <summary>卡片在屏幕上的矩形（自检用来断言动画期间没有重排）。</summary>
    public (double WidthDip, double HeightDip)? FirstCardSizeDip()
    {
        if (_cards.Count > 0 && _cards[0].Card.ActualWidth > 0)
            return (_cards[0].Card.ActualWidth, _cards[0].Card.ActualHeight);
        var host = _hosts.FirstOrDefault(h => h.ActualWidth > 0);
        return host == null ? null : (host.ActualWidth, host.ActualHeight);
    }

    public double? CanvasWidthDip => _canvas?.ActualWidth;
}
