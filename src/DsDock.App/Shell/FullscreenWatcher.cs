using System;
using System.Windows.Threading;
using DsDock.Diagnostics;
using DsDock.Platform;

namespace DsDock.Shell;

/// <summary>
/// 全屏检测（M6）：前台窗口是否铺满它所在的显示器 → 隐藏/恢复侧边栏与主界面。
/// 判定是纯几何（CoversMonitor 可穷举自检），并排除桌面/任务栏/自身窗口，
/// 避免把"最大化窗口""任务栏可见"误判成全屏。
/// </summary>
internal sealed class FullscreenWatcher
{
    private readonly DispatcherTimer _timer;
    private readonly Func<IntPtr[]> _selfWindows;
    private bool _hidden;
    private int _fullscreenStreak;

    public FullscreenWatcher(TimeSpan interval, Func<IntPtr[]> selfWindows)
    {
        _selfWindows = selfWindows;
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>检测到进入/退出全屏（true = 进入全屏，需要让位）。</summary>
    public event Action<bool>? FullscreenChanged;

    /// <summary>开关（设置界面里的"全屏时自动隐藏"）。关闭时本检测器不动作。</summary>
    public bool Enabled { get; set; } = true;

    public bool IsHidden => _hidden;
    public int PollCount { get; private set; }
    public string LastReason { get; private set; } = "(尚未检测)";

    /// <summary>连续命中多少次才认为进入全屏（去抖，避免切换瞬间闪烁）。</summary>
    public int EnterStreak { get; set; } = 2;

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    /// <summary>纯几何：窗口矩形是否铺满显示器（容忍 tolerancePx 边框误差）。</summary>
    public static bool CoversMonitor(NativeMethods.RECT window, NativeMethods.RECT monitor, int tolerancePx)
    {
        if (window.Width <= 0 || window.Height <= 0) return false;
        return window.Left <= monitor.Left + tolerancePx
            && window.Top <= monitor.Top + tolerancePx
            && window.Right >= monitor.Right - tolerancePx
            && window.Bottom >= monitor.Bottom - tolerancePx;
    }

    /// <summary>某个窗口是否算"占用全屏"（排除自身、桌面、任务栏）。</summary>
    public static bool Qualifies(IntPtr hwnd, IntPtr[] selfWindows, int tolerancePx, out string reason)
    {
        if (hwnd == IntPtr.Zero) { reason = "无前台窗口"; return false; }
        foreach (IntPtr self in selfWindows)
        {
            if (self != IntPtr.Zero && self == hwnd) { reason = "前台是本程序自己的窗口"; return false; }
        }

        string cls = WindowUtil.ClassOf(hwnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow")
        {
            reason = $"前台是系统窗口({cls})";
            return false;
        }

        var rect = WindowUtil.GetScreenRect(hwnd);
        var monitor = WindowUtil.GetMonitorRect(hwnd);
        bool covers = CoversMonitor(rect, monitor, tolerancePx);
        reason = $"{cls} {rect} vs 显示器 {monitor} → {(covers ? "全屏" : "非全屏")}";
        return covers;
    }

    private void Poll()
    {
        PollCount++;
        if (!Enabled)
        {
            if (_hidden) Apply(false, "检测已关闭，恢复显示");
            return;
        }

        IntPtr foreground = NativeMethods.GetForegroundWindow();
        bool fullscreen = Qualifies(foreground, _selfWindows(), 2, out string reason);

        if (fullscreen)
        {
            _fullscreenStreak++;
            if (_fullscreenStreak >= EnterStreak) Apply(true, $"{reason}（连续 {_fullscreenStreak} 次）");
            else LastReason = $"{reason}（等待去抖 {_fullscreenStreak}/{EnterStreak}）";
            return;
        }

        _fullscreenStreak = 0;
        Apply(false, reason);
    }

    /// <summary>真正切换隐藏/恢复（自检也直接调用这条路径）。</summary>
    public void Apply(bool hidden, string reason)
    {
        LastReason = reason;
        if (hidden == _hidden) return;
        _hidden = hidden;
        Log.Info($"全屏自动隐藏: {(hidden ? "隐藏侧边栏与主界面" : "恢复显示")} — {reason}");
        FullscreenChanged?.Invoke(hidden);
    }

    /// <summary>自检用：直接驱动切换。</summary>
    public void SetHiddenForTest(bool hidden, string reason = "自检") => Apply(hidden, reason);
}
