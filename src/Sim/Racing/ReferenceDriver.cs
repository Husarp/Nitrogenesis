using Nitrogenesis.Sim.Brain;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.Map;

namespace Nitrogenesis.Sim.Racing;

/// <summary>
/// Scripted driver with no learning (PLAN §3.2). It follows the distance field downhill with the real car
/// physics and slows before sharp turns. Its finish time is the track's estimated time and the base of the
/// auto time limit; "finished" is the generator's and the editor's Test check that a car really completes it.
/// </summary>
/// <remarks>
/// <para>Each tick it walks the steepest-descent cell path ahead of the car, steers towards a look-ahead point on
/// it (pure pursuit), and caps its speed so it can brake down to a safe cornering speed for every bend ahead,
/// where the safe speed of a bend follows from its curvature and the car's turn rate.</para>
/// <para>The path comes from its own steering field: the racing field's graph (same passable cells, neighbours
/// and surface costs) with extra cost on cells whose centre is close to a wall. Passable cells reach to 0.9
/// cells from a wall (the car's half-width), so the plain field's shortest path hugs walls where the car centre
/// cannot quite go, and a car chasing it would scrape along every inside wall.</para>
/// <para>When its nose is jammed against a wall (the car barely moves although it asks for throttle, so the
/// physics refuses the turn), it backs off for a moment with the steering reversed, like a three-point turn.</para>
/// <para>It runs inside a one-car <see cref="RacingMode"/>, so finish, crash and stall rules are exactly
/// those of training. The run is capped at <see cref="MaxSeconds"/>.</para>
/// </remarks>
public static class ReferenceDriver
{
    /// <summary>Longest run before giving up, s.</summary>
    public const int MaxSeconds = 600;

    /// <summary>Outcome of a reference run.</summary>
    /// <param name="Finished">The car reached the finish.</param>
    /// <param name="TimeSeconds">Finish time (sub-tick), or the time the run stopped.</param>
    /// <param name="EndStatus">Final status (Finished, Crashed, Stalled or TimedOut).</param>
    /// <param name="Path">Car centre after every tick, starting with the start position.</param>
    public sealed record Result(bool Finished, float TimeSeconds, AgentStatus EndStatus, IReadOnlyList<(float X, float Y)> Path);

    public static Result Run(RacingTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var mode = new RacingMode(track, 1, MaxSeconds * RacingSettings.TicksPerSecond);
        var pilot = new Pilot(mode);
        var path = new List<(float, float)> { (track.Track.Start.X, track.Track.Start.Y) };
        var all = AgentRange.All(1);
        while (!mode.IsDone(0))
        {
            mode.Step(all, 1, pilot);
            CarState car = mode.Car(0);
            path.Add((car.X, car.Y));
        }
        AgentStatus status = mode.Status(0);
        bool finished = status == AgentStatus.Finished;
        float ticks = finished ? mode.FinishTicks(0) : mode.Tick(0);
        return new Result(finished, ticks / RacingSettings.TicksPerSecond, status, path);
    }

    /// <summary>The control law. Single-threaded: one pilot drives one car.</summary>
    private sealed class Pilot : IAgentPolicy
    {
        // Tuning (cells, seconds). Look-ahead grows with speed so the line stays smooth at speed.
        private const float LookMin = 3f, LookPerSpeed = 0.3f, LookMax = 9f;
        /// <summary>Path cells walked ahead each tick; enough to brake from the highest max speed.</summary>
        private const int MaxPath = 96;
        /// <summary>Path points either side used to measure a bend's curvature.</summary>
        private const int CurveSpan = 4;
        /// <summary>Use this share of the car's turn rate when choosing a cornering speed.</summary>
        private const float CornerSafety = 0.7f;
        /// <summary>Plan braking at this share of the brake deceleration.</summary>
        private const float BrakeShare = 0.6f;
        private const float MinCornerSpeed = 3f;
        /// <summary>Jammed: forward speed below this (cells/s) while asking for throttle …</summary>
        private const float JamSpeed = 0.5f;
        /// <summary>… for this many ticks in a row.</summary>
        private const int JamTicks = 20;
        /// <summary>Ticks to reverse once jammed.</summary>
        private const int ReverseTicks = 30;
        /// <summary>Steering field: cells whose centre clearance is below this (cells) cost extra …</summary>
        private const float WallMargin = 1.5f;
        /// <summary>… by this factor per cell of shortfall (a cell touching a wall costs 1 + 4 × 1.0 = 5×).</summary>
        private const float WallPenalty = 4f;
        /// <summary>A straight line to the steering target must keep at least this far from blocking cells (cells).</summary>
        private const float LineClearance = CarSize.HalfWidth;
        /// <summary>Reach of <see cref="ClearanceSquared"/>, cells.</summary>
        private const int ClearanceCap = 2;
        /// <summary>The non-centre half-cell lattice points of a cell: edge midpoints, then corners.</summary>
        private static readonly (float X, float Y)[] LatticeOffsets =
            [(0.5f, 0f), (1f, 0.5f), (0.5f, 1f), (0f, 0.5f), (0f, 0f), (1f, 0f), (1f, 1f), (0f, 1f)];

