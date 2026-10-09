namespace Nitrogenesis.Sim.Racing;

/// <summary>Where a <see cref="PlayerRun"/> is.</summary>
public enum PlayerPhase : byte
{
    /// <summary>Waiting at the start; the car does not move.</summary>
    Countdown,
    Driving,
    Finished,
    /// <summary>Touched danger (or a wall in kill mode).</summary>
    Crashed,
}

/// <summary>
/// The player driving one car (PLAN §6.5, M2 part): the same <see cref="CarPhysics"/> as training, a countdown, a
/// timer, the finish time and an instant restart. One <see cref="Tick"/> is 1/60 s, like the simulation.
/// </summary>
/// <remarks>
/// <para>Finish time and crash follow <see cref="RacingMode"/> exactly (finish at sub-tick precision = ticks before
/// the finishing tick + the share of that tick), so a player's time compares with the cars' times. Unlike a
/// training car the player has no time limit and is never stopped for stalling.</para>
/// <para>The route is kept for drawing: the car centre every <see cref="TrailStride"/> ticks; when
/// <see cref="MaxTrail"/> points are used, every second point is dropped and the stride doubles, so any run fits.</para>
/// <para>Single-threaded; nothing allocates after construction.</para>
/// </remarks>
public sealed class PlayerRun
{
    public const int CountdownTicks = 3 * RacingSettings.TicksPerSecond;
    /// <summary>Countdown after R: one second ("instant restart" without a 3-second wait).</summary>
    public const int RestartCountdownTicks = RacingSettings.TicksPerSecond;
    public const int MaxTrail = 4096;

    private readonly float[] _trailX = new float[MaxTrail], _trailY = new float[MaxTrail];
    private float _bestDistance;

    public PlayerRun(RacingTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        Track = track;
        Physics = new CarPhysics(track.Grid, track.Clearance, track.Settings);
        Restart(CountdownTicks);
    }

    public RacingTrack Track { get; }
    public CarPhysics Physics { get; }
    public PlayerControls Controls { get; } = new();

    public PlayerPhase Phase { get; private set; }

    /// <summary>The car after the last tick, and before it (for interpolation).</summary>
    public CarState Car { get; private set; }
    public CarState PreviousCar { get; private set; }

    /// <summary>Countdown ticks left (0 once driving).</summary>
    public int CountdownLeft { get; private set; }

    /// <summary>Ticks driven since the start signal.</summary>
    public int Ticks { get; private set; }

    /// <summary>Finish time in ticks at sub-tick precision (valid when <see cref="Phase"/> is Finished).</summary>
    public float FinishTicks { get; private set; }

    /// <summary>Fastest finish since this run object was made (all restarts), ticks; +∞ before the first.</summary>
    public float BestFinishTicks { get; private set; } = float.PositiveInfinity;

    /// <summary>The last finish beat every earlier one.</summary>
    public bool NewBest { get; private set; }

    /// <summary>Finishes since this run object was made.</summary>
    public int Finishes { get; private set; }

    /// <summary>Runs started (the first one and each restart).</summary>
    public int Attempts { get; private set; }

    /// <summary>Best progress 0…1 along the track this run (distance field, like training; 1 once finished).</summary>
    public float Progress => Phase == PlayerPhase.Finished ? 1f : RacingFitness.ProgressFraction(_bestDistance, Track.StartDistance);

    /// <summary>The route so far (see the remarks); <see cref="TrailCount"/> points are valid.</summary>
    public float[] TrailX => _trailX;
    public float[] TrailY => _trailY;
    public int TrailCount { get; private set; }
    public int TrailStride { get; private set; }

    /// <summary>Back to the start with a countdown of <paramref name="countdownTicks"/> (0 = drive at once).</summary>
    public void Restart(int countdownTicks = RestartCountdownTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(countdownTicks);
        var start = Track.Track.Start;
        Car = new CarState(start.X, start.Y, Track.StartHeading);
        PreviousCar = Car;
        Controls.Reset();
        CountdownLeft = countdownTicks;
        Phase = countdownTicks > 0 ? PlayerPhase.Countdown : PlayerPhase.Driving;
        Ticks = 0;
        FinishTicks = 0f;
        NewBest = false;
        _bestDistance = Track.StartDistance;
        _trailX[0] = start.X;
        _trailY[0] = start.Y;
        TrailCount = 1;
        TrailStride = 1;
        Attempts++;
    }

    /// <summary>One tick with the player's input (see <see cref="PlayerControls.Tick"/>).</summary>
    public void Tick(int keyThrottle, int keySteer, float stickX, float rightTrigger, float leftTrigger)
    {
        PreviousCar = Car;
        switch (Phase)
        {
            case PlayerPhase.Countdown:
                // The controls already follow the keys, so a held throttle is fully on at the start signal.
                Controls.Tick(keyThrottle, keySteer, stickX, rightTrigger, leftTrigger);
                if (--CountdownLeft <= 0) Phase = PlayerPhase.Driving;
                return;
            case PlayerPhase.Driving:
                Controls.Tick(keyThrottle, keySteer, stickX, rightTrigger, leftTrigger);
                Drive(Controls.Throttle, Controls.Steer);
                return;
        }
    }

    /// <summary>One driving tick with throttle and steer given directly (the controls are bypassed).</summary>
    public void Drive(float throttle, float steer)
    {
        if (Phase != PlayerPhase.Driving) return;
        CarState car = Car;
        PreviousCar = car;
        TickResult result = Physics.Tick(ref car, throttle, steer);
        Car = car;
        int tickBefore = Ticks;
        Ticks = tickBefore + 1;
        AddTrail(car.X, car.Y);

        if (result.Outcome == TickOutcome.Crashed)
        {
            Phase = PlayerPhase.Crashed;
            return;
        }
        if (result.Outcome == TickOutcome.Finished)
        {
            FinishTicks = tickBefore + result.FinishFraction;
            Phase = PlayerPhase.Finished;
            Finishes++;
            NewBest = FinishTicks < BestFinishTicks;
            if (NewBest) BestFinishTicks = FinishTicks;
            return;
        }
        float d = Track.Field.Sample(car.X, car.Y);
        if (d < _bestDistance) _bestDistance = d;
    }

    /// <summary>The timer in seconds: running while driving (<paramref name="alpha"/> = the render fraction of a tick), else stopped.</summary>
    public float TimeSeconds(float alpha = 0f) => Phase switch
    {
        PlayerPhase.Countdown => 0f,
        PlayerPhase.Driving => (Ticks + Math.Clamp(alpha, 0f, 1f)) / RacingSettings.TicksPerSecond,
        PlayerPhase.Finished => FinishTicks / RacingSettings.TicksPerSecond,
        _ => (float)Ticks / RacingSettings.TicksPerSecond,
    };

    private void AddTrail(float x, float y)
    {
        if (Ticks % TrailStride != 0) return;
        if (TrailCount == MaxTrail)
        {
            // Keep the points at even multiples of the stride (ticks 0, 2s, 4s, …) and double it.
            for (int i = 0; i < MaxTrail / 2; i++)
            {
                _trailX[i] = _trailX[2 * i];
                _trailY[i] = _trailY[2 * i];
            }
            TrailCount = MaxTrail / 2;
            TrailStride *= 2;
            if (Ticks % TrailStride != 0) return;
        }
        _trailX[TrailCount] = x;
        _trailY[TrailCount] = y;
        TrailCount++;
    }
}
