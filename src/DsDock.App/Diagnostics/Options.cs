using System;
using System.Collections.Generic;
using System.Globalization;
using DsDock.Windows;

namespace DsDock.Diagnostics;

/// <summary>Command line options. Defaults are the shipping behaviour; every flag exists for the self test.</summary>
internal sealed class Options
{
    public string? SelfTestPath;
    public int HoldMs;
    public bool NoTray;
    public bool KeepOpen;

    /// <summary>重启自身时传入：先等旧实例退出并让出单实例锁，再启动界面。</summary>
    public bool RestartWait;
    public string Look = "live";   // live（实时透明，默认）| snapshot（静态壁纸快照）| acrylic
    public string? ForceDataRoot;

    public DockEdge Edge = DockEdge.Right;
    public double SidebarWidth = 8;
    public int SidebarAlpha = 50;

    public int Alpha = 90;          // 主界面不透明度 %
    public int Frost = 50;          // 磨砂程度 %
    public int Corner = 6;          // 圆角 px
    public double FontSize = 15;    // 字号 px
    public string Accent = "#DB6BBC";
    public int Rows = 3;            // 尺寸挡位 2×1 .. 2×6
    public bool Locked;
    public bool AutoExpand = true;  // 启动自动展开主界面（已确认，与文档"只显示侧边栏"不同）

    public int? X;
    public int? Y;

    public bool AlphaExplicit, FrostExplicit, CornerExplicit, FontExplicit, AccentExplicit, RowsExplicit;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a)
            {
                case "--selftest": o.SelfTestPath = Next(); break;
                case "--hold-ms": o.HoldMs = Int(Next(), o.HoldMs, 0, 600000); break;
                case "--no-tray": o.NoTray = true; break;
                case "--look": o.Look = Next() ?? o.Look; break;
                case "--keep-open": o.KeepOpen = true; break;
                case "--restart-wait": o.RestartWait = true; break;
                case "--force-data-root": o.ForceDataRoot = Next(); break;
                case "--edge": o.Edge = DockEdges.Parse(Next()); break;
                case "--sidebar-width": o.SidebarWidth = Double(Next(), o.SidebarWidth, 2, 60); break;
                case "--sidebar-alpha": o.SidebarAlpha = Int(Next(), o.SidebarAlpha, 0, 100); break;
                case "--alpha": o.Alpha = Int(Next(), o.Alpha, 30, 100); o.AlphaExplicit = true; break;
                case "--frost": o.Frost = Int(Next(), o.Frost, 0, 100); o.FrostExplicit = true; break;
                case "--corner": o.Corner = Int(Next(), o.Corner, 0, 24); o.CornerExplicit = true; break;
                case "--font": o.FontSize = Double(Next(), o.FontSize, 12, 20); o.FontExplicit = true; break;
                case "--accent": o.Accent = Next() ?? o.Accent; o.AccentExplicit = true; break;
                case "--rows": o.Rows = Int(Next(), o.Rows, 1, 6); o.RowsExplicit = true; break;
                case "--locked": o.Locked = true; break;
                case "--no-auto-expand": o.AutoExpand = false; break;
                case "--x": o.X = Int(Next(), 0, -20000, 20000); break;
                case "--y": o.Y = Int(Next(), 0, -20000, 20000); break;
            }
        }
        return o;
    }

    private static int Int(string? s, int fallback, int min, int max)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? Math.Clamp(v, min, max) : fallback;

    private static double Double(string? s, double fallback, double min, double max)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? Math.Clamp(v, min, max) : fallback;

    public string Summary()
    {
        var parts = new List<string>
        {
            $"edge={Edge}", $"sidebarWidth={SidebarWidth}", $"sidebarAlpha={SidebarAlpha}",
            $"alpha={Alpha}", $"frost={Frost}", $"corner={Corner}", $"font={FontSize}",
            $"accent={Accent}", $"rows={Rows}", $"locked={Locked}", $"autoExpand={AutoExpand}",
        };
        return string.Join(" ", parts);
    }
}
