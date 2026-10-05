using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DsDock.Diagnostics;
using DsDock.Platform;

namespace DsDock.Shell;

/// <summary>
/// A hidden top level window used as the message sink. It must NOT be a child of the desktop:
/// it has to survive an explorer restart to receive TaskbarCreated and re-mount the container.
/// </summary>
internal sealed class MessageSink : IDisposable
{
    private readonly Window _window;
    private HwndSource? _source;

    public MessageSink()
    {
        _window = new Window
        {
            Width = 1,
            Height = 1,
            Left = -32000,
            Top = -32000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Title = "DsDock M0 MessageSink",
        };
        _window.Show();
        Handle = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(Handle);
        _source?.AddHook(WndProc);

        TaskbarCreatedMessage = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
        ActivateMessage = NativeMethods.RegisterWindowMessageW(SingleInstance.ActivateMessageName);
        Log.Info($"消息窗口: handle=0x{Handle.ToInt64():X} TaskbarCreated=0x{TaskbarCreatedMessage:X} Activate=0x{ActivateMessage:X}");
    }

    public IntPtr Handle { get; }
    public uint TaskbarCreatedMessage { get; }
    public uint ActivateMessage { get; }

    public event Action? TaskbarCreated;
    public event Action? Activated;
    public event Action<uint, IntPtr, IntPtr>? TrayMessage;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        uint m = unchecked((uint)msg);
        if (m == TaskbarCreatedMessage && TaskbarCreatedMessage != 0)
        {
            Log.Info("收到 TaskbarCreated（explorer 重启）");
            TaskbarCreated?.Invoke();
            handled = true;
        }
        else if (m == ActivateMessage && ActivateMessage != 0)
        {
            Log.Info("收到激活请求（第二实例或托盘）");
            Activated?.Invoke();
            handled = true;
        }
        else if (m == TrayIcon.CallbackMessage)
        {
            TrayMessage?.Invoke(TrayIcon.CallbackMessage, wParam, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source?.RemoveHook(WndProc);
        _source = null;
        try { _window.Close(); } catch { /* shutdown path */ }
    }
}
