using DsDock.Platform;

namespace DsDock.Windows;

/// <summary>Screen edge the container docks to when collapsed.</summary>
internal enum DockEdge
{
    Right,
    Left,
    Top,
    Bottom,
}

internal static class DockEdges
{
    public static bool IsVertical(this DockEdge edge) => edge is DockEdge.Left or DockEdge.Right;

    public static DockEdge Parse(string? s) => (s ?? "").ToLowerInvariant() switch
    {
        "left" => DockEdge.Left,
        "top" => DockEdge.Top,
        "bottom" => DockEdge.Bottom,
        _ => DockEdge.Right,
    };

    /// <summary>Nearest edge of the work area to the given window rectangle.</summary>
    public static DockEdge Nearest(NativeMethods.RECT window, NativeMethods.RECT work)
    {
        int dl = window.Left - work.Left;
        int dr = work.Right - window.Right;
        int dt = window.Top - work.Top;
        int db = work.Bottom - window.Bottom;

        DockEdge edge = DockEdge.Right;
        int best = dr;
        if (dl < best) { best = dl; edge = DockEdge.Left; }
        if (dt < best) { best = dt; edge = DockEdge.Top; }
        if (db < best) { best = db; edge = DockEdge.Bottom; }
        return edge;
    }
}
