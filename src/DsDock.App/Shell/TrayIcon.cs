using System;
using System.Runtime.InteropServices;
using DsDock.Diagnostics;
using DsDock.Platform;

namespace DsDock.Shell;

/// <summary>
/// Minimal tray icon built straight on Shell_NotifyIcon - the project must stay free of
/// third party NuGet packages (this environment has no reachable feed).
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    public const uint CallbackMessage = 0x8000 + 42; // WM_APP + 42

    /// <summary>Last NIM_ADD result; null when the tray was not used in this run.</summary>
    public static bool? LastInstallResult { get; private set; }

    /// <summary>NIM_ADD 次数（自检用来确认资源管理器重启后确实重新注册了）。</summary>
    public static int InstallCount { get; private set; }

    private const uint NIF_INFO = 0x10;
    private const uint NIIF_INFO = 0x1;
    private const uint NIM_SETVERSION = 0x4;
    private const uint NotifyIconVersion4 = 4;

    /// <summary>Last balloon result; null when no balloon was attempted.</summary>
    public bool? LastBalloonResult { get; private set; }

    /// <summary>Last balloon text (kept even when the OS call is refused, e.g. under UIPI).</summary>
    public string? LastBalloonText { get; private set; }

    /// <summary>图标句柄（自检用：非 0 说明图标资源本身没问题）。</summary>
    public static IntPtr LastIconHandle { get; private set; }

    public static string LastIconSource { get; private set; } = "";

    /// <summary>NIM_ADD 失败时的 Win32 错误码（0 = 成功）。</summary>
    public static int LastInstallError { get; private set; }

    /// <summary>
    /// 解析托盘回调事件：
    /// NOTIFYICON_VERSION_4 起，消息 id 固定为 CallbackMessage，**事件在 wParam 低字**（NIN_SELECT / WM_CONTEXTMENU）；
    /// 更早的版本同样是 wParam 低字装鼠标消息（WM_LBUTTONUP / WM_RBUTTONUP）。
    /// 所以一律取 wParam 低字，拿不到才退回消息 id。
    /// </summary>
    /// <summary>NIM_SETVERSION(4) 是否成功。成功后回调布局变了，必须按版本区分（否则解析会错）。</summary>
    public static bool Version4 { get; private set; }

    /// <summary>
    /// 解析托盘回调事件。两种协议的区别（实测日志确认）：
    ///   v4   ：wParam = 锚点坐标（低字 x、高字 y），lParam 低字 = 事件（NIN_* / WM_CONTEXTMENU），高字 = 图标 id
    ///   旧版 ：wParam 低字 = 鼠标消息（WM_LBUTTONUP 等），lParam = 坐标点
    /// 所以必须按"是否 v4"来取，不能靠猜：旧版 lParam 的 x 坐标低字可能正好等于 NIN_* 的值。
    /// </summary>
    public static uint ResolveEventId(uint message, IntPtr wParam, IntPtr lParam)
        => ResolveEventId(message, wParam, lParam, Version4);

    public static uint ResolveEventId(uint message, IntPtr wParam, IntPtr lParam, bool version4)
    {
        if (message != CallbackMessage) return message;
        return version4
            ? (uint)(lParam.ToInt64() & 0xFFFF)
            : (uint)(wParam.ToInt64() & 0xFFFF);
    }

    private NativeMethods.NOTIFYICONDATA _data;
    private IntPtr _hwnd = IntPtr.Zero;
    private bool _installed;

    /// <summary>通过 Windows 系统通知（托盘气泡）提示用户。spec：卡片加载失败要提示。</summary>
    public bool ShowBalloon(string title, string text)
    {
        LastBalloonText = title + " / " + text;
        if (!_installed)
        {
            LastBalloonResult = false;
            Log.Info("气泡未发送（托盘未安装）: " + LastBalloonText);
            return false;
        }

        _data.uFlags = NIF_INFO;
        _data.szInfoTitle = title.Length > 63 ? title.Substring(0, 63) : title;
        _data.szInfo = text.Length > 255 ? text.Substring(0, 255) : text;
        _data.dwInfoFlags = NIIF_INFO;
        _data.uVersion = 0;
        LastBalloonResult = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref _data);
        Log.Info($"系统气泡: {LastBalloonText} → {LastBalloonResult}");
        return LastBalloonResult == true;
    }

    public void Install(IntPtr hwnd, string tip)
    {
        _hwnd = hwnd;
        _data = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = CallbackMessage,
            hIcon = ResolveIcon(),
            szTip = tip,
            szInfo = "",
            szInfoTitle = "",
        };
        InstallCount++;
        _installed = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref _data);
        LastInstallResult = _installed;
        if (_installed)
        {
            // Win10/11 推荐：注册成功后声明使用 NOTIFYICON_VERSION_4 的行为
            _data.uVersion = NotifyIconVersion4;
            bool version4Ok = NativeMethods.Shell_NotifyIconW(NIM_SETVERSION, ref _data);
            Version4 = version4Ok;   // 回调布局随版本改变，必须记住（v4 事件在 lParam 低字）
            Log.Info($"NIM_SETVERSION({NotifyIconVersion4}) = {version4Ok}");
            _data.uVersion = 0;
        }
        LastInstallError = _installed ? 0 : Marshal.GetLastWin32Error();
        Log.Info($"托盘图标安装: {_installed}（hIcon=0x{LastIconHandle.ToInt64():X} 来源={LastIconSource} " +
                 $"hWnd=0x{hwnd.ToInt64():X} cbSize={_data.cbSize} err={LastInstallError}）");
    }

    /// <summary>图标资源：系统自带应用图标（与技术验证阶段一致）。返回 0 说明连系统图标都拿不到。</summary>
    private static IntPtr ResolveIcon()
    {
        // 优先用随程序发布的 tray.ico（多尺寸 ICO，黑底已做透明）
        try
        {
            string file = System.IO.Path.Combine(AppContext.BaseDirectory, "tray.ico");
            if (System.IO.File.Exists(file))
            {
                int size = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON);
                if (size <= 0) size = 16;
                IntPtr fromFile = NativeMethods.LoadImageW(IntPtr.Zero, file, NativeMethods.IMAGE_ICON, size, size,
                    NativeMethods.LR_LOADFROMFILE);
                if (fromFile != IntPtr.Zero)
                {
                    LastIconHandle = fromFile;
                    LastIconSource = $"tray.ico（文件 {size}px）";
                    return fromFile;
                }
            }
        }
        catch
        {
            // 落到系统图标
        }

        IntPtr icon = NativeMethods.LoadIconW(IntPtr.Zero, new IntPtr(NativeMethods.IDI_APPLICATION));
        LastIconHandle = icon;
        LastIconSource = icon != IntPtr.Zero ? "IDI_APPLICATION（系统默认，tray.ico 不可用）" : "（系统图标也拿不到）";
        return icon;
    }

    public void UpdateTip(string tip)
    {
        if (!_installed) return;
        _data.uFlags = NativeMethods.NIF_TIP;
        _data.szTip = tip;
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref _data);
    }

    /// <summary>Returns the chosen command id: 1 = 显示/隐藏, 2 = 退出, 0 = nothing.</summary>
    public int ShowMenu(bool currentlyVisible)
    {
        IntPtr menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero) return 0;
        try
        {
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, new IntPtr(1),
                currentlyVisible ? "隐藏主界面" : "显示主界面");
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, null);
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, new IntPtr(2), "退出应用");

            NativeMethods.GetCursorPos(out var pt);
            NativeMethods.SetForegroundWindow(_hwnd);
            int cmd = NativeMethods.TrackPopupMenu(menu,
                NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_NONOTIFY,
                pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
            NativeMethods.PostMessageW(_hwnd, 0x0000, IntPtr.Zero, IntPtr.Zero); // WM_NULL, classic fix
            return cmd;
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (!_installed) return;
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref _data);
        _installed = false;
    }
}
