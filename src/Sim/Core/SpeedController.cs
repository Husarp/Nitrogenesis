namespace Nitrogenesis.Sim.Core;

/// <summary>
/// The speed controller (PLAN §4): <c>ticksDue += realDelta × 60 × speed</c>; the simulation runs the whole
/// ticks of <see cref="TicksDue"/> (within a frame budget) and hands them back with <see cref="Consume"/>; the
/// fractional rest is the render alpha.
/// </summary>
/// <remarks>
/// <para>Speed steps 0.01 … 100 plus MAX (index <see cref="MaxIndex"/>). MAX asks for as many ticks as the frame
/// budget allows. Pause drops the whole ticks still owed (a sim that fell behind does not run its backlog while
/// paused) and keeps the fraction (so an interpolated frame stays where it is); <see cref="StepOnce"/> adds exactly
/// one tick while paused.</para>
/// <para>The backlog is capped (<see cref="MaxBacklogSeconds"/> of game time at the target speed, at least 2 ticks),
/// so a sim that cannot keep up, or a long frame, never makes it race ahead afterwards: the speed simply drops,
/// which <see cref="SpeedMeter"/> shows as the actual speed.</para>
/// <para>Not thread-safe: the owner locks.</para>
/// </remarks>
public sealed class SpeedController
{
    public const int TicksPerSecond = 60;
    /// <summary>Simulation work per frame while the view is on (PLAN §4: 14 ms).</summary>
    public const double FrameBudgetSeconds = 0.014;
    /// <summary>Most game time that can pile up when the sim falls behind, in seconds at the target speed.</summary>
    public const double MaxBacklogSeconds = 0.25;

    private static readonly double[] s_steps = [0.01, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 25, 50, 100];

    /// <summary>Number of finite steps; the index equal to this is MAX.</summary>
    public static int StepCount => s_steps.Length;
    public static int MaxIndex => s_steps.Length;
    /// <summary>Index of 1×.</summary>
    public static int DefaultIndex => 5;

    /// <summary>The speed of step <paramref name="index"/>; +∞ for MAX.</summary>
    public static double StepValue(int index) => index >= s_steps.Length ? double.PositiveInfinity : s_steps[Math.Max(index, 0)];

    /// <summary>The step closest to <paramref name="speed"/> (∞ or anything above 100 = MAX).</summary>
    public static int IndexFor(double speed)
    {
        if (double.IsNaN(speed)) return DefaultIndex;
        if (speed > s_steps[^1]) return MaxIndex;
        int best = 0;
        for (int i = 1; i < s_steps.Length; i++)
            if (Math.Abs(Math.Log(s_steps[i] / speed)) < Math.Abs(Math.Log(s_steps[best] / speed))) best = i;
        return best;
    }

    /// <summary>Speed label: "0.01×", "1×", "100×", "MAX".</summary>
    public static string Label(double speed) =>
        double.IsPositiveInfinity(speed) ? "MAX" : speed.ToString(speed < 1 ? "0.##" : "0.#", System.Globalization.CultureInfo.InvariantCulture) + "×";

    private int _index = DefaultIndex;

    public int Index
    {
        get => _index;
        set
        {
            int index = Math.Clamp(value, 0, MaxIndex);
            if (index == _index) return;
            if (_index == MaxIndex) TicksDue = 0; // leaving MAX: drop the endless request, start a fresh accumulation
            _index = index;
            if (IsMax) TicksDue = double.PositiveInfinity;
            else TicksDue = Math.Min(TicksDue, BacklogCap);
        }
    }

    public bool IsMax => _index == MaxIndex;

    /// <summary>The chosen speed (+∞ for MAX).</summary>
    public double TargetSpeed => StepValue(_index);

    private bool _paused;

