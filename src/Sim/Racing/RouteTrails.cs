namespace Nitrogenesis.Sim.Racing;

/// <summary>
/// Every car's route in the running generation, for the view's route trail (PLAN §6.2 "route trail for followed
/// cars"): the car centre at the start and after every <see cref="Stride"/>-th tick. Written by
/// <see cref="RacingMode"/> while it steps (each car only by the worker that owns it), read when the workers are idle.
/// </summary>
/// <remarks>
/// The stride is the smallest power of two that keeps a whole generation within
/// <see cref="MaxSamplesPerCar"/> samples, so memory stays at most cars × 16 KB whatever the time limit (a 60 s limit
/// gives stride 2: a sample every 1/30 s, at most 0.6 cell apart at the default top speed). Only reads car state:
/// turning trails on cannot change a result. Nothing allocates after construction.
/// </remarks>
public sealed class RouteTrails
{
    public const int MaxSamplesPerCar = 2048;

    private readonly float[] _x, _y;
    private readonly int _shift, _mask;

    /// <param name="agents">Number of cars.</param>
    /// <param name="timeLimitTicks">Generation time limit; no car runs more ticks than this.</param>
    public RouteTrails(int agents, int timeLimitTicks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(agents, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeLimitTicks, 1);
        int shift = 0;
        while ((timeLimitTicks >> shift) + 1 > MaxSamplesPerCar) shift++;
        _shift = shift;
        _mask = (1 << shift) - 1;
        Agents = agents;
        SamplesPerCar = (timeLimitTicks >> shift) + 1;
        _x = new float[agents * SamplesPerCar];
        _y = new float[agents * SamplesPerCar];
    }

    public int Agents { get; }

    /// <summary>Ticks between two samples (a power of two).</summary>
    public int Stride => 1 << _shift;

    /// <summary>Room per car: one sample for the start plus one per stride up to the time limit.</summary>
    public int SamplesPerCar { get; }

    /// <summary>Samples a car has after running <paramref name="ticks"/> ticks (the start included).</summary>
    public int Count(int ticks) => Math.Min((ticks >> _shift) + 1, SamplesPerCar);

    /// <summary>The start position (sample 0).</summary>
    internal void Start(int agent, float x, float y)
    {
        int o = agent * SamplesPerCar;
        _x[o] = x;
        _y[o] = y;
    }

    /// <summary>The position after tick <paramref name="tick"/> (1-based); kept when the tick is a multiple of the stride.</summary>
    internal void Write(int agent, int tick, float x, float y)
    {
        if ((tick & _mask) != 0) return;
        int s = tick >> _shift;
        if (s >= SamplesPerCar) return;
        int o = agent * SamplesPerCar + s;
        _x[o] = x;
        _y[o] = y;
    }

    /// <summary>
    /// Copies the first <paramref name="count"/> samples of a car (see <see cref="Count"/>) into the caller's
    /// arrays. Returns the number copied. Does not allocate.
    /// </summary>
    public int CopyTo(int agent, int count, float[] x, float[] y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(agent);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(agent, Agents);
        count = Math.Min(Math.Min(count, SamplesPerCar), Math.Min(x.Length, y.Length));
        if (count <= 0) return 0;
        Array.Copy(_x, agent * SamplesPerCar, x, 0, count);
        Array.Copy(_y, agent * SamplesPerCar, y, 0, count);
        return count;
    }
}