        private readonly RacingMode _mode;
        private readonly DistanceField _field;
        private readonly Grid _grid;
        private readonly double[] _factor;
        private readonly float[] _clearance;
        private readonly float _maxSpeed, _maxTurn, _fullSteerSpeed;
        private readonly float[] _px = new float[MaxPath], _py = new float[MaxPath], _arc = new float[MaxPath];
        private int _jammedFor, _reverseLeft;

        public Pilot(RacingMode mode)
        {
            _mode = mode;
            RacingTrack track = mode.Track;
            _grid = track.Grid;
            _factor = RacingGraph.SurfaceFactors(_grid, track.Settings.GrassCost);
            _clearance = track.Clearance.Values;
            for (int i = 0; i < _factor.Length; i++)
                if (_clearance[i] < WallMargin) _factor[i] *= 1.0 + WallPenalty * (WallMargin - _clearance[i]);
            _field = DistanceField.ForRacing(_grid, new RacingGraph(_grid, track.Clearance, CarSize.HalfWidth, _factor));
            _maxSpeed = mode.Track.Settings.MaxSpeed;
            _maxTurn = mode.Physics.MaxTurnRate;
            _fullSteerSpeed = mode.Physics.FullSteerSpeed;
        }

        public void Act(int agent, ReadOnlySpan<float> inputs, Span<float> outputs)
        {
            CarState car = _mode.Car(agent);
            float speed = car.ForwardSpeed;
            int n = BuildPath(car.X, car.Y);
            if (n == 0)
            {
                outputs[0] = 1f;
                outputs[1] = 0f;
                return;
            }

            // Cumulative path length from the car.
            float ax = _px[0] - car.X, ay = _py[0] - car.Y;
            _arc[0] = MathF.Sqrt(ax * ax + ay * ay);
            for (int j = 1; j < n; j++)
            {
                float sx = _px[j] - _px[j - 1], sy = _py[j] - _py[j - 1];
                _arc[j] = _arc[j - 1] + MathF.Sqrt(sx * sx + sy * sy);
            }

            // Steering: pure pursuit towards the first path point at least the look-ahead distance away, or the
            // nearest point before it that the car can drive to in a straight line (so it does not cut an inside
            // corner into the wall).
            float look = Math.Clamp(LookMin + LookPerSpeed * MathF.Abs(speed), LookMin, LookMax);
            int target = n - 1;
            for (int j = 0; j < n; j++)
                if (_arc[j] >= look) { target = j; break; }
            while (target > 0 && !LineIsClear(car.X, car.Y, _px[target], _py[target])) target--;
            float tx = _px[target] - car.X, ty = _py[target] - car.Y;
            float chord = MathF.Sqrt(tx * tx + ty * ty);
            float heading = car.Heading * FastMath.RadiansPerUnit;
            float alpha = FastMath.AngleDiff(heading, FastMath.Atan2(ty, tx));
            float steer;
            float available = _maxTurn * MathF.Min(1f, MathF.Abs(speed) / _fullSteerSpeed);
            if (chord < 1e-3f) steer = 0f;
            else if (available < 1e-3f) steer = MathF.Sign(alpha);
            else steer = MathF.Max(MathF.Abs(speed), 1f) * 2f * FastMath.Sin(alpha) / chord / available;

            // Speed: the most restrictive "brake down to the safe cornering speed in time" over the bends ahead.
            float limit = _maxSpeed;
            for (int j = CurveSpan; j + CurveSpan < n; j++)
            {
                float inAngle = FastMath.Atan2(_py[j] - _py[j - CurveSpan], _px[j] - _px[j - CurveSpan]);
                float outAngle = FastMath.Atan2(_py[j + CurveSpan] - _py[j], _px[j + CurveSpan] - _px[j]);
                float bend = MathF.Abs(FastMath.AngleDiff(inAngle, outAngle));
                float span = _arc[j + CurveSpan] - _arc[j - CurveSpan];
                if (bend < 1e-3f) continue;
                float corner = MathF.Max(CornerSafety * _maxTurn * span / bend, MinCornerSpeed);
                float allowed = MathF.Sqrt(corner * corner + 2f * BrakeShare * RacingSettings.Brake * _arc[j - CurveSpan]);
                if (allowed < limit) limit = allowed;
            }
            // Facing well away from the path (e.g. after a wall contact): turn round slowly first.
            if (MathF.Abs(alpha) > FastMath.HalfPi) limit = MathF.Min(limit, MinCornerSpeed);

            float throttle = speed < limit ? 1f : speed > limit + 1f ? -1f : 0f;
            steer = Math.Clamp(steer, -1f, 1f);

            // Un-jam: reversing with the steering mirrored turns the nose the same way as driving forward would.
            if (_reverseLeft == 0)
            {
                _jammedFor = throttle > 0f && speed < JamSpeed ? _jammedFor + 1 : 0;
                if (_jammedFor >= JamTicks)
                {
                    _jammedFor = 0;
                    _reverseLeft = ReverseTicks;
                }
            }
            if (_reverseLeft > 0)
            {
                _reverseLeft--;
                throttle = -1f;
                steer = -MathF.Sign(steer);
            }

            outputs[0] = throttle;
            outputs[1] = steer;
        }

