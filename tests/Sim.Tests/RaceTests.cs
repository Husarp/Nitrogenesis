using Nitrogenesis.Sim;
using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Racing;

/// <summary>Race yourself (PLAN §6.5): ghosts on the race clock, physics choice, splits and results.</summary>
public class RaceTests
{
    private static readonly DateTime Saved = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static RacingTrack Sprint() => new(TestTracks.Load("sprint.track"), new RacingSettings());

    /// <summary>A recording with a sample per path point (heading along +x).</summary>
    private static Recording Rec(IEnumerable<(float X, float Y)> path)
    {
        var r = new Recording();
        foreach (var (x, y) in path) r.Add(x, y, 0);
        return r;
    }

    private static GhostEntry Ghost(Recording r, float finishTicks, string session = "run", int generation = 1, RacingSettings? physics = null,
        int simVersion = SimInfo.SimVersion, string branch = "main") =>
        new(session, branch, generation, 0, Saved, simVersion, physics ?? new RacingSettings(), finishTicks, r.Encode());

    private static ReferenceDriver.Result Reference(RacingTrack track)
    {
        var reference = ReferenceDriver.Run(track);
        Assert.True(reference.Finished);
        return reference;
    }

    /// <summary>The player drives with the reference driver's control law until it stops (finished or crashed).</summary>
    private static void PilotDrives(Race race)
    {
        race.Restart(0);
        var mirror = new RacingMode(race.Track, 1, ReferenceDriver.MaxSeconds * RacingSettings.TicksPerSecond);
        var pilot = ReferenceDriver.CreatePilot(mirror);
        var outputs = new float[RacingSettings.OutputCount];
        while (race.Run.Phase == PlayerPhase.Driving && race.Run.Ticks < ReferenceDriver.MaxSeconds * RacingSettings.TicksPerSecond)
        {
            mirror.SetCar(0, race.Run.Car);
            pilot.Act(0, ReadOnlySpan<float>.Empty, outputs);
            race.Drive(outputs[0], outputs[1]);
        }
    }

    // ---------------------------------------------------------------- physics of the chosen ghosts

    [Fact]
    public void NoGhostsMeansTheDefaultPhysics()
    {
        var choice = GhostPhysics.Choose([]);
        Assert.Equal(new RacingSettings(), choice.Settings);
        Assert.False(choice.Mismatch);
        Assert.Null(choice.Warning);
    }

    [Fact]
    public void GhostsWithTheSamePhysicsNeedNoWarningEvenIfSensorsOrTimingDiffer()
    {
        var rec = Rec([(1, 1), (2, 1)]);
        var a = Ghost(rec, 100, "a", physics: new RacingSettings { MaxSpeed = 25f });
        var b = Ghost(rec, 90, "b", 3, new RacingSettings { MaxSpeed = 25f, RayCount = 9, StallSeconds = 10f, TimeLimitSeconds = 40f });
        var choice = GhostPhysics.Choose([a, b]);
        Assert.False(choice.Mismatch);
        Assert.Null(choice.Warning);
        Assert.Equal(25f, choice.Settings.MaxSpeed);
    }

    [Fact]
    public void GhostsFromDifferentPhysicsWarnAndTheFirstOnesPhysicsIsUsed()
    {
        var rec = Rec([(1, 1), (2, 1)]);
        var fast = Ghost(rec, 100, "fast", physics: new RacingSettings { MaxSpeed = 30f });
        var slow = Ghost(rec, 100, "slow", physics: new RacingSettings { MaxSpeed = 12f });
        var choice = GhostPhysics.Choose([slow, fast]);
        Assert.True(choice.Mismatch);
        Assert.Equal(12f, choice.Settings.MaxSpeed);
        Assert.Contains("slow", choice.Warning);
        Assert.Equal(30f, GhostPhysics.Choose([fast, slow]).Settings.MaxSpeed); // the order decides, not the speed
    }

    [Theory]
    [InlineData("acceleration")]
    [InlineData("turn")]
    [InlineData("grass")]
    [InlineData("walls")]
    [InlineData("simversion")]
    public void EveryPhysicsDifferenceCountsAsAMismatch(string what)
    {
        var rec = Rec([(1, 1), (2, 1)]);
        var a = Ghost(rec, 100);
        var b = what switch
        {
            "acceleration" => Ghost(rec, 100, physics: new RacingSettings { Acceleration = 20f }),
            "turn" => Ghost(rec, 100, physics: new RacingSettings { TurnRateDeg = 150f }),
            "grass" => Ghost(rec, 100, physics: new RacingSettings { GrassGrip = 0.9f }),
            "walls" => Ghost(rec, 100, physics: new RacingSettings { Walls = WallMode.Bounce }),
            _ => Ghost(rec, 100, simVersion: SimInfo.SimVersion - 1),
        };
        Assert.True(GhostPhysics.Choose([a, b]).Mismatch);
    }

