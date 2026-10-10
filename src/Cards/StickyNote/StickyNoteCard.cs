using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DsDock.Card.Abstractions;

namespace DsDock.Cards.StickyNote;

/// <summary>
/// 便利贴卡片：与时钟同级的普通卡片（一张卡片 = 一张便利贴）。
/// 默认卡片形状（圆角 8，边框颜色跟随所选颜色）；左上角完成状态圆点；左下角"编辑"按钮；
/// 点击卡片切换完成态；编辑界面可改内容 / 颜色 / 完成状态，并**设置提醒**（到点走宿主的系统通知）。
/// 状态：Content / Color / Done / RemindAt / Reminded，经 ICardContext 持久化。
/// </summary>
public sealed class StickyNoteCard : ICard
{
    // 6 个高饱和、色相区分明显的预设色（不放相近色）：红 橙 绿 青 蓝 紫
    private static readonly string[] Palette = { "#EF4444", "#F97316", "#22C55E", "#06B6D4", "#3B82F6", "#A855F7" };
    private const int MaxChars = 300;   // 字数上限（原 100，按需求提高）
    private const string EmptyHint = "点击左下角编辑";

    private readonly Grid _root;
    private readonly Border _shell;
    private readonly Ellipse _dot;
    private readonly TextBlock _text;
    private readonly TextBlock _remind;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _reminderTimer;

    private ICardContext? _context;
    private CardSize _size = CardSize.OneByOne;
    private string _content = "";
    private string _color = Palette[0];
    private bool _done;
    private DateTime? _remindAt;
    private bool _reminded;

