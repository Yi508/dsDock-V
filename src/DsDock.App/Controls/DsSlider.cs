using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DsDock.Appearance;

namespace DsDock.Controls;

/// <summary>
/// 自绘滑杆：轨道 + 强调色进度条 + 白色旋钮；点击/拖动即时生效，悬浮时旋钮放大（120ms）。
/// 用来替代系统 Slider，让设置界面与开关/菜单/卡片风格统一。
/// </summary>
internal sealed class DsSlider : Grid
{
    private const double KnobSize = 14;
    private const double TrackHeight = 4;

    private readonly Border _track;
    private readonly Border _fill;
    private readonly Border _knob;
    private readonly Canvas _canvas;
    private readonly ScaleTransform _knobScale = new(1, 1);

    private bool _dragging;
    private int _value;

    public DsSlider()
    {
        Height = 24;
        // 透明但可命中：Background=null 的 Panel 收不到鼠标事件
        Background = Brushes.Transparent;

        _track = new Border
        {
            Height = TrackHeight,
            CornerRadius = new CornerRadius(TrackHeight / 2),
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _fill = new Border
        {
            Height = TrackHeight,
            CornerRadius = new CornerRadius(TrackHeight / 2),
            Background = new SolidColorBrush(Theme.Accent),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _knob = new Border
        {
            Width = KnobSize,
            Height = KnobSize,
            CornerRadius = new CornerRadius(KnobSize / 2),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00)),
            RenderTransform = _knobScale,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };

        _canvas = new Canvas();
        _canvas.Children.Add(_knob);

        Children.Add(_track);
        Children.Add(_fill);
        Children.Add(_canvas);

        SizeChanged += (_, _) => UpdateVisual();
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseEnter += (_, _) => AnimateKnob(1.3);
        MouseLeave += (_, _) => AnimateKnob(1.0);
    }

    public int Minimum { get; set; }

    public int Maximum { get; set; } = 100;

    public int Value
    {
        get => _value;
        set => SetValueInternal(value, raise: false);
    }

    /// <summary>值变化（拖动过程中也会持续触发，便于实时预览）。</summary>
    public event Action<int>? ValueChanged;

    /// <summary>主题色变化时刷新进度条颜色。</summary>
    public void ApplyTheme() => _fill.Background = new SolidColorBrush(Theme.Accent);

    private void SetValueInternal(int value, bool raise)
    {
        int clamped = Math.Clamp(value, Minimum, Maximum);
        bool changed = clamped != _value;
        _value = clamped;
        UpdateVisual();
        if (changed && raise) ValueChanged?.Invoke(clamped);
    }

    private void UpdateVisual()
    {
        double width = ActualWidth;
        if (width <= 0) return;

        double ratio = Maximum > Minimum ? (double)(_value - Minimum) / (Maximum - Minimum) : 0;
        double knobX = ratio * Math.Max(0, width - KnobSize);
        Canvas.SetLeft(_knob, knobX);
        Canvas.SetTop(_knob, (Height - KnobSize) / 2);
        _fill.Width = knobX + KnobSize / 2;
    }

    private void AnimateKnob(double scale)
    {
        var animation = new DoubleAnimation(scale, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        _knobScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        _knobScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        CaptureMouse();
        SetFromX(e.GetPosition(this).X);
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            if (_dragging) { _dragging = false; ReleaseMouseCapture(); }
            return;
        }
        if (!_dragging) return;
        SetFromX(e.GetPosition(this).X);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void SetFromX(double x)
    {
        double usable = Math.Max(1, ActualWidth - KnobSize);
        double ratio = Math.Clamp((x - KnobSize / 2) / usable, 0, 1);
        SetValueInternal((int)Math.Round(Minimum + ratio * (Maximum - Minimum)), raise: true);
    }
}
