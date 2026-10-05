using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace DsDock.Anim;

/// <summary>
/// 统一帧时钟：所有窗口/卡片动画都从这里取帧（默认 60 FPS，按 16.7ms 累加）。
/// One clock drives every animation so the frame budget stays observable.
/// Rendering can fire more than 60x/s; we consume it in fixed 1/30s steps (spec: 30 FPS).
/// </summary>
internal sealed class FrameClock
{
    public const double TargetFps = 60.0;
    public const double FrameMs = 1000.0 / TargetFps;

    private readonly List<Animation> _active = new();
    private TimeSpan _lastRender = TimeSpan.MinValue;
    private double _accumulator;
    private int _renderCallbacks;
    private long _tickBase;
    private DateTime _fpsWindow = DateTime.UtcNow;

    public bool Running { get; private set; }
    public double MeasuredRenderFps { get; private set; }
    public double MeasuredTickFps { get; private set; }
    public long TickCount { get; private set; }
    public int ActiveAnimations => _active.Count;
    public event Action? OnTick;

    public void Start()
    {
        if (Running) return;
        Running = true;
        CompositionTarget.Rendering += OnRendering;
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    public Animation Animate(double durationMs, Func<double, double> ease, Action<double> update, Action? done = null)
    {
        var a = new Animation(durationMs, ease, update, done);
        _active.Add(a);
        return a;
    }

    public void CancelAll() => _active.Clear();

    private void OnRendering(object? sender, EventArgs e)
    {
        _renderCallbacks++;

        TimeSpan now = (e as RenderingEventArgs)?.RenderingTime ?? TimeSpan.Zero;
        if (_lastRender == TimeSpan.MinValue)
        {
            _lastRender = now;
            return;
        }
        if (now == _lastRender) return; // WPF raises Rendering several times for the same frame

        double dt = (now - _lastRender).TotalMilliseconds;
        _lastRender = now;
        if (dt <= 0 || dt > 250) dt = FrameMs;

        _accumulator += dt;
        int guard = 0;
        while (_accumulator >= FrameMs && guard++ < 8)
        {
            _accumulator -= FrameMs;
            Step(FrameMs);
        }

        double seconds = (DateTime.UtcNow - _fpsWindow).TotalSeconds;
        if (seconds >= 0.5)
        {
            MeasuredRenderFps = _renderCallbacks / seconds;
            MeasuredTickFps = (TickCount - _tickBase) / seconds;
            _tickBase = TickCount;
            _renderCallbacks = 0;
            _fpsWindow = DateTime.UtcNow;
        }

        OnTick?.Invoke();
    }

    private void Step(double ms)
    {
        TickCount++;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            if (_active[i].Step(ms))
                _active.RemoveAt(i);
        }
    }
}

internal sealed class Animation
{
    private readonly double _duration;
    private readonly Func<double, double> _ease;
    private readonly Action<double> _update;
    private readonly Action? _done;
    private double _elapsed;

    internal Animation(double durationMs, Func<double, double> ease, Action<double> update, Action? done)
    {
        _duration = Math.Max(1.0, durationMs);
        _ease = ease;
        _update = update;
        _done = done;
    }

    internal bool Step(double ms)
    {
        _elapsed += ms;
        double t = Math.Clamp(_elapsed / _duration, 0.0, 1.0);
        _update(_ease(t));
        if (t >= 1.0)
        {
            _done?.Invoke();
            return true;
        }
        return false;
    }
}

internal static class Ease
{
    public static double Linear(double t) => t;

    public static double CubicOut(double t)
    {
        double p = 1.0 - t;
        return 1.0 - p * p * p;
    }

    public static double CubicInOut(double t)
        => t < 0.5 ? 4.0 * t * t * t : 1.0 - Math.Pow(-2.0 * t + 2.0, 3) / 2.0;

    /// <summary>
/// 统一帧时钟：所有窗口/卡片动画都从这里取帧（默认 60 FPS，按 16.7ms 累加）。Slight overshoot - used for the "rebound" feedback required by the spec.</summary>
    public static double BackOut(double t)
    {
        const double c1 = 1.70158;
        const double c3 = c1 + 1.0;
        double p = t - 1.0;
        return 1.0 + c3 * p * p * p + c1 * p * p;
    }
}
