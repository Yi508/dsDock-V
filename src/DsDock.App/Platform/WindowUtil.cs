using System;
using System.Collections.Generic;
using System.Text;

namespace DsDock.Platform;

/// <summary>
/// Window helpers shared by every window in the app: styles, screen geometry, cursor polling,
/// pixel sampling. (The M0 desktop-layer mounting code was dropped: this app is fully topmost.)
/// </summary>
internal static class WindowUtil
{
    public static string ClassOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var sb = new StringBuilder(256);
        int n = NativeMethods.GetClassNameW(hwnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString() : "";
    }

    public static string Describe(IntPtr hwnd)
        => hwnd == IntPtr.Zero ? "0x0" : $"0x{hwnd.ToInt64():X} [{ClassOf(hwnd)}]";

    public static uint ProcessOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 0;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    public static long GetStyle(IntPtr hwnd) => NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();

    public static long GetExStyle(IntPtr hwnd) => NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();

    public static void SetStyle(IntPtr hwnd, long style)
        => NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, new IntPtr(style));

    public static void SetExStyle(IntPtr hwnd, long exStyle)
        => NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

    public static string FlagsOf(long style, long ex)
    {
        var parts = new List<string>();
        if ((style & NativeMethods.WS_CHILD) != 0) parts.Add("WS_CHILD");
        if ((ex & NativeMethods.WS_EX_TOPMOST) != 0) parts.Add("WS_EX_TOPMOST");
        if ((ex & NativeMethods.WS_EX_LAYERED) != 0) parts.Add("WS_EX_LAYERED");
        if ((ex & NativeMethods.WS_EX_NOACTIVATE) != 0) parts.Add("WS_EX_NOACTIVATE");
        if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0) parts.Add("WS_EX_TOOLWINDOW");
        if ((ex & NativeMethods.WS_EX_APPWINDOW) != 0) parts.Add("WS_EX_APPWINDOW");
        if ((ex & NativeMethods.WS_EX_TRANSPARENT) != 0) parts.Add("WS_EX_TRANSPARENT");
        return parts.Count == 0 ? "(none)" : string.Join("|", parts);
    }

    public static NativeMethods.RECT GetScreenRect(IntPtr hwnd)
    {
        NativeMethods.GetWindowRect(hwnd, out var r);
        return r;
    }

    /// <summary>Work area (taskbar excluded) of the monitor the window is on.</summary>
    public static NativeMethods.RECT GetWorkArea(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        var rect = GetScreenRect(hwnd);
        return GetWorkAreaForRect(rect);
    }

    public static NativeMethods.RECT GetWorkAreaForRect(NativeMethods.RECT rect)
    {
        IntPtr monitor = NativeMethods.MonitorFromRect(ref rect, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfoW(monitor, ref info))
            return info.rcWork;
        return new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    public static NativeMethods.RECT GetMonitorRect(IntPtr hwnd)
    {
        var rect = GetScreenRect(hwnd);
        IntPtr monitor = NativeMethods.MonitorFromRect(ref rect, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfoW(monitor, ref info))
            return info.rcMonitor;
        return new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    public static string MonitorId(IntPtr hwnd)
    {
        var rect = GetScreenRect(hwnd);
        IntPtr monitor = NativeMethods.MonitorFromRect(ref rect, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return monitor == IntPtr.Zero ? "unknown" : $"0x{monitor.ToInt64():X}";
    }

    public static void SetRect(IntPtr hwnd, NativeMethods.RECT rect, bool topmost = false)
    {
        NativeMethods.SetWindowPos(hwnd, topmost ? NativeMethods.HWND_TOPMOST : IntPtr.Zero,
            rect.Left, rect.Top, rect.Width, rect.Height,
            topmost
                ? NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW
                : NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>Walks the parent chain looking for <paramref name="target"/>.</summary>
    public static bool IsInChain(IntPtr from, IntPtr target)
    {
        IntPtr cur = from;
        for (int i = 0; i < 32 && cur != IntPtr.Zero; i++)
        {
            if (cur == target) return true;
            cur = NativeMethods.GetParent(cur);
        }
        return false;
    }

    public static string ChainOf(IntPtr from)
    {
        var parts = new List<string>();
        IntPtr cur = from;
        for (int i = 0; i < 32 && cur != IntPtr.Zero; i++)
        {
            parts.Add(Describe(cur));
            cur = NativeMethods.GetParent(cur);
        }
        return string.Join(" <- ", parts);
    }

    public static void SendWinD()
    {
        NativeMethods.keybd_event(NativeMethods.VK_LWIN, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(30);
        NativeMethods.keybd_event(0x44, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(30);
        NativeMethods.keybd_event(0x44, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
        System.Threading.Thread.Sleep(30);
        NativeMethods.keybd_event(NativeMethods.VK_LWIN, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static uint GetPixelAtScreen(int x, int y)
    {
        IntPtr dc = NativeMethods.GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return 0xFFFFFFFF;
        try { return NativeMethods.GetPixel(dc, x, y); }
        finally { NativeMethods.ReleaseDC(IntPtr.Zero, dc); }
    }

    public static string ColorRefToString(uint colorRef)
    {
        if (colorRef == 0xFFFFFFFF) return "CLR_INVALID";
        int r = (int)(colorRef & 0xFF);
        int g = (int)((colorRef >> 8) & 0xFF);
        int b = (int)((colorRef >> 16) & 0xFF);
        return $"#{r:X2}{g:X2}{b:X2}";
    }
}
