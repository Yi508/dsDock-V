using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DsDock.Anim;
using DsDock.Card.Abstractions;

namespace DsDock.Plugins;

/// <summary>
/// 卡片交互：按住卡片任意空白处拖动（跟手，不显示占位格）、松手吸附最近网格、
/// 重叠/越界则 200ms 回弹；出现/消失动画 200ms；右键交给宿主弹自绘菜单。
/// </summary>
internal sealed partial class CardHost
{
    /// <summary>宿主判定这次移动是否合法（重叠/越界返回 false → 回弹）。</summary>
    public Func<CardHost, int, int, bool>? TryMoveTo { get; set; }

    /// <summary>右键请求（宿主弹出「更改尺寸 / 移除卡片」）。</summary>
    public Action<CardHost>? ContextMenuRequested { get; set; }

    /// <summary>统一 30FPS 时钟（动画用）。</summary>
    public FrameClock? Clock { get; set; }

    public double LastAnimationMs { get; private set; }

    private Point _grabOffset;
    private bool _dragging;
    private (int Col, int Row) _originCell;

    private Point CellPoint(int col, int row)
        => new(_pad + col * (_cellSize + _gap), _pad + row * (_cellSize + _gap));

    // ---------------------------------------------------------------- 增删动画

    public void PlayAppear()
    {
        if (Clock == null) { Opacity = 1; return; }
        var scale = new ScaleTransform(0.92, 0.92);
        RenderTransform = scale;
        RenderTransformOrigin = new Point(0.5, 0.5);
        Opacity = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Clock.Animate(200, Ease.CubicOut,
            t => { Opacity = t; scale.ScaleX = scale.ScaleY = 0.92 + 0.08 * t; },
            () => { Opacity = 1; RenderTransform = null; watch.Stop(); LastAnimationMs = watch.Elapsed.TotalMilliseconds; });
    }

    public void PlayDisappear(Action done)
    {
        if (Clock == null) { done(); return; }
        var scale = new ScaleTransform(1, 1);
        RenderTransform = scale;
        RenderTransformOrigin = new Point(0.5, 0.5);
        Clock.Animate(200, Ease.CubicOut,
            t => { Opacity = 1 - t; scale.ScaleX = scale.ScaleY = 1 - 0.08 * t; },
            done);
    }

    // ---------------------------------------------------------------- 拖动 / 吸附 / 回弹

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _grabOffset = e.GetPosition(this);
        _originCell = Cell;
        _dragging = false;
        // 不在这里捕获鼠标：先让卡片自己的控件拿到点击，超过 3px 才算拖动
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            if (_dragging) { _dragging = false; ReleaseMouseCapture(); }
            return;
        }

        if (!_dragging)
        {
            Point p = e.GetPosition(this);
            if (Math.Abs(p.X - _grabOffset.X) < 3 && Math.Abs(p.Y - _grabOffset.Y) < 3) return;
            _dragging = true;
            CaptureMouse();
        }

        if (_canvas == null) return;
        Point inCanvas = e.GetPosition(_canvas);
        Canvas.SetLeft(this, inCanvas.X - _grabOffset.X);
        Canvas.SetTop(this, inCanvas.Y - _grabOffset.Y);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        if (!SnapToNearestCell()) AnimateBackTo(_originCell);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        e.Handled = true;
        ContextMenuRequested?.Invoke(this);
    }

    /// <summary>尺寸切换动画 200ms（spec：改变卡片尺寸时有流畅的改变动画）。</summary>
    public void AnimateSizeTo(CardSize size)
    {
        double fromW = Width;
        double fromH = Height;
        Size = size;
        double toW = size.Columns * _cellSize + (size.Columns - 1) * _gap;
        double toH = size.Rows * _cellSize + (size.Rows - 1) * _gap;
        if (Clock == null || double.IsNaN(fromW) || fromW <= 0)
        {
            Width = toW;
            Height = toH;
            return;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Clock.Animate(200, Ease.CubicOut,
            t => { Width = fromW + (toW - fromW) * t; Height = fromH + (toH - fromH) * t; },
            () => { Width = toW; Height = toH; watch.Stop(); LastAnimationMs = watch.Elapsed.TotalMilliseconds; });
    }

    /// <summary>松手吸附到最近的合法网格；不合法返回 false（调用方负责回弹）。</summary>
    public bool SnapToNearestCell()
    {
        double pitch = _cellSize + _gap;
        int col = (int)Math.Round((Canvas.GetLeft(this) - _pad) / pitch);
        int row = (int)Math.Round((Canvas.GetTop(this) - _pad) / pitch);
        bool accepted = TryMoveTo?.Invoke(this, col, row) ?? false;
        if (accepted) SetCell(col, row);
        return accepted;
    }

    /// <summary>200ms 回弹到指定格（重叠/越界时用）。</summary>
    public void AnimateBackTo((int Col, int Row) cell)
    {
        double fromX = Canvas.GetLeft(this);
        double fromY = Canvas.GetTop(this);
        Point target = CellPoint(cell.Col, cell.Row);
        if (Clock == null)
        {
            SetCell(cell.Col, cell.Row);
            return;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Clock.Animate(200, Ease.CubicOut,
            t =>
            {
                Canvas.SetLeft(this, fromX + (target.X - fromX) * t);
                Canvas.SetTop(this, fromY + (target.Y - fromY) * t);
            },
            () => { SetCell(cell.Col, cell.Row); watch.Stop(); LastAnimationMs = watch.Elapsed.TotalMilliseconds; });
    }

    /// <summary>自检用：把卡片摆到某格附近（不改 Cell），随后调用 SnapToNearestCell 验证吸附/回弹。</summary>
    public void MoveNear(int col, int row, double dx = 0, double dy = 0)
    {
        Point p = CellPoint(col, row);
        Canvas.SetLeft(this, p.X + dx);
        Canvas.SetTop(this, p.Y + dy);
    }

    public bool IsDragging => _dragging;
}
