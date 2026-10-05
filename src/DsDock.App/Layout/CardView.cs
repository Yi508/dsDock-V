using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DsDock.Anim;
using DsDock.Layout;

namespace DsDock.Layout;

/// <summary>
/// Placeholder card: 1x1 cell, draggable with 1:1 pointer tracking, snaps to the nearest
/// free cell on release and springs back when the target cell is occupied or out of bounds.
/// </summary>
internal sealed partial class CardView : Border
{
    private readonly GridModel _grid;
    private readonly FrameClock _clock;
    private readonly Canvas _canvas;
    private readonly TranslateTransform _translate = new();
    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;

    private Point _dragStart;
    private bool _dragging;
    private bool _moved;

    public CardView(string id, GridModel grid, FrameClock clock, Canvas canvas, int index)
    {
        Id = id;
        _grid = grid;
        _clock = clock;
        _canvas = canvas;

        Width = grid.CellSize;
        Height = grid.CellSize;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        Background = new SolidColorBrush(Color.FromArgb((byte)(index == 0 ? 0x26 : 0x1A), 0xFF, 0xFF, 0xFF));
        Cursor = Cursors.Hand;
        RenderTransform = _translate;

        _title = new TextBlock
        {
            Text = $"占位卡片 {index + 1}",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
            TextAlignment = TextAlignment.Center,
        };
        _subtitle = new TextBlock
        {
            FontSize = 10,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };

        Child = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0),
            IsHitTestVisible = false,
            Children = { _title, _subtitle },
        };

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
    }

    public string Id { get; }
    public (int Col, int Row) Cell { get; private set; }

    /// <summary>Number of mouse-down events that actually reached this card (input reachability proof).</summary>
    public int DragStarts { get; private set; }

    public event Action<string>? LogMessage;
    public event Action? PlacementChanged;

    public void SetCell(int col, int row)
    {
        Cell = (col, row);
        Point origin = _grid.CellOrigin(col, row);
        Canvas.SetLeft(this, origin.X);
        Canvas.SetTop(this, origin.Y);
        UpdateLabels();
    }

    private void UpdateLabels()
    {
        _subtitle.Text = $"格 ({Cell.Col},{Cell.Row}) · 拖动我";
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _dragging = true;
        _moved = false;
        DragStarts++;
        _dragStart = e.GetPosition(_canvas);
        _clock.CancelAll();
        _translate.X = 0;
        _translate.Y = 0;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        Point p = e.GetPosition(_canvas);
        double dx = p.X - _dragStart.X;
        double dy = p.Y - _dragStart.Y;
        if (Math.Abs(dx) > 2 || Math.Abs(dy) > 2) _moved = true;
        _translate.X = dx;
        _translate.Y = dy;
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;

        if (!_moved)
        {
            ResetTransform();
            LogMessage?.Invoke($"{Id} 点击（未移动），仍在 ({Cell.Col},{Cell.Row})");
            return;
        }

        CompleteDrop();
    }

    /// <summary>Snap or rebound; returns "snap" | "rebound" | "same".</summary>
    public string CompleteDrop()
    {
        Point baseOrigin = _grid.CellOrigin(Cell.Col, Cell.Row);
        var dropped = new Point(baseOrigin.X + _translate.X, baseOrigin.Y + _translate.Y);
        (int col, int row) = _grid.NearestCell(dropped);

        string? occupant = _grid.OccupantAt(col, row);
        if (!_grid.InBounds(col, row))
        {
            LogMessage?.Invoke($"{Id} 越界回弹: 目标格({col},{row}) 不在容器内 → 回弹到 ({Cell.Col},{Cell.Row})");
            Rebound();
            return "rebound";
        }
        if (occupant != null && occupant != Id)
        {
            LogMessage?.Invoke($"{Id} 重叠回弹: 目标格({col},{row}) 已被 {occupant} 占用 → 回弹到 ({Cell.Col},{Cell.Row})");
            Rebound();
            return "rebound";
        }

        Point target = _grid.CellOrigin(col, row);
        double dx = target.X - baseOrigin.X;
        double dy = target.Y - baseOrigin.Y;
        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)
        {
            ResetTransform();
            LogMessage?.Invoke($"{Id} 原地放下，仍在 ({Cell.Col},{Cell.Row})");
            return "same";
        }

        (int Col, int Row) from = Cell;
        AnimateTo(dx, dy, 200, Ease.CubicOut, () =>
        {
            _grid.Remove(Id);
            _grid.Place(Id, col, row);
            SetCell(col, row);
            _translate.X = 0;
            _translate.Y = 0;
            LogMessage?.Invoke($"{Id} 吸附: ({from.Col},{from.Row}) -> ({col},{row})");
            PlacementChanged?.Invoke();
        });
        return "snap";
    }

    private void Rebound()
    {
        AnimateTo(0, 0, 200, Ease.BackOut, () =>
        {
            _translate.X = 0;
            _translate.Y = 0;
            LogMessage?.Invoke($"{Id} 回弹完成，位于 ({Cell.Col},{Cell.Row})");
        });
    }

    private void ResetTransform()
    {
        _translate.X = 0;
        _translate.Y = 0;
    }

    private void AnimateTo(double dx, double dy, double ms, Func<double, double> ease, Action? done)
    {
        double sx = _translate.X;
        double sy = _translate.Y;
        _clock.Animate(ms, ease,
            t =>
            {
                _translate.X = sx + (dx - sx) * t;
                _translate.Y = sy + (dy - sy) * t;
            },
            () =>
            {
                _translate.X = dx;
                _translate.Y = dy;
                done?.Invoke();
            });
    }
}
