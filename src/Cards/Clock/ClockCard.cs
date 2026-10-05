using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DsDock.Card.Abstractions;

namespace DsDock.Cards.Clock;

/// <summary>
/// 时钟卡片：1×1 显示时间+日期；2×1 时间更大并带秒数。
/// 右键菜单项由卡片自己贡献（"显示/隐藏秒数"），状态通过 ICardContext 持久化。
/// 只使用契约程序集，不引用宿主。
/// </summary>
public sealed class ClockCard : ICard
{
    private readonly StackPanel _root;
    private readonly TextBlock _time;
    private readonly TextBlock _date;
    private readonly DispatcherTimer _timer;
    private ICardContext? _context;
    private CardSize _size = CardSize.OneByOne;
    private bool _showSeconds = true;

    public ClockCard()
    {
        _time = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)),
        };
        _date = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
        };
        _root = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _root.Children.Add(_time);
        _root.Children.Add(_date);
        View = _root;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        Refresh();
    }

    public FrameworkElement View { get; }

    /// <summary>卡片自己的右键菜单项：宿主会把它们排在"更改尺寸/移除卡片"之前。</summary>
    public IReadOnlyList<CardMenuItem> GetMenuItems() => new[]
    {
        new CardMenuItem(_showSeconds ? "隐藏秒数" : "显示秒数", ToggleSeconds),
    };

    public void OnAttached(ICardContext context)
    {
        _context = context;
        _size = context.Size;
        RestoreState(context.LoadState());
        ApplyTypography();
        Refresh();
        _timer.Start();
    }

    public void OnSizeChanged(CardSize size)
    {
        _size = size;
        ApplyTypography();
        Refresh();
    }

    public void OnDetached()
    {
        _timer.Stop();
        _context = null;
    }

    private void ToggleSeconds()
    {
        _showSeconds = !_showSeconds;
        _context?.SaveState(JsonSerializer.Serialize(new ClockState { ShowSeconds = _showSeconds }));
        ApplyTypography();
        Refresh();
    }

    private void RestoreState(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            ClockState? state = JsonSerializer.Deserialize<ClockState>(json);
            if (state != null) _showSeconds = state.ShowSeconds;
        }
        catch
        {
            // 状态损坏时用默认值，不影响卡片显示
        }
    }

    private void ApplyTypography()
    {
        double baseSize = _context?.FontSize ?? 15;
        bool wide = _size.Columns >= 2;
        // 显示秒数时字号小一档，否则 1×1（160×160）放不下 "HH:mm:ss"
        _time.FontSize = wide
            ? (_showSeconds ? baseSize * 2.25 : baseSize * 2.6)
            : (_showSeconds ? baseSize * 1.35 : baseSize * 1.9);
        _date.FontSize = wide ? baseSize * 1.15 : baseSize * 0.95;
        _root.Margin = wide ? new Thickness(16, 0, 16, 0) : new Thickness(8, 0, 8, 0);
    }

    private void Refresh()
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        DateTime now = DateTime.Now;
        _time.Text = now.ToString(TimeFormat(culture), culture);
        // 日期跟随系统区域格式（短日期 + 星期名），不再硬编码中文
        _date.Text = now.ToString("d", culture) + " " + now.ToString("dddd", culture);
    }

    /// <summary>12/24 小时跟随系统区域设置：由 ShortTimePattern 是否含 "h" 判定。</summary>
    private string TimeFormat(CultureInfo culture)
    {
        bool hour12 = culture.DateTimeFormat.ShortTimePattern.Contains("h");
        if (hour12) return _showSeconds ? "h:mm:ss tt" : "h:mm tt";
        return _showSeconds ? "HH:mm:ss" : "HH:mm";
    }

    private sealed class ClockState
    {
        public bool ShowSeconds { get; set; } = true;
    }
}

/// <summary>卡片入口：宿主用反射找到 ICardFactory 的实现。</summary>
public sealed class ClockCardFactory : ICardFactory
{
    public string Id => "clock";
    public ICard Create() => new ClockCard();
}
