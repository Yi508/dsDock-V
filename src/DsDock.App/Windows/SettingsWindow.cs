using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DsDock.Appearance;
using DsDock.Controls;
using DsDock.Diagnostics;
using DsDock.Layout;
using System.IO;
using DsDock.Shell;
using DsDock.Storage;

namespace DsDock.Windows;

/// <summary>
/// 设置界面（M1 范围）：不透明度 / 磨砂 / 圆角 / 尺寸挡位 / 字号 / 强调色（5 预设 + 简易调色板）/
/// 恢复默认外观 / 关闭应用。置顶、可拖动、点击外部关闭、Esc 不关闭、记住位置。
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly PanelWindow _panel;
    // M2 会把"重置所有数据"的二次确认接到自绘弹窗

    private TextBlock? _rowsValue;
    private Grid? _bodyHost;
    private bool _dialogOpen;
    private readonly Action<bool>? _fullscreenHideChanged;
    private Action? _toggleFullscreenHide;
    private Action? _toggleStartup;
    private Action? _toggleHud;   // 文件对话框打开期间，不能因为"失去激活"而关掉设置窗口
    private readonly TextBlock _status = new()
    {
        FontSize = 13,
        Margin = new Thickness(2, 8, 2, 0),
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
    };
    private DsSlider? _rowsSlider;

    public SettingsWindow(PanelWindow panel, Action<bool>? fullscreenHideChanged = null)
    {
        _panel = panel;
        _fullscreenHideChanged = fullscreenHideChanged;


        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 306;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowActivated = true;
        Title = "设置";

        var body = new StackPanel { Margin = new Thickness(12, 4, 12, 12) };
        AddSlider(body, "主界面不透明度", 30, 100, (int)Theme.Alpha, v => Theme.Apply(Theme.Accent, Theme.FontSize, Theme.Corner, v, Theme.Frost));
        AddSlider(body, "圆角", 0, 24, Theme.Corner, v => Theme.Apply(Theme.Accent, Theme.FontSize, v, Theme.Alpha, Theme.Frost));
        AddSlider(body, "字号", 12, 20, (int)Theme.FontSize, v => Theme.Apply(Theme.Accent, v, Theme.Corner, Theme.Alpha, Theme.Frost));
        AddRowsSlider(body);

        body.Children.Add(Divider());
        body.Children.Add(new TextBlock
        {
            Text = "强调色",
            FontSize = 11,
            Margin = new Thickness(2, 2, 0, 4),
            Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
        });
        body.Children.Add(BuildPresets());
        body.Children.Add(BuildPalette());

        body.Children.Add(Divider());
        body.Children.Add(MakeButton("恢复默认外观", RestoreDefaults));
        _status.Text = string.Empty;   // 初始留空，只在有操作结果时显示提示
        body.Children.Add(Divider());
        body.Children.Add(MakeButton("导出", ExportData));
        body.Children.Add(MakeButton("导入", ImportData));
        body.Children.Add(MakeButton("重置所有数据…", ShowResetConfirm));
        body.Children.Add(BuildSwitchRow("全屏时自动隐藏",
            () => SettingsStore.Load().FullscreenHide, ApplyFullscreenHide, out Action toggleFullscreen));
        body.Children.Add(BuildSwitchRow("显示调试信息（FPS/尺寸）",
            () => SettingsStore.Load().ShowHud, ApplyHud, out Action toggleHud));
        _toggleHud = () => toggleHud();
        body.Children.Add(BuildSwitchRow("开机自启动",
            StartupManager.IsEnabled, ApplyStartup, out Action toggleStartup));
        _toggleFullscreenHide = () => toggleFullscreen();
        _toggleStartup = () => toggleStartup();
        body.Children.Add(_status);
        body.Children.Add(Divider());
        body.Children.Add(MakeButton("关闭应用（直接退出）", () => Application.Current.Shutdown(), primary: true));



        _bodyHost = new Grid();
        _bodyHost.Children.Add(body);
        Content = Shell(_bodyHost, "设置");
    }

    private UIElement Shell(UIElement body, string title)
    {
        var header = new Grid { Height = 34 };
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
            Width = 24,
            Height = 22,
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
        header.MouseLeftButtonUp += (_, _) => { if (_dragging) { _dragging = false; header.ReleaseMouseCapture(); } };
        header.Cursor = System.Windows.Input.Cursors.SizeAll;

        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(body);

        return new Border
        {
            Margin = new Thickness(14),
            CornerRadius = Theme.TokenCorner("MenuCorner", 10),
            Background = new SolidColorBrush(Color.FromArgb(0xF7, 0x12, 0x18, 0x24)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 16, ShadowDepth = 3, Opacity = 0.55, Color = Colors.Black,
            },
            Child = dock,
        };
    }

    private static Border Divider() => new()
    {
        Height = 1,
        Margin = new Thickness(0, 10, 0, 6),
        Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
    };

    private void AddSlider(Panel host, string name, int min, int max, int value, Action<int> apply)
    {
        var label = new TextBlock
        {
            FontSize = 11,
            Text = $"{name}  {value}",
            Margin = new Thickness(2, 8, 0, 2),
            Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
        };
        var slider = new DsSlider { Minimum = min, Maximum = max, Value = value, Margin = new Thickness(0, 2, 0, 0) };
        slider.ValueChanged += v =>
        {
            label.Text = $"{name}  {v}";
            apply(v);
        };
        host.Children.Add(label);
        host.Children.Add(slider);
    }

    /// <summary>尺寸挡位：下限受当前卡片布局约束（spec：不能缩小到放不下现有卡片）。</summary>
    private void AddRowsSlider(Panel host)
    {
        int min = Math.Max(LayoutEngine.MinRows, _panel.RequiredRows);
        _rowsValue = new TextBlock
        {
            FontSize = 11,
            Text = $"尺寸挡位  2×{_panel.Rows}（下限 {min}，本屏上限 {_panel.MaxRows}）",
            Margin = new Thickness(2, 8, 0, 2),
            Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
        };
        _rowsSlider = new DsSlider
        {
            Minimum = min,
            Maximum = Math.Max(min, _panel.MaxRows),   // 分辨率自适应上限（≤2×12）
            Value = _panel.Rows,
            Margin = new Thickness(0, 2, 0, 0),
        };
        _rowsSlider.ValueChanged += v =>
        {
            int applied = _panel.ApplyRows(v);
            _rowsValue.Text = $"尺寸挡位  2×{applied}（下限 {Math.Max(LayoutEngine.MinRows, _panel.RequiredRows)}，本屏上限 {_panel.MaxRows}）";
        };
        host.Children.Add(_rowsValue);
        host.Children.Add(_rowsSlider);
    }

    private UIElement BuildPresets()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach ((string name, string hex) in Theme.Presets)
        {
            panel.Children.Add(Swatch(hex, name));
        }
        return panel;
    }

    /// <summary>简易调色板（spec 要的是"可点击才弹出的调色板"，完整取色器留 M6）。</summary>
    private UIElement BuildPalette()
    {
        string[] hexes = { "#EF4444", "#F97316", "#FACC15", "#22C55E", "#14B8A6", "#06B6D4", "#3B82F6", "#6366F1", "#A855F7", "#EC4899", "#94A3B8", "#E5E7EB" };
        var wrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (string hex in hexes) wrap.Children.Add(Swatch(hex, hex));
        return wrap;
    }

    private Border Swatch(string hex, string tip)
    {
        Theme.TryParseHex(hex, out Color color);
        var swatch = new Border
        {
            Width = 22,
            Height = 22,
            Margin = new Thickness(0, 0, 6, 6),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(color),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = tip,
        };
        swatch.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Theme.ApplyAccent(hex);
            Log.Info($"强调色切换: {hex}");
        };
        return swatch;
    }

    private Border MakeButton(string text, Action action, bool primary = false)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var button = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = primary ? Theme.AccentBrush(0x55) : new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            BorderBrush = Theme.AccentBrush(0x40),
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 6, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = label,
        };
        DsHover.Enable(button);   // 统一悬停高亮（新增按钮只要这一行）
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; action(); };
        return button;
    }

    /// <summary>导出：用户选择保存位置（默认文件名带时间戳，默认 .dsdock.json）。</summary>
    /// <summary>
    /// 自绘开关按钮：轨道 + 旋钮，点击即切换，旋钮位移与轨道颜色都有 150ms 反馈动画。
    /// 切换后立刻写盘并通知宿主（即时生效，不需要重启）。
    /// </summary>
    private FrameworkElement BuildSwitchRow(string title, Func<bool> get, Action<bool> set, out Action toggle)
    {
        bool initial = get();
        const double trackWidth = 36, trackHeight = 20, knobSize = 14, gap = 3;

        var row = new Grid { Margin = new Thickness(2, 8, 2, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = title,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
        };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        Color onColor = Theme.Accent;
        Color offColor = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);

        var track = new Border
        {
            Width = trackWidth,
            Height = trackHeight,
            CornerRadius = new CornerRadius(trackHeight / 2),
            Background = new SolidColorBrush(initial ? onColor : offColor),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "点击切换（立即生效）",
        };

        var knob = new Border
        {
            Width = knobSize,
            Height = knobSize,
            CornerRadius = new CornerRadius(knobSize / 2),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
        };
        var canvas = new Canvas { Width = trackWidth, Height = trackHeight };
        Canvas.SetLeft(knob, initial ? trackWidth - knobSize - gap : gap);
        Canvas.SetTop(knob, (trackHeight - knobSize) / 2);
        canvas.Children.Add(knob);
        track.Child = canvas;
        DsHover.Enable(track);   // 开关按钮也吃悬停高亮
        Grid.SetColumn(track, 1);
        row.Children.Add(track);

        void Animate(bool on)
        {
            var move = new DoubleAnimation(Canvas.GetLeft(knob), on ? trackWidth - knobSize - gap : gap,
                TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            knob.BeginAnimation(Canvas.LeftProperty, move);

            var brush = new SolidColorBrush(((SolidColorBrush)track.Background).Color);
            track.Background = brush;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(on ? onColor : offColor,
                TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        bool current = initial;
        Action localToggle = () =>
        {
            current = !current;
            Animate(current);
            set(current);   // 具体的"落盘 + 通知"由调用方给
        };
        toggle = localToggle;   // out 参数不能直接被 lambda 捕获
        track.MouseLeftButtonUp += (_, e) => { e.Handled = true; localToggle(); };

        return row;
    }

    /// <summary>自检用：走与点击完全相同的代码路径。</summary>
    public void ToggleFullscreenHideForTest() => _toggleFullscreenHide?.Invoke();

    /// <summary>调试信息开关：立即生效。</summary>
    private void ApplyHud(bool enabled)
    {
        SettingsStore settings = SettingsStore.Load();
        settings.ShowHud = enabled;
        settings.Save();
        _panel.SetHudVisible(enabled);
        _status.Text = $"显示调试信息 = {enabled}（已即时生效）";
        Log.Info(_status.Text);
    }

    /// <summary>全屏开关：落盘 + 立即通知宿主（即时生效）。</summary>
    private void ApplyFullscreenHide(bool enabled)
    {
        SettingsStore settings = SettingsStore.Load();
        settings.FullscreenHide = enabled;
        settings.Save();
        _status.Text = $"全屏时自动隐藏 = {enabled}（已即时生效，无需重启）";
        _fullscreenHideChanged?.Invoke(enabled);
        Log.Info(_status.Text);
    }

    /// <summary>开机自启开关：写/删 HKCU Run 键，立即生效。</summary>
    private void ApplyStartup(bool enabled)
    {
        bool changed = enabled ? StartupManager.Enable() : StartupManager.Disable();
        _status.Text = $"开机自启动 = {StartupManager.IsEnabled()}（{(changed ? "已生效" : "操作失败，见日志")}）";
        Log.Info(_status.Text);
    }

    /// <summary>自检用：走与点击完全相同的路径。</summary>
    public void ToggleStartupForTest() => _toggleStartup?.Invoke();

    private void ExportData()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出桌面备忘录数据",
            Filter = ImportExport.PackageFilter,
            DefaultExt = ".dsdock.json",
            FileName = $"dsDock-{DateTime.Now:yyyyMMdd-HHmmss}.dsdock.json",
            InitialDirectory = Directory.Exists(ImportExport.ExportDir) ? ImportExport.ExportDir : DataRoot.Root,
            AddExtension = true,
            OverwritePrompt = true,
        };

        _dialogOpen = true;
        try
        {
            if (dialog.ShowDialog(this) != true) { _status.Text = "已取消导出"; return; }
        }
        finally
        {
            _dialogOpen = false;
        }

        bool ok = ImportExport.ExportTo(dialog.FileName, out string detail);
        _status.Text = ok ? $"{detail} → {dialog.FileName}" : detail;
    }

    /// <summary>导入：用户选择文件 → 先格式校验 → 不合格直接拒绝并说明原因。</summary>
    private void ImportData()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要导入的桌面备忘录数据包",
            Filter = ImportExport.PackageFilter,
            DefaultExt = ".json",
            InitialDirectory = Directory.Exists(ImportExport.ExportDir) ? ImportExport.ExportDir : DataRoot.Root,
            CheckFileExists = true,
            Multiselect = false,
        };

        _dialogOpen = true;
        try
        {
            if (dialog.ShowDialog(this) != true) { _status.Text = "已取消导入"; return; }
        }
        finally
        {
            _dialogOpen = false;
        }

        if (!ImportExport.Validate(dialog.FileName, out _, out string reason))
        {
            _status.Text = "文件格式不符，已拒绝：" + reason;
            Log.Info("导入被拒: " + reason);
            return;
        }

        bool ok = ImportExport.Import(dialog.FileName, out string detail);
        if (ok && ImportExport.LastImportedStartup != StartupManager.IsEnabled())
        {
            // 开机自启是系统注册表状态，不在 settings.json 里，导入时一并同步
            if (ImportExport.LastImportedStartup) StartupManager.Enable();
            else StartupManager.Disable();
            detail += $"；开机自启已同步为 {ImportExport.LastImportedStartup}";
        }
        _status.Text = detail;
        if (ok) ShowRestartPrompt("数据已导入，需要重启应用后生效。");
    }

    /// <summary>导入/重置后的重启提示：明确告知需要重启，并直接给重启按钮。</summary>
    private void ShowRestartPrompt(string message)
    {
        var panel = new StackPanel { Width = 236, Margin = new Thickness(12, 2, 12, 12) };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        var later = MakeButton("稍后", () => { });
        later.Margin = new Thickness(0, 0, 6, 0);
        buttons.Children.Add(later);
        buttons.Children.Add(MakeButton("立即重启", RestartApp, primary: true));
        panel.Children.Add(buttons);

        var popup = new PopupWindow("导入完成", panel, null);
        later.MouseLeftButtonUp += (_, _) => popup.Close();
        _dialogOpen = true;
        popup.Closed += (_, _) => _dialogOpen = false;
        Platform.NativeMethods.GetCursorPos(out var pt);
        popup.ShowNear(pt.X + 12, pt.Y + 12);
    }

    /// <summary>重置：窗口内二次确认（不另开窗口，避免"点外部关闭"互相打断）。</summary>
    private void ShowResetConfirm()
    {
        if (_bodyHost == null) return;
        var panel = new StackPanel { Margin = new Thickness(12, 4, 12, 12) };
        panel.Children.Add(new TextBlock
        {
            Text = "重置所有数据？",
            FontSize = Theme.FontSize,
            Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "将清空全部卡片与设置（布局、卡片内容、外观、窗口位置）。\n重置前会自动做一次整包备份，可从 data\\backup_1.json 恢复。\n此操作不可撤销。",
            FontSize = 10,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = MakeButton("取消", ShowMain);
        cancel.Margin = new Thickness(0, 0, 6, 0);
        buttons.Children.Add(cancel);
        buttons.Children.Add(MakeButton("确认重置并重启", DoReset, primary: true));
        panel.Children.Add(buttons);

        _bodyHost.Children.Clear();
        _bodyHost.Children.Add(panel);
    }

    private void ShowMain()
    {
        if (_bodyHost == null) return;
        Close();
    }

    private void DoReset()
    {
        bool ok = ImportExport.ResetAll(out string detail);
        Log.Info("重置: " + detail);
        if (!ok)
        {
            _status.Text = detail;
            return;
        }
        RestartApp();
    }

    /// <summary>
    /// 重启自身。带上 --restart-wait：新进程会先等旧进程让出单实例锁，
    /// 否则新进程会撞上互斥锁直接退出，表现为"没有重启"。
    /// </summary>
    internal static void RestartApp()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false, Arguments = "--restart-wait" };
                var started = Process.Start(psi);
                Log.Info($"重启：已启动新进程 pid={started?.Id}（--restart-wait）");
            }
            else
            {
                Log.Info("重启失败：拿不到自身路径");
            }
        }
        catch (Exception ex)
        {
            Log.Info("重启失败: " + ex.Message);
        }
        Application.Current.Shutdown();
    }

    private void RestoreDefaults()
    {
        Log.Info("恢复默认外观");
        Theme.Apply(Color.FromRgb(0xDB, 0x6B, 0xBC), 15, 6, 90, 0);
        _panel.ApplyRows(3);   // 预设主界面尺寸 2×3
    }

    public IntPtr Handle { get; private set; }

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
        // 鼠标下方放不下就翻到鼠标上方（窗口较高时很常见），仍然"在鼠标附近"
        int y = screenY;
        if (y + h > work.Bottom && screenY - h - 24 >= work.Top) y = screenY - h - 24;
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - h));
        Platform.NativeMethods.SetWindowPos(Handle, Platform.NativeMethods.HWND_TOPMOST, x, y, 0, 0,
            Platform.NativeMethods.SWP_NOSIZE | Platform.NativeMethods.SWP_NOACTIVATE | Platform.NativeMethods.SWP_SHOWWINDOW);
        Log.Info($"设置窗口显示: ({x},{y}) {w}x{h}");
    }

    private bool _dragging;
    private Point _dragCursor;

    private void OnHeaderDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragCursor = PointToScreen(e.GetPosition(this));
        if (sender is UIElement element) element.CaptureMouse();
        e.Handled = true;
    }

    private void OnHeaderMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging || Handle == IntPtr.Zero) return;
        Point cursor = PointToScreen(e.GetPosition(this));
        var rect = Platform.WindowUtil.GetScreenRect(Handle);
        Platform.NativeMethods.SetWindowPos(Handle, Platform.NativeMethods.HWND_TOPMOST,
            rect.Left + (int)Math.Round(cursor.X - _dragCursor.X),
            rect.Top + (int)Math.Round(cursor.Y - _dragCursor.Y),
            0, 0, Platform.NativeMethods.SWP_NOSIZE | Platform.NativeMethods.SWP_NOACTIVATE);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (IsVisible && !_dialogOpen) Close();
    }
}