    [Fact]
    public void TheRaceUsesTheFirstGhostsPhysicsForThePlayersCar()
    {
        var track = TestTracks.Load("sprint.track");
        var path = Reference(Sprint()).Path;
        var a = Ghost(Rec(path), 1800, "a", physics: new RacingSettings { MaxSpeed = 24f });
        var b = Ghost(Rec(path), 1800, "b", physics: new RacingSettings { MaxSpeed = 18f });
        var race = Race.Create(track, [a, b]);
        Assert.True(race.Physics.Mismatch);
        Assert.Equal(24f, race.Track.Settings.MaxSpeed);
        Assert.Equal(new RacingSettings(), Race.Create(track, []).Track.Settings);
        Assert.Throws<ArgumentException>(() => Race.Create(track, Enumerable.Repeat(a, Race.MaxGhosts + 1).ToArray()));
    }

    // ---------------------------------------------------------------- splits

    [Fact]
    public void ASplitIsSetOnTheFirstCrossingOnly()
    {
        var s = new RaceSplits([]);
        s.Update(0.1f, 50);
        Assert.Equal(0, s.Crossed);
        s.Update(0.25f, 100);
        Assert.Equal(100, s.PlayerTick(0));
        s.Update(0.3f, 130);
        s.Update(0.1f, 200); // never happens (progress is the best so far), but must not move a split
        s.Update(0.3f, 300);
        Assert.Equal(100, s.PlayerTick(0));
        Assert.Equal(-1, s.PlayerTick(1));
        s.Update(0.8f, 400); // two splits at once: both at this tick
        Assert.Equal((400, 400, 3), (s.PlayerTick(1), s.PlayerTick(2), s.Crossed));
        s.Update(1f, 500);
        Assert.Equal(400, s.PlayerTick(2));
        s.Reset();
        Assert.Equal((-1, 0), (s.PlayerTick(0), s.Crossed));
        Assert.Equal(25, (int)(RaceSplits.Fraction(0) * 100));
        Assert.Equal(75, (int)(RaceSplits.Fraction(2) * 100));
    }

    [Fact]
    public void AheadIsNegativeBehindIsPositiveAndAGhostThatNeverGotThereIsBehind()
    {
        var s = new RaceSplits([[130, 300, 450], [90, -1, -1]]);
        Assert.Null(s.Delta(0, 0)); // the player has not crossed it
        s.Update(0.26f, 100);
        var ahead = s.Delta(0, 0)!.Value;
        Assert.Equal(-0.5f, ahead.Seconds, 4); // 30 ticks earlier than the ghost
        Assert.True(ahead.GhostReached && ahead.Ahead);
        var behind = s.Delta(0, 1)!.Value;
        Assert.Equal(10f / 60, behind.Seconds, 4);
        Assert.True(behind.GhostReached && !behind.Ahead);
        s.Update(0.51f, 320);
        var never = s.Delta(1, 1)!.Value;
        Assert.False(never.GhostReached);
        Assert.True(never.Ahead);
        Assert.Equal(20f / 60, s.Delta(1, 0)!.Value.Seconds, 4);
        Assert.Null(s.Delta(2, 0));
    }

    [Fact]
    public void TheGhostsSplitsAreTheTicksOfItsFirstCrossingOnTheRacesField()
    {
        var track = Sprint();
        var path = Reference(track).Path.ToList();
        var full = new RaceGhost(Ghost(Rec(path), path.Count - 1), track);
        Assert.True(full.SplitTick(0) > 0 && full.SplitTick(0) < full.SplitTick(1) && full.SplitTick(1) < full.SplitTick(2));
        Assert.True(full.SplitTick(2) < full.LastTick);
        Assert.Equal(1f, full.Progress);

        // Out to 50 %, back to the start, then the whole way: the splits are those of the first pass.
        int k = full.SplitTick(1);
        var there = path.Take(k + 1).ToList();
        var loop = there.Concat(Enumerable.Reverse(there)).Concat(path).ToList();
        var looped = new RaceGhost(Ghost(Rec(loop), loop.Count - 1), track);
        Assert.Equal(new[] { full.SplitTick(0), full.SplitTick(1), full.SplitTick(2) }.Take(2), new[] { looped.SplitTick(0), looped.SplitTick(1) });
        Assert.Equal(full.SplitTick(2) + 2 * (k + 1), looped.SplitTick(2));

        // Stops before 50 %: reached 25 % only; unfinished, so its progress is what it reached.
        var stopped = new RaceGhost(Ghost(Rec(path.Take(full.SplitTick(0) + 5)), float.PositiveInfinity), track);
        Assert.Equal((full.SplitTick(0), -1, -1), (stopped.SplitTick(0), stopped.SplitTick(1), stopped.SplitTick(2)));
        Assert.InRange(stopped.Progress, 0.25f, 0.5f);
        Assert.False(stopped.Finished);
    }

