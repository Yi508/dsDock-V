using System;
using DsDock.Anim;
using DsDock.Platform;

namespace DsDock.Windows;

/// <summary>
/// Animates a window rectangle on the shared 30 FPS clock using SetWindowPos only.
/// Content is never re-laid out during the animation, so cards do not deform while the
/// container grows out of / shrinks back into the sidebar.
/// </summary>
internal sealed class WindowAnimator
{
    private readonly FrameClock _clock;

    public WindowAnimator(FrameClock clock) => _clock = clock;

    public double LastDurationMs { get; private set; }
    public int LastTicks { get; private set; }
    public bool IsAnimating { get; private set; }
    private int _seq;

    public void Cancel()
    {
        _seq++;
        IsAnimating = false;
    }

    public void Set(IntPtr hwnd, NativeMethods.RECT rect)
        => NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

    public void Animate(IntPtr hwnd, NativeMethods.RECT from, NativeMethods.RECT to, double durationMs, Action? done = null)
    {
        if (hwnd == IntPtr.Zero) return;

        if (durationMs <= 0)
        {
            Set(hwnd, to);
            LastDurationMs = 0;
            LastTicks = 0;
            done?.Invoke();
            return;
        }

        // 只作废本 animator 的上一次动画，不用 CancelAll（会连带杀掉侧边栏悬浮等其它动画）
        _seq++;
        int seq = _seq;
        long baseTicks = _clock.TickCount;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        IsAnimating = true;

        _clock.Animate(durationMs, Ease.CubicOut, t =>
        {
            if (seq != _seq) return;
            Set(hwnd, Lerp(from, to, t));
        }, () =>
        {
            if (seq != _seq) return;
            Set(hwnd, to);
            watch.Stop();
            LastDurationMs = watch.Elapsed.TotalMilliseconds;
            LastTicks = (int)(_clock.TickCount - baseTicks);
            IsAnimating = false;
            done?.Invoke();
        });
    }

    public static NativeMethods.RECT Lerp(NativeMethods.RECT a, NativeMethods.RECT b, double t)
        => new()
        {
            Left = (int)Math.Round(a.Left + (b.Left - a.Left) * t),
            Top = (int)Math.Round(a.Top + (b.Top - a.Top) * t),
            Right = (int)Math.Round(a.Right + (b.Right - a.Right) * t),
            Bottom = (int)Math.Round(a.Bottom + (b.Bottom - a.Bottom) * t),
        };
}
