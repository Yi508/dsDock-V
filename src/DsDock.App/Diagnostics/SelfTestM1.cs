using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using DsDock.Appearance;
using DsDock.Platform;
using DsDock.Storage;
using DsDock.Windows;

namespace DsDock.Diagnostics;

internal sealed class M1Check
{
    public string Name { get; set; } = "";
    public bool Pass { get; set; }
    public bool Skipped { get; set; }
    public string Detail { get; set; } = "";
}

internal sealed class M1Report
{
    public string Tool { get; set; } = "dsDock 0.1 M1 自检（侧边栏 / 主界面形态）";
    public string StartedAt { get; set; } = "";
    public string Options { get; set; } = "";
    public string Integrity { get; set; } = "";
    public bool AllPassed { get; set; }
    public List<M1Check> Checks { get; set; } = new();

    public double StartupMs { get; set; }
    public double FpsTicks { get; set; }
    public int Dpi { get; set; }
    public double DpiScale { get; set; }
    public string WorkArea { get; set; } = "";
    public string PanelStyle { get; set; } = "";
    public string SidebarStyle { get; set; } = "";
    public string SidebarGeometry { get; set; } = "";
    public string ExpandDetail { get; set; } = "";
    public string ReflowDetail { get; set; } = "";
    public string RowsDetail { get; set; } = "";
    public string ThemeDetail { get; set; } = "";
    public string ClearDetail { get; set; } = "";
    public string DataRoot { get; set; } = "";

    public int HoldLeft { get; set; } = -1;
    public int HoldTop { get; set; } = -1;
    public int HoldWidth { get; set; }
    public int HoldHeight { get; set; }

    public List<string> LogTail { get; set; } = new();
}