        /// <summary>
        /// True when every point along the segment (sampled each half cell, the start left out) is at least
        /// <see cref="LineClearance"/> from blocking cells, i.e. the car centre can drive it straight.
        /// </summary>
        private bool LineIsClear(float x0, float y0, float x1, float y1)
        {
            float dx = x1 - x0, dy = y1 - y0;
            int steps = (int)MathF.Ceiling(MathF.Sqrt(dx * dx + dy * dy) / 0.5f);
            for (int k = 1; k <= steps; k++)
            {
                float t = (float)k / steps;
                if (ClearanceSquared(x0 + dx * t, y0 + dy * t) < LineClearance * LineClearance) return false;
            }
            return true;
        }

        /// <summary>
        /// Squared distance from point (x, y) to the nearest blocking square (the outside counts), capped at
        /// <see cref="ClearanceCap"/>² (only the cells within that reach are looked at).
        /// </summary>
        private float ClearanceSquared(float x, float y)
        {
            int cx = (int)MathF.Floor(x), cy = (int)MathF.Floor(y);
            float best = ClearanceCap * ClearanceCap;
            for (int by = cy - ClearanceCap; by <= cy + ClearanceCap; by++)
                for (int bx = cx - ClearanceCap; bx <= cx + ClearanceCap; bx++)
                {
                    if (!_grid.IsBlocking(bx, by)) continue;
                    float ex = MathF.Max(MathF.Max(bx - x, x - (bx + 1)), 0f);
                    float ey = MathF.Max(MathF.Max(by - y, y - (by + 1)), 0f);
                    best = MathF.Min(best, ex * ex + ey * ey);
                }
            return best;
        }

        /// <summary>
        /// Where in cell (x, y) the car should aim: its centre when the car centre fits there, otherwise the one of
        /// its 9 half-cell lattice points farthest from blocking cells (centre, then edge midpoints, then corners on
        /// a tie). E.g. in a 2-cell-wide corridor no cell centre fits, but the midline on the cells' shared edge does.
        /// </summary>
        private (float X, float Y) AimPoint(int x, int y)
        {
            float bestX = x + 0.5f, bestY = y + 0.5f;
            if (_clearance[_grid.Index(x, y)] >= CarSize.HalfWidth) return (bestX, bestY);
            float best = ClearanceSquared(bestX, bestY);
            foreach (var (ox, oy) in LatticeOffsets)
            {
                float d = ClearanceSquared(x + ox, y + oy);
                if (d > best)
                {
                    best = d;
                    bestX = x + ox;
                    bestY = y + oy;
                }
            }
            return (bestX, bestY);
        }

        /// <summary>
        /// Fills the path buffers with aim points (<see cref="AimPoint"/>) along the steering field's steepest descent from the car's cell
        /// (each step to the neighbour with the lowest value + step cost, diagonals not squeezing between two
        /// blocking cells). Returns the number of points (0 when no reachable cell is near the car).
        /// </summary>
        private int BuildPath(float x, float y)
        {
            int cx = (int)MathF.Floor(x), cy = (int)MathF.Floor(y);
            int cur = _field.IsReachable(cx, cy) ? _grid.Index(cx, cy) : BestNeighbour(cx, cy, float.PositiveInfinity, false);
            int n = 0;
            while (cur >= 0 && n < MaxPath)
            {
                int py = cur / _grid.Width, px = cur - py * _grid.Width;
                (_px[n], _py[n]) = AimPoint(px, py);
                n++;
                float value = _field.Values[cur];
                if (value == 0f) break;
                cur = BestNeighbour(px, py, value, true);
            }
            return n;
        }

        /// <summary>The reachable neighbour of (x, y) that is cheapest to go through, or −1.</summary>
        private int BestNeighbour(int x, int y, float below, bool costed)
        {
            int best = -1;
            double bestCost = double.PositiveInfinity;
            double here = _grid.InBounds(x, y) ? _factor[_grid.Index(x, y)] : 1.0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((dx | dy) == 0 || !_field.IsReachable(nx, ny)) continue;
                    bool diagonal = dx != 0 && dy != 0;
                    if (diagonal && _grid.IsBlocking(nx, y) && _grid.IsBlocking(x, ny)) continue;
                    float v = _field[nx, ny];
                    if (v >= below) continue;
                    double cost = v + (costed ? (diagonal ? RacingGraph.Diagonal : 1.0) * (here + _factor[_grid.Index(nx, ny)]) * 0.5 : 0.0);
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        best = _grid.Index(nx, ny);
                    }
                }
            return best;
        }
    }
}