    [Fact]
    public void SplitsAgainstGhostsThatAreLaterEarlierAndGone()
    {
        var track = Sprint();
        var ref0 = Reference(track);
        var path = ref0.Path.ToList();
        int n = path.Count - 1;
        var same = Ghost(Rec(path), n, "same");
        var late = Ghost(Rec(Enumerable.Repeat(path[0], 60).Concat(path)), n + 60, "late"); // starts a second later
        var quick = Ghost(Rec(path.Where((_, i) => i % 10 != 9)), n * 0.9f, "quick");      // 10 % faster
        var gone = Ghost(Rec(path.Take(n / 3)), float.PositiveInfinity, "gone");
        var race = Race.Create(TestTracks.Load("sprint.track"), [same, late, quick, gone]);
        PilotDrives(race);
        Assert.Equal(PlayerPhase.Finished, race.Run.Phase);
        Assert.Equal(3, race.Splits.Crossed);

        for (int split = 0; split < RaceSplits.Count; split++)
        {
            Assert.InRange(race.Splits.Delta(split, 0)!.Value.Seconds, -2f / 60, 2f / 60); // the same route: level (quantization may shift a tick)
            Assert.InRange(race.Splits.Delta(split, 1)!.Value.Seconds, -1.05f, -0.95f);    // a second ahead of the late one
            var d = race.Splits.Delta(split, 2)!.Value;
            Assert.True(d.Seconds > 0f && !d.Ahead);                                        // behind the quick one, more and more
            if (split > 0) Assert.True(d.Seconds > race.Splits.Delta(split - 1, 2)!.Value.Seconds);
        }
        Assert.True(race.Splits.Delta(0, 3)!.Value.GhostReached);
        Assert.False(race.Splits.Delta(2, 3)!.Value.GhostReached);
        Assert.True(race.Splits.Delta(2, 3)!.Value.Ahead);
    }

    [Fact]
    public void ARestartClearsTheSplitsAndTheClock()
    {
        var track = Sprint();
        var path = Reference(track).Path.ToList();
        var race = Race.Create(TestTracks.Load("sprint.track"), [Ghost(Rec(path), path.Count - 1)]);
        PilotDrives(race);
        Assert.Equal(3, race.Splits.Crossed);
        Assert.True(race.Ticks > 0);
        race.Restart();
        Assert.Equal((PlayerPhase.Countdown, 0, 0), (race.Run.Phase, race.Ticks, race.Splits.Crossed));
        Assert.Equal(-1, race.Splits.PlayerTick(0));
    }

    // ---------------------------------------------------------------- the ghosts' clock and positions

    [Fact]
    public void AGhostPlaysItsRecordingSampleBySampleAndHoldsTheLastOne()
    {
        var track = Sprint();
        var rec = new Recording();
        for (int t = 0; t < 200; t++) rec.Add(10 + t * 0.5f, 20 - t * 0.25f, t * 100);
        var ghost = new RaceGhost(Ghost(rec, 199), track);
        for (int t = 0; t <= 199; t += 7)
        {
            ghost.Pose(t, 0f, out float x, out float y, out float a);
            Assert.Equal((rec.X(t), rec.Y(t), rec.Heading(t) * Nitrogenesis.Sim.Brain.FastMath.RadiansPerUnit), (x, y, a));
        }
        ghost.Pose(1000, 0.5f, out float lx, out float ly, out _);
        Assert.Equal((rec.X(199), rec.Y(199)), (lx, ly)); // past the end: held, never extrapolated
        ghost.Pose(10, 0.5f, out float mx, out float my, out _);
        Assert.Equal(rec.X(10) + (rec.X(11) - rec.X(10)) * 0.5f, mx);
        Assert.Equal(rec.Y(10) + (rec.Y(11) - rec.Y(10)) * 0.5f, my);
        ghost.Pose(-5, 0f, out float sx, out _, out _);
        Assert.Equal(rec.X(0), sx);
        Assert.Equal(AgentStatus.Running, ghost.StatusAt(198));
        Assert.Equal(AgentStatus.Finished, ghost.StatusAt(199));
        Assert.Equal(AgentStatus.Crashed, new RaceGhost(Ghost(rec, float.PositiveInfinity), track).StatusAt(500)); // gone, not on the finish
    }

