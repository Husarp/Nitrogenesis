using System;

namespace Nitrogenesis.View;

/// <summary>
/// Bytes the main thread allocates per frame (PLAN §9: "no GC pauses (zero-allocation check in a debug overlay)"):
/// the <see cref="GC.GetAllocatedBytesForCurrentThread"/> delta from one frame's <see cref="EndFrame"/> to the next,
/// so it covers the view's own work, drawing callbacks, input handlers and every other node in between.
/// Deliberate exceptions are taken out with <see cref="Exclude"/>: the HUD text (refreshed 10 times a second) and
/// the debug command line's own printing.
/// </summary>
public sealed class FrameAllocMeter
{
    private long _mark, _excluded, _max;
    private bool _started;
    private int _gc0, _gc1, _gc2;

    public FrameAllocMeter() => ResetCollections();

    /// <summary>Bytes allocated in the last whole frame (exclusions taken out).</summary>
    public long LastFrame { get; private set; }

    /// <summary>Garbage collections since the meter was made (or reset), per generation.</summary>
    public int Gen0 => GC.CollectionCount(0) - _gc0;
    public int Gen1 => GC.CollectionCount(1) - _gc1;
    public int Gen2 => GC.CollectionCount(2) - _gc2;

    public void ResetCollections()
    {
        _gc0 = GC.CollectionCount(0);
        _gc1 = GC.CollectionCount(1);
        _gc2 = GC.CollectionCount(2);
    }

    /// <summary>Bytes allocated on purpose during this frame that should not count (e.g. the HUD text).</summary>
    public void Exclude(long bytes) => _excluded += bytes;

    /// <summary>Call once at the end of the frame's work.</summary>
    public void EndFrame()
    {
        long now = GC.GetAllocatedBytesForCurrentThread();
        if (_started)
        {
            LastFrame = Math.Max(0, now - _mark - _excluded);
            _max = Math.Max(_max, LastFrame);
        }
        _excluded = 0;
        _started = true;
        _mark = GC.GetAllocatedBytesForCurrentThread();
    }

    /// <summary>The largest frame since the last call (for the HUD, a few times a second).</summary>
    public long TakeMax()
    {
        long max = _max;
        _max = 0;
        return max;
    }
}