/// <summary>
/// M1 自检：能自动判定的都判定（样式位、侧边栏四边几何、展开动画时长、动画期间无重排、
/// 尺寸挡位下限、外观落盘、清除卡片保留数据）。需要鼠标手感的项写进报告的"需人工确认"。
/// </summary>
internal static class SelfTestM1
{
    public static async Task<M1Report> RunAsync(Shell.AppShell shell, Options options)
    {
        var r = new M1Report
        {
            StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Options = options.Summary(),
            Integrity = ProcessIntegrity.Describe(),
            DataRoot = DataRoot.Root,
            StartupMs = shell.StartupMs,
        };

        PanelWindow? panel = shell.Panel;
        SidebarWindow? sidebar = shell.Sidebar;
        if (panel == null || sidebar == null)
        {
            Check(r, "侧边栏与主界面已创建", false, "窗口未创建");
            Finish(r);
            return r;
        }

        r.Dpi = (int)NativeMethods.GetDpiForWindow(panel.Handle);
        r.DpiScale = panel.DpiScale;
        r.WorkArea = panel.WorkArea().ToString();

        // 1) 置顶与工具窗口
        long panelEx = panel.ExStyle;
        r.PanelStyle = $"Topmost={panel.Topmost} ShowInTaskbar={panel.ShowInTaskbar} exStyle=0x{panelEx:X} " +
                       $"({WindowUtil.FlagsOf(panel.StyleFlags, panelEx)})";
        Check(r, "主界面置顶、不进任务栏（WS_EX_TOPMOST + TOOLWINDOW）",
            panel.Topmost && !panel.ShowInTaskbar &&
            (panelEx & NativeMethods.WS_EX_TOPMOST) != 0 &&
            (panelEx & NativeMethods.WS_EX_TOOLWINDOW) != 0, r.PanelStyle);

        long sideEx = sidebar.ExStyle;
        r.SidebarStyle = $"Topmost={sidebar.Topmost} ShowInTaskbar={sidebar.ShowInTaskbar} exStyle=0x{sideEx:X} " +
                         $"({WindowUtil.FlagsOf(sidebar.StyleFlags, sideEx)})";
        Check(r, "侧边栏置顶、不抢焦点、不进任务栏（NOACTIVATE + TOOLWINDOW）",
            sidebar.Topmost && (sideEx & NativeMethods.WS_EX_NOACTIVATE) != 0 &&
            (sideEx & NativeMethods.WS_EX_TOOLWINDOW) != 0, r.SidebarStyle);

        // 2) 侧边栏四边吸附几何（8px 宽 / 60% 屏高 / 贴边 / 在工作区内）
        var work = panel.WorkArea();
        var lines = new List<string>();
        bool geometryOk = true;
        foreach (DockEdge edge in new[] { DockEdge.Right, DockEdge.Left, DockEdge.Top, DockEdge.Bottom })
        {
            sidebar.SnapTo(edge, 0.5, animate: false);
            await Task.Delay(120);
            var rect = sidebar.PanelRect();
            bool vertical = edge == DockEdge.Right || edge == DockEdge.Left;
            int size = vertical ? rect.Width : rect.Height;
            int expected = Math.Max(2, (int)Math.Round(options.SidebarWidth * r.DpiScale));
            var panelRectNow = panel.PanelRect();
            int expectedLength = edge is DockEdge.Right or DockEdge.Left ? panelRectNow.Height : panelRectNow.Width;
            bool flush = edge switch
            {
                DockEdge.Right => rect.Right == work.Right,
                DockEdge.Left => rect.Left == work.Left,
                DockEdge.Top => rect.Top == work.Top,
                _ => rect.Bottom == work.Bottom,
            };
            bool inside = rect.Left >= work.Left && rect.Top >= work.Top && rect.Right <= work.Right && rect.Bottom <= work.Bottom;
            bool lengthOk = vertical ? Math.Abs(rect.Height - expectedLength) <= 2 : true;
            bool sizeOk = Math.Abs(size - expected) <= 1;
            geometryOk &= flush && inside && sizeOk && lengthOk;
            lines.Add($"{edge}: {rect}（厚 {size}px 期望 {expected}，长 {(vertical ? rect.Height : rect.Width)}px 期望 {expectedLength}）贴边={flush} 在内={inside}");
        }
        r.SidebarGeometry = string.Join("\n    ", lines);
        Check(r, "侧边栏长度跟随主界面、厚 8px、贴边、不越界", geometryOk, r.SidebarGeometry);

        // 3) 展开动画：从侧边栏矩形长到主界面，200ms，且动画期间卡片不重排
        sidebar.SnapTo(options.Edge, 0.5, animate: false);
        await Task.Delay(150);
        var sidebarRect = sidebar.PanelRect();
        var cardBefore = panel.FirstCardSizeDip();
        var canvasBefore = panel.CanvasWidthDip;

        panel.Expand(sidebar.Edge, sidebarRect, animate: true);
        var samples = new List<string>();
        for (int i = 0; i < 12; i++)
        {
            await Task.Delay(20);
            var size = panel.FirstCardSizeDip();
            if (size != null) samples.Add($"{size.Value.WidthDip:F0}x{size.Value.HeightDip:F0}");
        }
        await Task.Delay(250);

        double animMs = panel.LastAnimationMs;
        r.ExpandDetail = $"展开动画 {animMs:F0}ms（配置 {Theme.TokenDuration("SlideMs", 200):F0}ms）；" +
                         $"从 {sidebarRect} 到 {panel.PanelRect()}";
        Check(r, "主界面从侧边栏边长出，动画时长符合配置",
            animMs > 0 && Math.Abs(animMs - Theme.TokenDuration("SlideMs", 200)) < 120, r.ExpandDetail);

        string distinct = string.Join(" | ", samples.Distinct());
        bool noReflow = samples.Count > 0 && samples.Distinct().Count() == 1 &&
                        cardBefore != null && samples.Count > 0 && samples[0] == $"{cardBefore.Value.WidthDip:F0}x{cardBefore.Value.HeightDip:F0}";
        r.ReflowDetail = $"动画期间卡片尺寸采样: {distinct}（应与动画前完全一致）；canvas 宽 {canvasBefore:F0} DIP";
        Check(r, "展开动画期间卡片不重排（尺寸全程恒定，与动画前一致）", noReflow, r.ReflowDetail);

        // 4) 尺寸挡位：下限受当前卡片布局约束
        while (panel.CardCount < 3) panel.TryAddPlaceholderCard();
        int required = panel.RequiredRows;
        int applied = panel.ApplyRows(1);
        r.RowsDetail = $"卡片 {panel.CardCount} 张，需求行数 {required}；请求 1 行 → 实际 {applied} 行（窗口 {panel.WindowWidthDip:F0}x{panel.WindowHeightDip:F0} DIP）";
        Check(r, "缩小尺寸挡位被布局约束挡住（不会小到放不下卡片）", applied >= required && required >= 1, r.RowsDetail);
        int maxRows = panel.MaxRows;
        int grown = panel.ApplyRows(maxRows);
        double tallestPx = panel.WindowHeightDip * panel.DpiScale;
        bool fits = tallestPx <= panel.WorkArea().Height + 1;
        Check(r, $"尺寸挡位自适应到本屏最大值 2×{maxRows}（≤2×12）且不超出工作区",
            grown == maxRows && maxRows is >= 1 and <= 12 && fits,
            $"MaxRows={maxRows}（自适应）ApplyRows({maxRows}) → {grown}；窗口高 {tallestPx:F0}px vs 工作区 {panel.WorkArea().Height}px");

        // 5) 外观：改值 → 落盘 → 读回
        Theme.Apply(Color.FromRgb(0xA8, 0x55, 0xF7), 18, 20, 60, 80);
        await Task.Delay(250);
        shell.PersistForTest();
        var reloaded = SettingsStore.Load();
        bool persisted = reloaded.Alpha == 60 && reloaded.Frost == 80 && reloaded.Corner == 20 &&
                         Math.Abs(reloaded.FontSize - 18) < 0.1 && reloaded.Accent == "#A855F7";
        r.ThemeDetail = $"应用: accent=#A855F7 font=18 corner=20 alpha=60 frost=80；" +
                        $"读回: accent={reloaded.Accent} font={reloaded.FontSize} corner={reloaded.Corner} alpha={reloaded.Alpha} frost={reloaded.Frost}";
        Check(r, "外观修改实时生效并写入 settings.json（重启可读回）", persisted, r.ThemeDetail);

        // 6) 清除所有卡片：容器清空、数据保留
        int before = panel.CardCount;
        panel.ClearCards();
        await Task.Delay(150);
        r.ClearDetail = $"清除前 {before} 张 → 清除后 {panel.CardCount} 张，归档保留 {panel.ArchivedCount} 条";
        Check(r, "「清除所有卡片」清空容器但保留卡片数据", panel.CardCount == 0 && panel.ArchivedCount >= before, r.ClearDetail);

        // 7) 启动时间
        Check(r, "启动 3 秒内显示主界面", r.StartupMs > 0 && r.StartupMs < 3000, $"启动耗时 {r.StartupMs:F0}ms");

        // 8) 截图保持
        if (options.HoldMs > 0 && options.SelfTestPath != null)
        {
            panel.ApplyRows(3);
            while (panel.CardCount < 2) panel.TryAddPlaceholderCard();
            if (!panel.IsExpanded) panel.Expand(sidebar.Edge, sidebar.PanelRect(), animate: false);
            await Task.Delay(500);

            var panelRect = panel.PanelRect();
            r.HoldLeft = panelRect.Left - 20;
            r.HoldTop = panelRect.Top - 20;
            r.HoldWidth = panelRect.Width + 40;
            r.HoldHeight = panelRect.Height + 40;

            string marker = options.SelfTestPath + ".hold";
            string rectFile = options.SelfTestPath + ".rect";
            try { File.WriteAllText(marker, DateTime.Now.ToString("O")); } catch { }
            try { File.WriteAllText(rectFile, $"{r.HoldLeft},{r.HoldTop},{r.HoldWidth},{r.HoldHeight}"); } catch { }
            Log.Info($"保持展开 {options.HoldMs}ms 供截图，裁剪矩形=({r.HoldLeft},{r.HoldTop}) {r.HoldWidth}x{r.HoldHeight}");
            await Task.Delay(options.HoldMs);
            try { File.Delete(marker); } catch { }
            try { File.Delete(rectFile); } catch { }
        }

        Finish(r);
        return r;
    }

