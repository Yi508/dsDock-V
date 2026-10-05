using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DsDock.Appearance;
using DsDock.Controls;
using DsDock.Diagnostics;
using DsDock.Plugins;

namespace DsDock.Windows;

/// <summary>
/// 卡片库：4 列 × 3 行（每页 12 张）分页展示，悬浮放大 1.05（150ms），
/// 每张卡片一个添加/移除按钮；加载失败的卡片显示灰色占位。
/// 置顶、与设置界面互斥（由宿主负责关闭另一个）。
/// </summary>
internal sealed class LibraryWindow : Window
{
    public const int ColumnsPerPage = 4;
    public const int RowsPerPage = 3;
    public const int PageSize = ColumnsPerPage * RowsPerPage;

    private readonly CardRuntime _runtime;
    private readonly Func<string, bool> _add;
    private readonly Func<string, bool> _remove;
    private readonly Func<string, string> _install;
    private readonly Func<string?> _pickFolder;
    private readonly Func<string, string> _uninstall;
    private Border? _overlay;
    private bool _cardListOpen;
    private bool _dialogOpen;   // 文件夹对话框打开期间：失活不自关（否则窗口在导入过程中被关掉 → 崩溃）

    private readonly Grid _items = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _pageText = new();
    private readonly Border _prev;
    private readonly Border _next;

    private int _page;
    private int _pageCount = 1;

    public LibraryWindow(CardRuntime runtime, Func<string, bool> add, Func<string, bool> remove, Func<string, string> install, Func<string?> pickFolder, Func<string, string> uninstall)
    {
        _runtime = runtime;
        _add = add;
        _remove = remove;
        _install = install;
        _pickFolder = pickFolder;
        _uninstall = uninstall;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        // 每格（卡片展示框）横竖各 +25：4 列 × 25 = 宽 +100，3 行 × 25 = 高 +75
        Width = 530;
        Height = 471;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "卡片库";

        for (int i = 0; i < ColumnsPerPage; i++) _items.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < RowsPerPage; i++) _items.RowDefinitions.Add(new RowDefinition());

        // ---- 标题栏
        var header = new Grid { Height = 34 };
        header.Children.Add(new TextBlock
        {
            Text = "卡片库",
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
            Cursor = System.Windows.Input.Cursors.Hand,
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
        header.MouseLeftButtonUp += (s, _) => { _dragging = false; if (s is UIElement el) el.ReleaseMouseCapture(); };

        // ---- 翻页行
        _prev = NavButton("‹");
        _next = NavButton("›");
        _prev.MouseLeftButtonUp += (_, e) => { e.Handled = true; GoToPage(_page - 1); };
        _next.MouseLeftButtonUp += (_, e) => { e.Handled = true; GoToPage(_page + 1); };

        _pageText.FontSize = 10;
        _pageText.Margin = new Thickness(10, 0, 10, 0);
        _pageText.VerticalAlignment = VerticalAlignment.Center;
        _pageText.Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF));

        var nav = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        nav.Children.Add(_prev);
        nav.Children.Add(_pageText);
        nav.Children.Add(_next);

        // 左下角：导入新卡片（选文件夹 → 拷进 Cards\<id>\ → 写 registry.json → 热加载，无需重启）
        var leftButtons = new StackPanel { Orientation = Orientation.Horizontal };
        leftButtons.Children.Add(MakeImportButton());
        leftButtons.Children.Add(MakeUninstallButton());

        var navRow = new Grid { Margin = new Thickness(12, 2, 12, 6) };
        navRow.Children.Add(leftButtons);
        navRow.Children.Add(nav);

        _status.FontSize = 13;
        _status.Margin = new Thickness(12, 4, 12, 4);
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Foreground = new SolidColorBrush(Color.FromArgb(0x77, 0xFF, 0xFF, 0xFF));

