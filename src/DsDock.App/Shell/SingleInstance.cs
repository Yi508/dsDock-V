using System;
using System.Threading;
using DsDock.Diagnostics;
using DsDock.Platform;

namespace DsDock.Shell;

/// <summary>单实例：命名 Mutex + 广播激活消息给已有实例。</summary>
internal static class SingleInstance
{
    public const string ActivateMessageName = "DsDock.Activate.v01";

    private static Mutex? _mutex;

    /// <summary>
    /// 获取单实例锁。restartWait=true 时先等旧实例退出（重启自身时的竞态：
    /// 新进程比旧进程先跑起来的话，会撞上互斥锁然后立刻退出，看起来就是"没有重启"）。
    /// </summary>
    public static bool TryAcquire(bool restartWait = false)
    {
        int attempts = restartWait ? 30 : 1;
        for (int i = 0; i < attempts; i++)
        {
            _mutex = new Mutex(true, @"Local\DsDock.SingleInstance.v01", out bool created);
            if (created) return true;

            _mutex.Dispose();
            _mutex = null;
            if (i == 0)
            {
                // 普通第二次启动：立即叫出已有实例
                uint message = NativeMethods.RegisterWindowMessageW(ActivateMessageName);
                NativeMethods.PostMessageW(NativeMethods.HWND_BROADCAST, message, IntPtr.Zero, IntPtr.Zero);
                if (!restartWait)
                {
                    Log.Info("已有实例在运行：已广播激活消息，本进程退出");
                    return false;
                }
                Log.Info("重启等待：旧实例仍在运行，等待其退出…");
            }
            Thread.Sleep(300);
        }

        Log.Info($"等待旧实例退出超时（{attempts * 300}ms），本进程退出");
        return false;
    }
}