    [Fact]
    public void TheGhostsMoveWithTheRaceClockNotTheCountdown()
    {
        var track = Sprint();
        var path = Reference(track).Path.ToList();
        var rec = Rec(path);
        var race = Race.Create(TestTracks.Load("sprint.track"), [Ghost(rec, path.Count - 1)]);
        race.GhostPose(0, 0.5f, out float x0, out float y0, out _);
        Assert.False(race.ClockRunning);
        for (int t = 0; t < PlayerRun.CountdownTicks; t++)
        {
            race.Tick(1, 0, 0f, 0f, 0f);
            race.GhostPose(0, 0.9f, out float x1, out float y1, out _);
            Assert.Equal((x0, y0), (x1, y1)); // the countdown never moves a ghost, whatever the render fraction
        }
        Assert.Equal((0, PlayerPhase.Driving), (race.Ticks, race.Run.Phase));
        for (int t = 0; t < 90; t++) race.Tick(1, 0, 0f, 0f, 0f);
        Assert.Equal(90, race.Ticks);
        Assert.Equal(race.Run.Ticks, race.Ticks);
        // Drawn like the player's car: between the last two ticks (alpha 0 = tick 89, alpha 1 = tick 90).
        race.GhostPose(0, 1f, out float x90, out float y90, out _);
        Assert.Equal((rec.X(90), rec.Y(90)), (x90, y90));
        race.GhostPose(0, 0f, out float x89, out _, out _);
        Assert.Equal(rec.X(89), x89);
        race.GhostPose(0, 0.5f, out float xm, out _, out _);
        Assert.Equal(rec.X(89) + (rec.X(90) - rec.X(89)) * 0.5f, xm);
    }

    [Fact]
    public void TheGhostsGoOnAfterACrashAndStopWhenThePlayerFinishes()
    {
        var map = TestMaps.Parse(
            "############",
            "#..........#",
            "#.S......xx#",
            "#..........#",
            "############");
        var rec = new Recording();
        for (int t = 0; t < 600; t++) rec.Add(2.5f + t * 0.01f, 2.5f, 0);
        var race = Race.Create(map, [Ghost(rec, float.PositiveInfinity)]);
        race.Restart(0);
        while (race.Run.Phase == PlayerPhase.Driving) race.Tick(1, 0, 0f, 0f, 0f);
        Assert.Equal(PlayerPhase.Crashed, race.Run.Phase);
        int crashed = race.Ticks;
        for (int t = 0; t < 30; t++) race.Tick(1, 0, 0f, 0f, 0f);
        Assert.Equal(crashed + 30, race.Ticks);
        Assert.Equal(crashed, race.Run.Ticks);
        Assert.True(race.ClockRunning);

        var track = Sprint();
        var path = Reference(track).Path.ToList();
        var other = Race.Create(TestTracks.Load("sprint.track"), [Ghost(Rec(path), path.Count - 1)]);
        PilotDrives(other);
        Assert.Equal(PlayerPhase.Finished, other.Run.Phase);
        int finished = other.Ticks;
        Assert.Equal(other.Run.Ticks, finished);
        other.Drive(1f, 0f);
        other.Tick(1, 0, 0f, 0f, 0f);
        Assert.Equal(finished, other.Ticks);
        Assert.False(other.ClockRunning);
    }

    [Fact]
    public void TwoRacesWithTheSameInputShowTheSameGhostsAtEveryTick()
    {
        var path = Reference(Sprint()).Path.ToList();
        var entries = new[] { Ghost(Rec(path), path.Count - 1, "a"), Ghost(Rec(path.Skip(30)), path.Count - 31, "b", 2) };
        Race Make() => Race.Create(TestTracks.Load("sprint.track"), entries);
        var one = Make();
        var two = Make();
        for (int t = 0; t < PlayerRun.CountdownTicks + 400; t++)
        {
            int steer = t % 90 < 45 ? 1 : -1;
            one.Tick(1, steer, 0f, 0f, 0f);
            two.Tick(1, steer, 0f, 0f, 0f);
            float alpha = (t % 5) / 5f;
            for (int g = 0; g < 2; g++)
            {
                one.GhostPose(g, alpha, out float x1, out float y1, out float a1);
                two.GhostPose(g, alpha, out float x2, out float y2, out float a2);
                Assert.Equal((x1, y1, a1), (x2, y2, a2));
                Assert.Equal(one.GhostStatus(g), two.GhostStatus(g));
            }
            Assert.Equal((one.Run.Car, one.Ticks, one.Splits.Crossed), (two.Run.Car, two.Ticks, two.Splits.Crossed));
        }
    }

