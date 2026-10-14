using System.Diagnostics;
using System.Windows.Media;

namespace GlassDesk.Dock;

/// <summary>Run hover motion once per composed frame, only while a target is changing.</summary>
internal sealed class DockRenderLoop(Func<double, bool> advance)
{
    /// <summary>60Hz 的帧周期，量不到时用它兜底。</summary>
    private static readonly TimeSpan FallbackInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>实测帧间隔的可接受区间：300Hz（3.3ms）到 60Hz（16.7ms）。</summary>
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(3);
    private static readonly TimeSpan MaximumInterval = TimeSpan.FromMilliseconds(17);

    /// <summary>
    /// 超过这个间隔就不当作刷新周期，而是丢帧或卡顿，不予采信。
    /// </summary>
    private static readonly TimeSpan StallThreshold = TimeSpan.FromMilliseconds(40);

    private bool _active;
    private long _timestamp;
    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;

    /// <summary>
    /// 最近一次实际呈现间隔；没量到过时为 null。
    /// </summary>
    /// <remarks>
    /// 动画本身跑在 <see cref="CompositionTarget.Rendering"/> 上，天然跟着合成器帧率走；
    /// 但轮询用的 <c>DispatcherTimer</c> 需要知道周期——60Hz 是 16.7ms，300Hz 只有 3.3ms。
    /// 写死 16ms 在高刷屏上会让「已经停下」的判定迟一到几拍，在低刷屏上又可能比帧还密。
    ///
    /// <para>
    /// 这里直接量合成器交给我们的相邻帧时间：不用去问显示器，也就不必处理多屏、
    /// 热插拔与每条输出线各自刷新率这些麻烦事。丢帧（间隔过大）不采信，
    /// 否则一次卡顿会被当成「这台机器就是 30Hz」。
    /// </para>
    /// </remarks>
    internal static TimeSpan? MeasuredFrameInterval { get; private set; }

    /// <summary>轮询节拍：量到过就跟着显示器的刷新周期走，否则按 60Hz 兜底。</summary>
    internal static TimeSpan PollInterval => MeasuredFrameInterval ?? FallbackInterval;

    internal void Start()
    {
        if (_active) return;
        _active = true;
        _timestamp = Stopwatch.GetTimestamp();
        _lastRenderingTime = TimeSpan.MinValue;
        CompositionTarget.Rendering += OnRendering;
    }

    internal void Stop()
    {
        if (!_active) return;
        CompositionTarget.Rendering -= OnRendering;
        _active = false;
        _timestamp = 0;
        _lastRenderingTime = TimeSpan.MinValue;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs rendering)
        {
            if (rendering.RenderingTime == _lastRenderingTime) return;

            if (_lastRenderingTime != TimeSpan.MinValue)
            {
                var interval = rendering.RenderingTime - _lastRenderingTime;

                if (interval > TimeSpan.Zero && interval < StallThreshold)
                {
                    MeasuredFrameInterval = interval < MinimumInterval ? MinimumInterval
                        : interval > MaximumInterval ? MaximumInterval
                        : interval;
                }
            }

            _lastRenderingTime = rendering.RenderingTime;
        }
        var now = Stopwatch.GetTimestamp();
        var deltaSeconds = Math.Clamp(Stopwatch.GetElapsedTime(_timestamp, now).TotalSeconds, 0, .1);
        _timestamp = now;
        if (advance(deltaSeconds)) Stop();
    }
}