    public StickyNoteCard()
    {
        _dot = new Ellipse
        {
            Width = 10,
            Height = 10,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(9, 7, 0, 0),
            IsHitTestVisible = false,
        };

        _text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)),
        };

        _remind = new TextBlock
        {
            FontSize = 9,
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            Visibility = Visibility.Collapsed,
        };

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        stack.Children.Add(_text);
        stack.Children.Add(_remind);

        // 与时钟卡片同样的卡片样式：不铺底色，外观由宿主的卡片壳提供；这里只画同色边框
        _shell = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 22, 10, 26),
            Background = null,
            Child = stack,
        };

        // 左下角编辑按钮：单击直接进入编辑
        var editButton = new Border
        {
            Width = 34,
            Height = 18,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8, 0, 0, 6),
            Cursor = Cursors.Hand,
            ToolTip = "编辑",
            Child = new TextBlock
            {
                Text = "编辑",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        WireHover(editButton);
        editButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenEditor(); };

        // 透明但可命中：否则空白处收不到鼠标事件（双击进不了编辑、按钮也不响应）
        _root = new Grid { Background = Brushes.Transparent };
        _root.Children.Add(_shell);
        _root.Children.Add(_dot);
        _root.Children.Add(editButton);

        _root.MouseLeftButtonUp += (_, e) =>
        {
            if (e.ClickCount >= 2) { OpenEditor(); return; }
            ToggleDone();
        };
        // 注意：右键**不要**设 e.Handled = true —— 那样事件不会冒泡到宿主的卡片壳，右键菜单就弹不出来

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Render();

        // 提醒检查：每 5 秒看一次（到点即触发一次）
        _reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _reminderTimer.Tick += (_, _) => CheckReminder();

        Render();
    }

    public FrameworkElement View => _root;

    public IReadOnlyList<CardMenuItem> GetMenuItems() => new[]
    {
        new CardMenuItem("编辑", OpenEditor),
        new CardMenuItem(_done ? "标记未完成" : "标记完成", ToggleDone),
        new CardMenuItem("提醒：10 分钟后", () => SetReminder(TimeSpan.FromMinutes(10))),
        new CardMenuItem("提醒：1 分钟后", () => SetReminder(TimeSpan.FromMinutes(1))),
        new CardMenuItem(_remindAt == null ? "清除提醒（未设置）" : "清除提醒", ClearReminder),
    };

    public void OnAttached(ICardContext context)
    {
        _context = context;
        _size = context.Size;
        RestoreState(context.LoadState());
        ApplySize();
        Render();
        _timer.Start();

        // 有提醒就开始检查；已过期未提醒的会立刻触发系统通知
        if (_remindAt != null) _reminderTimer.Start();
        CheckReminder();
    }

    public void OnSizeChanged(CardSize size)
    {
        _size = size;
        ApplySize();
    }

    public void OnDetached()
    {
        _timer.Stop();
        _reminderTimer.Stop();
        _context = null;
    }

    // ---------------------------------------------------------------- 行为

    private void ToggleDone()
    {
        _done = !_done;
        Save();
        Render();
    }

    private void OpenEditor()
    {
        if (_context == null) return;
        IEditorPopup? popup = null;
        popup = _context.ShowPopup("编辑便利贴", BuildEditor(() => popup));
    }

    // ---------------------------------------------------------------- 提醒

    /// <summary>设定提醒（相对现在）。</summary>
    private void SetReminder(TimeSpan delay) => SetReminderAt(DateTime.Now.Add(delay));

    /// <summary>设定提醒（绝对时间）。</summary>
    private void SetReminderAt(DateTime when)
    {
        _remindAt = when;
        _reminded = false;
        Save();
        Render();
        _reminderTimer.Start();
        Log($"设定提醒: {when:yyyy-MM-dd HH:mm:ss}");
    }

    private void ClearReminder()
    {
        _remindAt = null;
        _reminded = false;
        Save();
        Render();
        _reminderTimer.Stop();
        Log("已清除提醒");
    }

    /// <summary>到点检查：时间到且未提醒过 → 通过宿主的系统通知提醒用户。</summary>
    private void CheckReminder()
    {
        if (_remindAt is not { } when || _reminded) return;
        if (DateTime.Now < when) return;

        _reminded = true;
        Save();
        Render();
        string body = string.IsNullOrWhiteSpace(_content) ? "(空便利贴)" : _content;
        _context?.Notify("便利贴提醒", body);
        Log($"提醒已触发: {body}");
    }

    private string ReminderLabel()
    {
        if (_remindAt is not { } when) return "未设置提醒";
        if (_reminded) return $"已提醒（{when:MM-dd HH:mm}）";
        TimeSpan left = when - DateTime.Now;
        string leftText = left.TotalMinutes >= 1
            ? $"还有 {left.TotalMinutes:F0} 分钟"
            : $"还有 {Math.Max(0, (int)left.TotalSeconds)} 秒";
        return $"{when:MM-dd HH:mm}（{leftText}）";
    }

    // ---------------------------------------------------------------- 编辑界面

    private FrameworkElement BuildEditor(Func<IEditorPopup?> popupAccessor)
    {
        string pendingColor = _color;
        bool pendingDone = _done;

        var panel = new StackPanel { Width = 244, Margin = new Thickness(12, 2, 12, 12) };

        var editor = new TextBox
        {
            Text = _content,
            MaxLength = MaxChars,
            AcceptsReturn = true,
            Height = 58,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            Padding = new Thickness(6),
        };
        var counter = new TextBlock
        {
            Margin = new Thickness(2, 3, 0, 0),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(0x77, 0xFF, 0xFF, 0xFF)),
            Text = $"{editor.Text.Length}/{MaxChars}",
        };
        editor.TextChanged += (_, _) => counter.Text = $"{editor.Text.Length}/{MaxChars}";
        editor.Loaded += (_, _) => { Keyboard.Focus(editor); editor.Focus(); editor.SelectAll(); };
        panel.Children.Add(editor);
        panel.Children.Add(counter);
        panel.Children.Add(new TextBlock
        {
            Text = "回车保存，Shift+回车换行",
            Margin = new Thickness(2, 2, 0, 0),
            Foreground = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
        });

        // 颜色
        panel.Children.Add(SectionLabel("颜色"));
        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        var rings = new List<Border>();
        foreach (string hex in Palette)
        {
            var swatch = new Border
            {
                Width = 22, Height = 22,
                Margin = new Thickness(0, 0, 6, 0),
                CornerRadius = new CornerRadius(6),
                Background = Brush(hex),
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(hex == pendingColor
                    ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                    : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                Cursor = Cursors.Hand,
                ToolTip = hex,
            };
            WireHover(swatch);
            swatch.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                pendingColor = hex;
                foreach (Border ring in rings)
                    ring.BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
                swatch.BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
            };
            rings.Add(swatch);
            swatches.Children.Add(swatch);
        }
        panel.Children.Add(swatches);

        // 完成状态
        panel.Children.Add(SectionLabel("完成状态"));
        var doneToggle = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 5, 10, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            Cursor = Cursors.Hand,
        };
        var doneLabel = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)),
            Text = pendingDone ? "已完成（点击改为未完成）" : "未完成（点击改为已完成）",
        };
        WireHover(doneToggle);
        doneToggle.Child = doneLabel;
        doneToggle.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            pendingDone = !pendingDone;
            doneLabel.Text = pendingDone ? "已完成（点击改为未完成）" : "未完成（点击改为已完成）";
        };
        panel.Children.Add(doneToggle);

        // 提醒
        panel.Children.Add(BuildReminderSection());

        // 按钮
        void CommitAndClose()
        {
            string text = editor.Text.Trim();
            if (text.Length > MaxChars) text = text.Substring(0, MaxChars);
            _content = text;
            _color = pendingColor;
            _done = pendingDone;
            Save();
            Render();
            popupAccessor()?.Close();
        }

        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers == ModifierKeys.Shift) return;
            e.Handled = true;
            CommitAndClose();
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancel = EditorButton("取消", false);
        cancel.MouseLeftButtonUp += (_, e) => { e.Handled = true; popupAccessor()?.Close(); };
        buttons.Children.Add(cancel);
        var save = EditorButton("保存", true);
        save.MouseLeftButtonUp += (_, e) => { e.Handled = true; CommitAndClose(); };
        buttons.Children.Add(save);
        panel.Children.Add(buttons);

        return panel;
    }

    /// <summary>编辑界面里的"提醒"一节：状态 + 快捷预设 + 自定义时间（HH:mm）。</summary>
    private FrameworkElement BuildReminderSection()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(SectionLabel("提醒"));

        var status = new TextBlock
        {
            FontSize = 10,
            Text = ReminderLabel(),
            Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
        };
        panel.Children.Add(status);

        var quickRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        void Quick(string text, TimeSpan delay)
        {
            var button = EditorButton(text, false);
            button.Margin = new Thickness(0, 0, 6, 0);
            button.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                SetReminder(delay);
                status.Text = ReminderLabel();
            };
            quickRow.Children.Add(button);
        }
        Quick("1 分钟后", TimeSpan.FromMinutes(1));
        Quick("10 分钟后", TimeSpan.FromMinutes(10));
        Quick("1 小时后", TimeSpan.FromHours(1));
        panel.Children.Add(quickRow);

        var customRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var timeBox = new TextBox
        {
            Width = 58,
            FontSize = 11,
            Text = DateTime.Now.AddMinutes(30).ToString("HH:mm"),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            Padding = new Thickness(5, 2, 5, 2),
        };
        customRow.Children.Add(timeBox);

        var apply = EditorButton("设定", false);
        apply.Margin = new Thickness(6, 0, 6, 0);
        apply.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!TimeSpan.TryParse(timeBox.Text.Trim(), out TimeSpan parsed))
            {
                status.Text = "时间格式应为 HH:mm";
                return;
            }
            DateTime when = DateTime.Today.Add(parsed);
            if (when <= DateTime.Now) when = when.AddDays(1);   // 已过点则顺延到明天
            SetReminderAt(when);
            status.Text = ReminderLabel();
        };
        customRow.Children.Add(apply);

        var clear = EditorButton("清除", false);
        clear.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ClearReminder();
            status.Text = ReminderLabel();
        };
        customRow.Children.Add(clear);
        panel.Children.Add(customRow);

        panel.Children.Add(new TextBlock
        {
            FontSize = 13,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Text = "到点通过 Windows 系统通知提醒（需卡片在容器内且应用在运行）。",
            Foreground = new SolidColorBrush(Color.FromArgb(0x77, 0xFF, 0xFF, 0xFF)),
        });
        return panel;
    }

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Margin = new Thickness(2, 10, 0, 4),
        Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
    };

    /// <summary>
    /// 卡片内按钮的悬停高亮（卡片不能引用宿主的 DsHover，所以在卡片里做同样两行）。
    /// 新增卡片按钮请一律套用它，保证观感一致。
    /// </summary>
    private void WireHover(Border button)
    {
        Color baseColor = (button.BorderBrush as SolidColorBrush)?.Color ?? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);
        button.MouseEnter += (_, _) => button.BorderBrush =
            new SolidColorBrush(_context?.Accent ?? Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
        button.MouseLeave += (_, _) => button.BorderBrush = new SolidColorBrush(baseColor);
        if (button.BorderThickness.Left <= 0) button.BorderThickness = new Thickness(1);
    }

    private Border EditorButton(string text, bool primary)
    {
        var button = new Border
    {
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(10, 5, 10, 5),
        Margin = new Thickness(6, 0, 0, 0),
        Background = new SolidColorBrush(primary
            ? Color.FromArgb(0x66, 0x3B, 0x82, 0xF6)
            : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
        Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
            },
        };
        WireHover(button);
        return button;
    }

    // ---------------------------------------------------------------- 呈现

    private void ApplySize()
    {
        double font = (_context?.FontSize ?? 15) * (_size.Rows >= 2 ? 1.0 : 0.9);
        _text.FontSize = font;
        _dot.Width = _dot.Height = _size.Rows >= 2 ? 12 : 10;
    }

    private void Render()
    {
        bool empty = string.IsNullOrWhiteSpace(_content);
        _text.Text = empty ? EmptyHint : _content;
        _text.Opacity = empty ? 0.45 : 1.0;
        _text.TextDecorations = _done ? TextDecorations.Strikethrough : null;
        _text.Foreground = new SolidColorBrush(_done
            ? Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));

        _remind.Visibility = _remindAt == null ? Visibility.Collapsed : Visibility.Visible;
        _remind.Text = _remindAt is { } when ? (_reminded ? "已提醒" : $"提醒 {when:HH:mm}") : "";

        // 颜色只作用在边框与状态圆点（不铺卡片底色）
        Color accent = ParseColor(_color);
        _shell.BorderBrush = new SolidColorBrush(accent);
        _shell.BorderThickness = new Thickness(1);
        _dot.Stroke = new SolidColorBrush(accent);
        _dot.StrokeThickness = 2;
        _dot.Fill = new SolidColorBrush(_done ? accent : Color.FromArgb(0x22, accent.R, accent.G, accent.B));
    }

    private static SolidColorBrush Brush(string hex) => new(ParseColor(hex));

    private static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Color.FromRgb(0xEF, 0x44, 0x44); }
    }

    private void Log(string message) => _context?.Log(message);

    // ---------------------------------------------------------------- 状态

    private void Save()
        => _context?.SaveState(JsonSerializer.Serialize(new StickyState
        {
            Content = _content,
            Color = _color,
            Done = _done,
            RemindAt = _remindAt?.ToString("O"),
            Reminded = _reminded,
        }));

    private void RestoreState(string json)
    {
        _color = Palette[0];
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            StickyState? state = JsonSerializer.Deserialize<StickyState>(json);
            if (state == null) return;
            _content = state.Content ?? "";
            _done = state.Done;
            _reminded = state.Reminded;
            _remindAt = DateTime.TryParse(state.RemindAt, out DateTime parsed) ? parsed : null;
            if (!string.IsNullOrWhiteSpace(state.Color) && Array.IndexOf(Palette, state.Color) >= 0)
                _color = state.Color;
        }
        catch
        {
            // 状态损坏时退回默认观感，绝不让卡片崩掉
        }
    }

    /// <summary>自检用：当前状态摘要。</summary>
    public string DebugState => $"content=\"{_content}\" color={_color} done={_done} remindAt={_remindAt:O}";

    private sealed class StickyState
    {
        public string? Content { get; set; }
        public string? Color { get; set; }
        public bool Done { get; set; }
        public string? RemindAt { get; set; }
        public bool Reminded { get; set; }
    }
}

public sealed class StickyNoteCardFactory : ICardFactory
{
    public string Id => "sticky_note";
    public ICard Create() => new StickyNoteCard();
}