    /// <summary>Paused: no game time is added. Pausing drops the whole ticks still owed and keeps the render fraction.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            if (value && !_paused && double.IsFinite(TicksDue)) TicksDue -= Math.Floor(TicksDue);
            _paused = value;
        }
    }

    /// <summary>Game ticks owed to the simulation, whole part plus the render fraction (+∞ at MAX).</summary>
    public double TicksDue { get; private set; }

    /// <summary>Whole ticks the simulation should run now (int.MaxValue at MAX unless paused).</summary>
    public int WholeTicks => double.IsPositiveInfinity(TicksDue)
        ? (Paused ? 0 : int.MaxValue)
        : (int)Math.Min(Math.Floor(TicksDue), int.MaxValue);

    /// <summary>
    /// The render alpha (PLAN §4): <see cref="TicksDue"/> clamped to 0…1, which is its fraction once the whole ticks
    /// are consumed. While whole ticks are still owed the snapshot pair is stale, so the alpha is 1 (draw curr)
    /// rather than the fraction (which would draw the car up to a tick back). 1 at MAX.
    /// </summary>
    public float Alpha => (float)Math.Clamp(TicksDue, 0, 1);

    /// <summary>Whether the view interpolates between prev and curr (at 1× and slower; above, only curr is drawn).</summary>
    public bool Interpolates => !IsMax && TargetSpeed <= 1;

    private double BacklogCap => Math.Max(2, TicksPerSecond * TargetSpeed * MaxBacklogSeconds);

    public void Faster() => Index = _index + 1;
    public void Slower() => Index = _index - 1;

    /// <summary>Adds the game time of <paramref name="realSeconds"/> of real time (nothing while paused).</summary>
    public void Advance(double realSeconds)
    {
        if (Paused || !(realSeconds > 0)) return;
        if (IsMax)
        {
            TicksDue = double.PositiveInfinity;
            return;
        }
        TicksDue = Math.Min(TicksDue + realSeconds * TicksPerSecond * TargetSpeed, Math.Max(BacklogCap, TicksDue));
    }

    /// <summary>One more tick while paused (the "." key). Ignored when not paused.</summary>
    public void StepOnce()
    {
        if (!Paused) return;
        if (double.IsPositiveInfinity(TicksDue)) TicksDue = 0;
        TicksDue += 1;
    }

    /// <summary>
    /// The simulation ran <paramref name="ticks"/> of the due ticks. More than were due (ticks already running when
    /// <see cref="Paused"/> dropped them) keeps the render fraction.
    /// </summary>
    public void Consume(int ticks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        if (double.IsPositiveInfinity(TicksDue)) return; // MAX asks for everything
        double left = TicksDue - ticks;
        TicksDue = left >= 0 ? left : Math.Max(0, TicksDue - Math.Floor(TicksDue));
    }
}

/// <summary>
/// Measures the actual speed (ticks run per real second ÷ 60), so the HUD can show "100× (actual 64×)" (PLAN §4).
/// A window lasts at least half a second and long enough for <see cref="MinWindowTicks"/> ticks at the target speed,
/// so whole ticks do not make an on-time slow speed read low (0.05× is 1.5 ticks per half second). A new target speed
/// starts a fresh window. Not thread-safe: the owner locks.
/// </summary>
public sealed class SpeedMeter
{
    public const double WindowSeconds = 0.5;
    /// <summary>Ticks a window holds at the target speed, at least (±1 tick is then within ±5 %).</summary>
    public const int MinWindowTicks = 20;

    private long _ticks;
    private double _seconds;
    private double _target = double.NaN;

    /// <summary>The last measured speed (0 while paused or before the first window).</summary>
    public double Actual { get; private set; }

    /// <summary>Ticks the simulation ran.</summary>
    public void AddTicks(int ticks) => _ticks += ticks;

    /// <summary>The window length at <paramref name="targetSpeed"/>, seconds.</summary>
    public static double Window(double targetSpeed) =>
        targetSpeed > 0 ? Math.Max(WindowSeconds, MinWindowTicks / (SpeedController.TicksPerSecond * targetSpeed)) : WindowSeconds;

    /// <summary>
    /// Real time that passed at <paramref name="targetSpeed"/>. While paused, or when the target changed, the meter
    /// reads 0 and starts a fresh window.
    /// </summary>
    public void AddTime(double realSeconds, bool paused, double targetSpeed)
    {
        if (paused || !targetSpeed.Equals(_target))
        {
            Actual = 0;
            _ticks = 0;
            _seconds = 0;
            _target = paused ? double.NaN : targetSpeed;
            if (paused) return;
        }
        if (realSeconds > 0) _seconds += realSeconds;
        if (_seconds >= Window(targetSpeed))
        {
            Actual = _ticks / (_seconds * SpeedController.TicksPerSecond);
            _ticks = 0;
            _seconds = 0;
        }
    }
}