        var body = new DockPanel();
        DockPanel.SetDock(header, System.Windows.Controls.Dock.Top);
        DockPanel.SetDock(navRow, System.Windows.Controls.Dock.Bottom);
        DockPanel.SetDock(_status, System.Windows.Controls.Dock.Bottom);
        body.Children.Add(header);
        body.Children.Add(navRow);
        body.Children.Add(_status);
        body.Children.Add(_items);

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
            Child = body,
        };

        Refresh();
    }

    public IntPtr Handle { get; private set; }

    /// <summary>当前页上的条目数（自检用）。</summary>
    public int ItemCount => _items.Children.Count;

    public int Page => _page;
    public int PageCount => _pageCount;
    public int TotalCards => _runtime.Cards.Count;

    /// <summary>主按钮文案：未达上限前一直是"添加"（纯逻辑，便于自检）。</summary>
    public static string PrimaryActionLabel(bool ok, int count, int max)
        => !ok ? "无法添加" : count < max ? "添加" : $"已达上限 {max}";

    private static Border TileButton(string text, bool enabled)
    {
        var button = new Border
        {
        CornerRadius = new CornerRadius(5),
        Padding = new Thickness(8, 3, 8, 3),
        HorizontalAlignment = HorizontalAlignment.Left,
        Background = new SolidColorBrush(enabled
            ? Color.FromArgb(0x44, Theme.Accent.R, Theme.Accent.G, Theme.Accent.B)
            : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
        Cursor = enabled ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)),
        },
        };
        DsHover.Enable(button);
        return button;
    }

    /// <summary>左下角「导入新卡片…」按钮。</summary>
    private Border MakeImportButton()
    {
        var button = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.FromArgb(0x33, Theme.Accent.R, Theme.Accent.G, Theme.Accent.B)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "选择卡片文件夹（内含 manifest.json 与卡片 DLL），自动安装并即时生效",
            Child = new TextBlock
            {
                Text = "导入新卡片…",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(0xEA, 0xFF, 0xFF, 0xFF)),
            },
        };
        DsHover.Enable(button);
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; ImportCard(); };
        return button;
    }

    /// <summary>弹文件夹选择框 → 安装。</summary>
    private void ImportCard()
    {
        // 文件夹选择框由宿主负责弹（现代 IFileOpenDialog，挂在卡片库窗口上）
        _dialogOpen = true;
        string? folder;
        try { folder = _pickFolder(); }
        finally { _dialogOpen = false; }
        if (string.IsNullOrEmpty(folder))
        {
            _status.Text = "已取消导入";
            return;
        }
        ImportCardFrom(folder);
    }

    /// <summary>走"选定文件夹之后"的完整安装路径（自检直接调它，不需要弹框）。</summary>
    public string ImportCardFrom(string folder)
    {
        _dialogOpen = true;   // 安装/刷新期间也不自关
        Log.Info($"导入新卡片: 开始安装 {folder}");
        string detail;
        try
        {
            detail = _install(folder);
        }
        catch (Exception ex)
        {
            detail = $"安装失败（已阻止崩溃）: {ex.GetType().Name} — {ex.Message}";
            Log.Info(detail);
        }

        Log.Info("导入新卡片: 安装返回 → " + detail);
        try
        {
            _page = 0;          // 新卡片可能落在别的页，回到首页便于看到
            Refresh();
            _status.Text = detail;
        }
        catch (Exception ex)
        {
            Log.Info("导入新卡片: 刷新卡片库失败: " + ex);
        }
        _dialogOpen = false;
        return detail;
    }

    /// <summary>「移除卡片…」按钮：弹出已安装卡片列表。</summary>
    private Border MakeUninstallButton()
    {
        var button = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(6, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "列出已安装卡片，可把某张卡片从目录移除并注销",
            Child = new TextBlock
            {
                Text = "移除卡片…",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(0xEA, 0xFF, 0xFF, 0xFF)),
            },
        };
        DsHover.Enable(button);
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; ToggleCardList(); };
        return button;
    }

    private void ToggleCardList()
    {
        _cardListOpen = !_cardListOpen;
        if (_cardListOpen) AddCardListOverlay();
        else Refresh();
    }

    /// <summary>已安装卡片列表浮层：每行右侧一个"移除"。</summary>
    private void AddCardListOverlay()
    {
        if (_overlay != null) { _items.Children.Remove(_overlay); _overlay = null; }

        var panel = new StackPanel { Margin = new Thickness(10) };
        panel.Children.Add(new TextBlock
        {
            Text = "已安装卡片（点右侧移除即删除目录并注销）",
            FontSize = 13,
            Margin = new Thickness(2, 0, 0, 6),
            Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
        });

        if (_runtime.Cards.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "（没有已安装卡片）",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
            });
        }

        foreach (LoadedCard card in _runtime.Cards)
        {
            string id = card.Entry.Id;
            int count = _runtime.CountOf(id);

            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = $"{card.Entry.Manifest?.Name ?? id}（{id}）" + (count > 0 ? $" · 容器 {count} 张" : "") + (card.Ok ? "" : " · 加载失败"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(card.Ok ? Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xAA, 0xAA, 0xAA, 0xAA)),
            };
            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            var remove = TileButton("移除", true);
            remove.Margin = new Thickness(6, 0, 0, 0);
            remove.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _status.Text = _uninstall(id);   // 宿主：容器实例 + 目录 + registry + 运行时
                AddCardListOverlay();            // 重建列表（被移除的项会消失）
            };
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            panel.Children.Add(row);
        }

        var close = TileButton("关闭", true);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 6, 0, 0);
        close.MouseLeftButtonUp += (_, e) => { e.Handled = true; _cardListOpen = false; Refresh(); };
        panel.Children.Add(close);

        _overlay = new Border
        {
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x10, 0x16, 0x22)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel },
        };
        Grid.SetRow(_overlay, 0);
        Grid.SetColumn(_overlay, 0);
        Grid.SetRowSpan(_overlay, RowsPerPage);
        Grid.SetColumnSpan(_overlay, ColumnsPerPage);
        _items.Children.Add(_overlay);
    }

    /// <summary>自检用：走与列表里"移除"按钮完全相同的路径。</summary>
    public string UninstallCardFrom(string id)
    {
        string detail = _uninstall(id);
        _cardListOpen = true;
        AddCardListOverlay();
        _status.Text = detail;
        return detail;
    }

    /// <summary>自检用：卡片列表是否打开。</summary>
    public bool CardListOpen => _cardListOpen;

    /// <summary>分页纯逻辑（便于穷举自检）：总数 + 页码 → 页数 / 该页首索引 / 该页条目数。</summary>
    public static (int PageCount, int FirstIndex, int Count) PaginationForTest(int total, int page = 0)
    {
        int pages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        int current = Math.Clamp(page, 0, pages - 1);
        int first = current * PageSize;
        int count = Math.Max(0, Math.Min(PageSize, total - first));
        return (pages, first, count);
    }

    private void GoToPage(int page)
    {
        int target = Math.Clamp(page, 0, _pageCount - 1);
        if (target == _page) return;
        _page = target;
        Log.Info($"卡片库翻页: 第 {_page + 1}/{_pageCount} 页");
        Refresh();
    }

    /// <summary>重建当前页（添加/移除/翻页后调用）。</summary>
    public void Refresh()
    {
        (int pages, int first, _) = PaginationForTest(_runtime.Cards.Count, _page);
        _pageCount = pages;
        _page = Math.Clamp(_page, 0, _pageCount - 1);

        _items.Children.Clear();
        List<LoadedCard> pageCards = _runtime.Cards.Skip(first).Take(PageSize).ToList();

        for (int index = 0; index < pageCards.Count; index++)
        {
            LoadedCard card = pageCards[index];
            bool ok = card.Ok;
            string id = card.Entry.Id;
            int count = _runtime.CountOf(id);
            int max = _runtime.MaxInstancesOf(id);
            bool canAddMore = ok && count < max;

            var tile = new Border
            {
                Margin = new Thickness(3),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(ok ? (byte)0x22 : (byte)0x14, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(ok ? Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x44, 0x80, 0x80, 0x80)),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1),
            };

            var stack = new StackPanel { Margin = new Thickness(7, 6, 7, 5) };
            stack.Children.Add(new TextBlock
            {
                Text = ok ? card.Entry.Manifest!.Name : id + "（失败）",
                FontSize = Math.Max(11, Theme.FontSize - 2),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(ok ? Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0xAA, 0xAA, 0xAA)),
            });
            stack.Children.Add(new TextBlock
            {
                Text = ok ? card.Entry.Manifest!.Description : (card.Error ?? "未知错误"),
                FontSize = 13,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 40,
                Foreground = new SolidColorBrush(Color.FromArgb(ok ? (byte)0x88 : (byte)0x77, 0xCC, 0xCC, 0xCC)),
            });

            // 主按钮：未达 maxInstances 之前一直是"添加"（可连续添加）；满足后再显示"已达上限"
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
            var addButton = TileButton(PrimaryActionLabel(ok, count, max), canAddMore);
            addButton.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (!canAddMore) return;
                Log.Info($"卡片库添加: {id} count={count} → {(_add(id) ? "已添加" : "失败")}");
                Refresh();
            };
            actions.Children.Add(addButton);

            if (count > 0)
            {
                var minus = TileButton("-", true);
                minus.Margin = new Thickness(4, 0, 0, 0);
                minus.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    Log.Info($"卡片库移除: {id} count={count} → {(_remove(id) ? "已移除" : "失败")}");
                    Refresh();
                };
                actions.Children.Add(minus);
            }
            stack.Children.Add(actions);

            tile.Child = stack;
            AttachHoverScale(tile);
            Grid.SetRow(tile, index / ColumnsPerPage);
            Grid.SetColumn(tile, index % ColumnsPerPage);
            _items.Children.Add(tile);
        }

        _pageText.Text = $"{_page + 1}/{_pageCount}";
        _prev.Opacity = _page > 0 ? 1.0 : 0.35;
        _next.Opacity = _page < _pageCount - 1 ? 1.0 : 0.35;

        string inContainer = string.Join("、", _runtime.Cards
            .Where(c => _runtime.CountOf(c.Entry.Id) > 0)
            .Select(c => $"{c.Entry.Id}×{_runtime.CountOf(c.Entry.Id)}"));
        _status.Text = $"{_runtime.RegistryDetail}；第 {_page + 1}/{_pageCount} 页，本页 {pageCards.Count}/{PageSize} 张，共 {_runtime.Cards.Count} 张" +
                       (inContainer.Length > 0 ? $"；容器内 {inContainer}" : "");

        if (_cardListOpen) AddCardListOverlay();   // 刷新会把浮层清掉，这里重建
    }

    private Border NavButton(string glyph)
    {
        var button = new Border
    {
        Width = 22,
        Height = 20,
        CornerRadius = new CornerRadius(5),
        Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
        Cursor = System.Windows.Input.Cursors.Hand,
        Child = new TextBlock
        {
            Text = glyph,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
        };
        DsHover.Enable(button);
        return button;
    }

    /// <summary>悬浮放大 1.05，150ms（spec）。</summary>
    private static void AttachHoverScale(Border tile)
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        tile.MouseEnter += (_, _) => Animate(tile, 1.05, easing);
        tile.MouseLeave += (_, _) => Animate(tile, 1.0, easing);
    }

    private static void Animate(Border tile, double to, IEasingFunction easing)
    {
        if (tile.RenderTransform is not ScaleTransform scale) return;
        var duration = TimeSpan.FromMilliseconds(150);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, duration) { EasingFunction = easing });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, duration) { EasingFunction = easing });
    }

    public void ShowAt(int screenX, int screenY)
    {
        Show();
        Handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Platform.WindowUtil.SetExStyle(Handle, Platform.WindowUtil.GetExStyle(Handle) | Platform.NativeMethods.WS_EX_TOOLWINDOW);

        var work = Platform.WindowUtil.GetWorkArea(Handle);
        int dpi = (int)Math.Round((double)Platform.NativeMethods.GetDpiForWindow(Handle));
        int w = (int)Math.Ceiling(ActualWidth * dpi / 96.0);
        int h = (int)Math.Ceiling(ActualHeight * dpi / 96.0);
        int x = Math.Clamp(screenX, work.Left, Math.Max(work.Left, work.Right - w));
        int y = screenY;
        if (y + h > work.Bottom && screenY - h - 24 >= work.Top) y = screenY - h - 24;   // 下方放不下→翻到上方
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - h));
        Platform.NativeMethods.SetWindowPos(Handle, Platform.NativeMethods.HWND_TOPMOST, x, y, w, h,
            Platform.NativeMethods.SWP_NOACTIVATE | Platform.NativeMethods.SWP_SHOWWINDOW);
        Log.Info($"卡片库显示: ({x},{y}) {w}x{h}，共 {TotalCards} 张，第 {_page + 1}/{_pageCount} 页");
    }

    private bool _dragging;
    private Point _dragCursor;

    private void OnHeaderDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragCursor = PointToScreen(e.GetPosition(this));
    }

    private void OnHeaderMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging || Handle == IntPtr.Zero) return;
        Point cursor = PointToScreen(e.GetPosition(this));
        var rect = Platform.WindowUtil.GetScreenRect(Handle);
        Platform.NativeMethods.SetWindowPos(Handle, Platform.NativeMethods.HWND_TOPMOST,
            rect.Left + (int)Math.Round(cursor.X - _dragCursor.X),
            rect.Top + (int)Math.Round(cursor.Y - _dragCursor.Y), 0, 0,
            Platform.NativeMethods.SWP_NOSIZE | Platform.NativeMethods.SWP_NOACTIVATE);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (IsVisible) if (!_dialogOpen) Close();   // 导入时文件夹对话框会让本窗口失活，不能自关   // spec：点击卡片库外部自动关闭
    }
}
