using System;
using System.Collections.Generic;
using System.Linq;

namespace DsDock.Layout;

/// <summary>One card's footprint inside the container grid.</summary>
internal sealed record Placement(string InstanceId, int Col, int Row, int Columns, int Rows);

/// <summary>
/// Pure layout logic for the container: 2 columns × 1..6 rows.
/// No WPF types here on purpose - this is what the self test exercises exhaustively
/// (多格占用 / 找空位 / 自动扩行 / 缩小下限).
/// </summary>
internal static class LayoutEngine
{
    public const int Columns = 2;
    /// <summary>硬上限：任何分辨率下都不超过 2×12。</summary>
    public const int MaxRows = 12;
    public const int MinRows = 1;

    public static int Cells(int columns, int rows) => columns * rows;

    /// <summary>True when the footprint is inside the grid and does not overlap anything else.</summary>
    public static bool CanPlace(IReadOnlyList<Placement> all, int col, int row, int columns, int rows, int gridRows, string? exceptId = null)
    {
        if (col < 0 || row < 0) return false;
        if (col + columns > Columns || row + rows > gridRows) return false;

        foreach (Placement p in all)
        {
            if (exceptId != null && p.InstanceId == exceptId) continue;
            bool overlaps = col < p.Col + p.Columns && p.Col < col + columns &&
                            row < p.Row + p.Rows && p.Row < row + rows;
            if (overlaps) return false;
        }
        return true;
    }

    /// <summary>First free cell that fits the footprint (row-major), or null.</summary>
    public static (int Col, int Row)? FindSlot(IReadOnlyList<Placement> all, int columns, int rows, int gridRows, string? exceptId = null)
    {
        for (int row = 0; row < gridRows; row++)
        {
            for (int col = 0; col < Columns; col++)
            {
                if (CanPlace(all, col, row, columns, rows, gridRows, exceptId)) return (col, row);
            }
        }
        return null;
    }

    /// <summary>Rows needed to hold every placement (used as the size slider's lower bound).</summary>
    public static int RequiredRows(IReadOnlyList<Placement> all)
    {
        int rows = MinRows;
        foreach (Placement p in all)
            rows = Math.Max(rows, p.Row + p.Rows);
        return Math.Min(rows, MaxRows);
    }

    /// <summary>Does everything still fit when the grid is this tall?</summary>
    public static bool FitsInRows(IReadOnlyList<Placement> all, int gridRows)
        => RequiredRows(all) <= gridRows;

    /// <summary>
    /// Rows needed to add one more card (spec: 一次调整到能容纳新卡片的最小挡位，不重排已有卡片).
    /// Returns null when even 2×6 cannot hold it.
    /// </summary>
    public static int? RowsNeededToAdd(IReadOnlyList<Placement> all, int columns, int rows, int maxRows = MaxRows)
    {
        for (int gridRows = RequiredRows(all); gridRows <= maxRows; gridRows++)
        {
            if (FindSlot(all, columns, rows, gridRows) != null) return gridRows;
        }
        return null;
    }

    /// <summary>Clamp a requested row count to what the current layout allows.</summary>
    public static int ClampRows(IReadOnlyList<Placement> all, int requested)
        => ClampRows(all, requested, MaxRows);

    public static int ClampRows(IReadOnlyList<Placement> all, int requested, int maxRows)
        => Math.Clamp(requested, RequiredRows(all), Math.Max(MinRows, maxRows));

    /// <summary>
    /// 该屏幕允许的最大行数：容器高度 = 顶栏 + 行数×格高 + (行数-1)×间距 + 2×内边距，
    /// 反解出不超出工作区的行数，再钳到 [1,12]。纯逻辑，便于穷举自检。
    /// </summary>
    public static int MaxRowsFor(double workHeightDip, double topBarHeight, double cellSize, double gap, double padding)
    {
        double available = workHeightDip - topBarHeight - 2 * padding + gap;
        if (available <= 0) return MinRows;
        int rows = (int)Math.Floor(available / (cellSize + gap));
        return Math.Clamp(rows, MinRows, MaxRows);
    }

    public static string Describe(IReadOnlyList<Placement> all)
        => all.Count == 0 ? "(空)" : string.Join(", ", all.Select(p => $"{p.InstanceId}@({p.Col},{p.Row}) {p.Columns}x{p.Rows}"));
}
