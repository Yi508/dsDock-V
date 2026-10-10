using System;
using System.Linq;
using System.Runtime.InteropServices;
using DsDock.Diagnostics;

namespace DsDock.Shell;

/// <summary>
/// 全局底层鼠标钩子（WH_MOUSE_LL）：检测"本应用之外的点击"，供"点击外部自动收纳"使用。
/// 为什么不能用 WM_ACTIVATEAPP（失焦事件）：面板从侧边栏展开时不会获得焦点（侧边栏 WS_EX_NOACTIVATE），
/// 本应用从未激活过 → 没有"失焦转移"可言，必须直接监听鼠标按下并判断目标窗口归属。
/// 约定：本类只通知"点在了别处"，是否收纳（开关/已展开/全屏让位）由宿主判断；钩子回调内必须快速返回。
/// </summary>
internal sealed class OutsideClickWatcher : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private static readonly int[] ButtonDown = { 0x0201, 0x0204, 0x0207, 0x020B };   // 左/右/中/X 下按

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    private static readonly LowLevelProc Proc = Callback;
    private static OutsideClickWatcher? _current;
    private IntPtr _hook;

    /// <summary>点在了本应用之外（参数 = 点击坐标；处理器用它做归属判定，不依赖真实光标）。</summary>
    public event Action<int, int>? OutsideClickDetected;

    public bool Installed { get; private set; }
    public string? InstallError { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Pt { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct { public Pt Pt; public uint Flags; public uint Time; public IntPtr Extra; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelProc fn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Pt point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

    /// <summary>
    /// 点是否落在本应用任意**可见**窗口的矩形内（枚举本进程全部顶层窗口：主界面/侧边栏/设置/卡片库/弹窗/菜单）。
    /// 为什么不能只用 WindowFromPoint：主界面是 WPF 透明窗口，透明像素处 WindowFromPoint 会**穿透**到
    /// 桌面后面的窗口 → 点主界面内部被误判为"外部点击"。矩形判定与像素透明度无关，稳定可靠。
    /// </summary>
    public static bool IsPointInOurWindows(int x, int y)
    {
        bool hit = false;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);   // 注意：lambda 参数就叫 _，`_ =` 会被当成赋值而非丢弃
            if (pid != (uint)Environment.ProcessId) return true;
            if (GetWindowRect(h, out Rect r) && x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom)
            {
                hit = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return hit;
    }

    public void Start()
    {
        _current = this;
        try
        {
            _hook = SetWindowsHookExW(WH_MOUSE_LL, Proc, GetModuleHandleW(null), 0);
            Installed = _hook != IntPtr.Zero;
            InstallError = Installed ? null : $"SetWindowsHookEx 失败 err={Marshal.GetLastWin32Error()}";
        }
        catch (Exception ex)
        {
            Installed = false;
            InstallError = ex.Message;
        }
        Log.Info(Installed
            ? "外部点击监听已安装（WH_MOUSE_LL）"
            : $"外部点击监听安装失败: {InstallError}（自动收纳将退化为仅失焦路径）");
    }

    private static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // 钩子回调里绝不能抛异常，也不能久留（Windows 会移除超时的钩子）
        try
        {
            if (nCode >= 0 && Array.IndexOf(ButtonDown, (int)wParam.ToInt64()) >= 0)
            {
                var data = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                _current?.ProcessPoint(data.Pt.X, data.Pt.Y);
            }
        }
        catch { }
        return CallNextHookEx(_current?._hook ?? IntPtr.Zero, nCode, wParam, lParam);
    }

    public void ProcessPoint(int x, int y)
    {
        // 先做矩形判定：落在本应用任何窗口内（含透明像素区）→ 直接忽略；
        // 否则再按窗口归属（WindowFromPoint）判定
        if (IsPointInOurWindows(x, y)) return;
        OutsideClickDetected?.Invoke(x, y);   // 矩形判定为唯一机制，坐标随事件传递
    }

    /// <summary>按窗口归属判定（自检直接调用，与钩子同一路径）：外部窗口 → 触发通知。</summary>
    public void ProcessWindow(IntPtr hwnd)
    {
        // 已停用（矩形判定为唯一机制）：保留方法仅为兼容，不再触发事件
    }

    /// <summary>窗口是否属于其他进程（桌面/其它程序=true；本应用任意窗口=false）。</summary>
    public static bool IsForeignWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        return pid != 0 && pid != (uint)Environment.ProcessId;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            Installed = false;
        }
        if (_current == this) _current = null;
    }
}