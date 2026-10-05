using System;
using System.Collections.Generic;
using System.Windows;

namespace DsDock.Layout;

/// <summary>
/// Container geometry. The spec's container is 2 columns x 6 rows with a 2x3 default;
/// M0 renders the default 2x3 while the model already carries 6 rows, so growing later
/// does not invalidate the drag/snap math.
/// </summary>
internal sealed class GridModel
{
    public GridModel(int columns, int rows, double cellSize, double gap, double padding, int visibleRows)
    {
        Columns = columns;
        Rows = rows;
        CellSize = cellSize;
        Gap = gap;
        Padding = padding;
        VisibleRows = visibleRows;
    }

    public int Columns { get; }
    public int Rows { get; }
    public int VisibleRows { get; set; }
    public double CellSize { get; }
    public double Gap { get; }
    public double Padding { get; }

    public double Pitch => CellSize + Gap;
    public double CanvasWidth => Columns * CellSize + (Columns - 1) * Gap + 2 * Padding;
    public double CanvasHeight(int rows) => rows * CellSize + (rows - 1) * Gap + 2 * Padding;
    public double VisibleHeight => CanvasHeight(VisibleRows);

    private readonly Dictionary<string, (int Col, int Row)> _placement = new();

    public bool InBounds(int col, int row)
        => col >= 0 && col < Columns && row >= 0 && row < VisibleRows;

    public bool IsFree(int col, int row, string? exceptId = null)
    {
        foreach (var kv in _placement)
        {
            if (exceptId != null && kv.Key == exceptId) continue;
            if (kv.Value.Col == col && kv.Value.Row == row) return false;
        }
        return true;
    }

    public string? OccupantAt(int col, int row)
    {
        foreach (var kv in _placement)
            if (kv.Value.Col == col && kv.Value.Row == row) return kv.Key;
        return null;
    }

    public void Place(string id, int col, int row) => _placement[id] = (col, row);

    public void Remove(string id) => _placement.Remove(id);

    public (int Col, int Row)? CellOf(string id)
        => _placement.TryGetValue(id, out var cell) ? cell : null;

    public Point CellOrigin(int col, int row) => new(Padding + col * Pitch, Padding + row * Pitch);

    public (int Col, int Row) NearestCell(Point p)
    {
        int col = (int)Math.Round((p.X - Padding) / Pitch, MidpointRounding.AwayFromZero);
        int row = (int)Math.Round((p.Y - Padding) / Pitch, MidpointRounding.AwayFromZero);
        return (col, row);
    }
}
