using Nitrogenesis.Sim.Core;
using Nitrogenesis.Sim.History;

/// <summary>"Watch best" (PLAN §5): a recording plays back sample by sample and stops at its last sample.</summary>
public class RecordingPlaybackTests
{
    private static Recording Make(int samples)
    {
        var r = new Recording();
        for (int t = 0; t < samples; t++) r.Add(10 + t * 0.5f, 20 - t * 0.25f, t * 100);
        return r;
    }

    [Fact]
    public void RefusesAnEmptyRecording() =>
        Assert.Throws<ArgumentException>(() => new RecordingPlayback(new Recording()));

    [Fact]
    public void StartsAtSampleZeroAndPlaysEveryRecordedPositionAt100x()
    {
        var rec = Make(301);
        var p = new RecordingPlayback(rec) { SpeedIndex = RecordingPlayback.MaxSpeedIndex }; // 100x: whole ticks, no interpolation
        p.Pose(out float x, out float y, out _);
        Assert.Equal((0, rec.X(0), rec.Y(0)), (p.Tick, x, y));
        Assert.False(p.Ended);

        int last = 0;
        while (!p.Ended)
        {
            p.Advance(1 / 60.0);
            Assert.True(p.Tick >= last);
            p.Pose(out x, out y, out _);
            Assert.Equal(rec.X(p.Tick), x);
            Assert.Equal(rec.Y(p.Tick), y);
            last = p.Tick;
        }
        Assert.Equal(300, p.Tick);
        Assert.Equal(p.LastTick, p.Tick);
    }

    [Fact]
    public void At1xItTakesAboutTheRecordedTimeAndStopsAtTheLastSample()
    {
        var rec = Make(121); // 120 ticks = 2 s
        var p = new RecordingPlayback(rec);
        Assert.Equal(1.0, p.TargetSpeed);
        double t = 0;
        while (!p.Ended && t < 10)
        {
            p.Advance(1 / 60.0);
            t += 1 / 60.0;
        }
        Assert.InRange(t, 1.95, 2.1);
        // Past the end it stays on the last sample.
        p.Advance(5);
        p.Pose(out float x, out float y, out _);
        Assert.Equal((120, rec.X(120), rec.Y(120)), (p.Tick, x, y));
    }

    [Fact]
    public void BelowOneTheCarIsDrawnBetweenTwoSamples()
    {
        var rec = Make(50);
        var p = new RecordingPlayback(rec) { SpeedIndex = SpeedController.IndexFor(0.5) };
        p.Advance(1 / 60.0); // half a tick
        p.Pose(out float x, out _, out _);
        Assert.Equal(0, p.Tick);
        Assert.InRange(x, rec.X(0) + 0.01f, rec.X(1) - 0.01f);
    }

    [Fact]
    public void PausedStaysPutStepMovesOneTickAndRestartGoesBack()
    {
        var p = new RecordingPlayback(Make(100)) { Paused = true };
        p.Advance(2);
        Assert.Equal(0, p.Tick);
        p.StepOnce();
        p.Advance(0);
        Assert.Equal(1, p.Tick);
        p.StepOnce();
        p.Advance(0);
        Assert.Equal(2, p.Tick);

        p.Paused = false;
        p.SpeedIndex = RecordingPlayback.MaxSpeedIndex;
        p.Advance(10);
        Assert.True(p.Ended);
        p.Restart();
        Assert.Equal(0, p.Tick);
        Assert.False(p.Ended);
        Assert.Equal(RecordingPlayback.MaxSpeedIndex, p.SpeedIndex); // restart keeps the speed
    }

    [Fact]
    public void MaxIsNotOffered()
    {
        var p = new RecordingPlayback(Make(10)) { SpeedIndex = 1000 };
        Assert.Equal(RecordingPlayback.MaxSpeedIndex, p.SpeedIndex);
        Assert.Equal(100.0, p.TargetSpeed);
        p.SpeedIndex = -3;
        Assert.Equal(0, p.SpeedIndex);
    }

    [Fact]
    public void AnEncodedRecordingPlaysTheSame()
    {
        var rec = Make(80);
        var back = Recording.Decode(rec.Encode());
        var a = new RecordingPlayback(rec) { SpeedIndex = RecordingPlayback.MaxSpeedIndex };
        var b = new RecordingPlayback(back) { SpeedIndex = RecordingPlayback.MaxSpeedIndex };
        while (!a.Ended)
        {
            a.Advance(0.01);
            b.Advance(0.01);
            a.Pose(out float ax, out float ay, out float aa);
            b.Pose(out float bx, out float by, out float ba);
            Assert.Equal((a.Tick, ax, ay, aa), (b.Tick, bx, by, ba));
        }
        Assert.True(b.Ended);
    }
}