    [Fact]
    public void AGhostRouteIsThinnedByAPowerOfTwoToFitAndStopsAtTheTick()
    {
        var track = Sprint();
        var rec = new Recording();
        for (int t = 0; t < 5000; t++) rec.Add(10 + t * 0.001f, 20, 0);
        var ghost = new RaceGhost(Ghost(rec, 4999), track);
        Assert.Equal(4, ghost.RouteStride); // 5000 samples: 4 × 2048 ≥ 5000 > 2 × 2048
        var x = new float[RaceGhost.MaxRoutePoints + 2];
        var y = new float[RaceGhost.MaxRoutePoints + 2];
        Assert.Equal(1, ghost.CopyRoute(0, x, y));
        Assert.Equal(26, ghost.CopyRoute(100, x, y));
        Assert.Equal(rec.X(100), x[25]);
        Assert.Equal(1250, ghost.CopyRoute(99999, x, y));
        Assert.Equal(rec.X(4996), x[1249]);
    }

    // ---------------------------------------------------------------- results

    [Fact]
    public void FinishersAreOrderedByTimeThenTheOthersByProgress()
    {
        RaceStanding Line(string name, bool player, float seconds, float progress, int ghost = 0) =>
            new(name, player, float.IsFinite(seconds), seconds, progress, ghost);
        var order = RaceStanding.Order(
        [
            Line("a", false, 30f, 1f),
            Line("dnf-low", false, float.PositiveInfinity, 0.4f),
            Line("b", false, 28f, 1f),
            Line("dnf-high", false, float.PositiveInfinity, 0.7f),
            Line("You", true, 29f, 1f, -1),
        ]);
        Assert.Equal(["b", "You", "a", "dnf-high", "dnf-low"], order.Select(s => s.Name));

        // A tie goes to the player; a player who did not finish ranks among the others by progress.
        Assert.Equal("You", RaceStanding.Order([Line("g", false, 28f, 1f), Line("You", true, 28f, 1f, -1)])[0].Name);
        var dnf = RaceStanding.Order([Line("g", false, 40f, 1f), Line("x", false, float.PositiveInfinity, 0.3f), Line("You", true, float.PositiveInfinity, 0.5f, -1)]);
        Assert.Equal(["g", "You", "x"], dnf.Select(s => s.Name));
        Assert.Equal(["a", "b"], RaceStanding.Order([Line("a", false, 30f, 1f), Line("b", false, 30f, 1f)]).Select(s => s.Name)); // equal lines keep their order
    }

    [Fact]
    public void TheRaceResultsHoldThePlayersTimeAndEveryGhostsRecordedTime()
    {
        var track = Sprint();
        var ref0 = Reference(track);
        var path = ref0.Path.ToList();
        float refTicks = ref0.TimeSeconds * RacingSettings.TicksPerSecond;
        var quick = Ghost(Rec(path), refTicks - 120f, "quick", 5);
        var slow = Ghost(Rec(path), refTicks + 300f, "slow", 2);
        var gone = Ghost(Rec(path.Take(path.Count / 2)), float.PositiveInfinity, "gone", 3);
        var race = Race.Create(TestTracks.Load("sprint.track"), [slow, gone, quick]);

        var before = race.Standings(); // before the finish: the player has not finished
        Assert.False(before.Single(s => s.IsPlayer).Finished);
        Assert.Equal(["gen 5 · quick", "gen 2 · slow"], before.Take(2).Select(s => s.Name));

        PilotDrives(race);
        var results = race.Standings();
        Assert.Equal(["gen 5 · quick", "You", "gen 2 · slow"], results.Take(3).Select(s => s.Name));
        var you = results[1];
        Assert.True(you.IsPlayer && you.Finished);
        Assert.Equal(ref0.TimeSeconds, you.Seconds);
        Assert.Equal(1f, you.Progress);
        Assert.Equal(quick.FinishSeconds, results[0].Seconds);
        var dnf = results[3];
        Assert.False(dnf.Finished);
        Assert.Equal(1, dnf.Ghost);
        Assert.InRange(dnf.Progress, 0.1f, 0.9f);
        Assert.Equal([2, -1, 0, 1], results.Select(s => s.Ghost).Select(g => g)); // quick = ghost 2, slow = ghost 0, gone = ghost 1
    }
}