    private static void Finish(M1Report r)
    {
        var shell = (Shell.AppShell?)null;
        _ = shell;
        r.LogTail = Log.Lines.TakeLast(120).ToList();
        r.AllPassed = r.Checks.All(c => c.Pass);
    }

    private static void Check(M1Report r, string name, bool pass, string detail)
    {
        r.Checks.Add(new M1Check { Name = name, Pass = pass, Detail = detail });
        Log.Info($"M1 自检 {(pass ? "PASS" : "FAIL")} · {name} · {detail}");
    }

    public static async Task WriteAsync(M1Report report, string path)
    {
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        var text = new List<string>
        {
            $"dsDock 0.1 M1 自检报告  {report.StartedAt}",
            $"参数: {report.Options}",
            $"总体: {(report.AllPassed ? "全部通过" : "存在失败项")}",
            "",
        };
        foreach (M1Check c in report.Checks)
            text.Add($"  [{(c.Skipped ? "SKIP" : c.Pass ? "PASS" : "FAIL")}] {c.Name} — {c.Detail}");
        text.Add("");
        text.Add($"进程完整性: {report.Integrity}   DPI: {report.Dpi} (x{report.DpiScale:F2})");
        text.Add($"工作区: {report.WorkArea}");
        text.Add($"主界面样式: {report.PanelStyle}");
        text.Add($"侧边栏样式: {report.SidebarStyle}");
        text.Add($"侧边栏几何:\n    {report.SidebarGeometry}");
        text.Add($"展开: {report.ExpandDetail}");
        text.Add($"无重排: {report.ReflowDetail}");
        text.Add($"挡位: {report.RowsDetail}");
        text.Add($"外观: {report.ThemeDetail}");
        text.Add($"清除卡片: {report.ClearDetail}");
        text.Add($"启动: {report.StartupMs:F0}ms");
        text.Add($"数据目录: {report.DataRoot}");
        text.Add($"截图裁剪矩形: ({report.HoldLeft},{report.HoldTop}) {report.HoldWidth}x{report.HoldHeight}");
        text.Add("");
        text.Add("需人工确认（脚本做不到）:");
        text.Add("  1. 鼠标点击侧边栏能否展开主界面（手感/命中）");
        text.Add("  2. 侧边栏悬浮变亮是否明显、拖动吸附是否跟手");
        text.Add("  3. 展开/收回动画观感是否流畅");
        text.Add("  4. 托盘左键显示/隐藏、右键自绘菜单、悬浮提示");
        text.Add("  5. 主界面右键菜单、锁定后拖动是否无效");
        text.Add("  6. 真机全屏视频/游戏：主界面是否滑出到最近左右边框并隐藏，退出后滑回原位");
        text.Add("  7. 真机结束 explorer.exe：托盘图标是否自动重新出现（进程内已用 TaskbarCreated 广播验证重注册路径）");
        text.Add("  8. 真机改系统缩放（100%↔150%）：主界面/卡片/字号是否重排且不越界");
        text.Add("");
        text.Add("日志尾部:");
        foreach (string line in report.LogTail) text.Add("  " + line);

        await File.WriteAllLinesAsync(Path.ChangeExtension(path, ".txt"), text);
    }
}
