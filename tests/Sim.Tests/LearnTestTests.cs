using Nitrogenesis.Sim.Evolution;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Map;
using Nitrogenesis.Sim.Racing;

public class LearnTestTests
{
    [Fact]
    public void GrassShareCountsTicksWithTheCentreOnGrass()
    {
        var g = new TrackBuilder(new Grid(10, 4)).Fill(CellType.Road).Grid;
        g[5, 1] = CellType.Grass;
        g[6, 1] = CellType.Grass;
        var rec = new Recording();
        rec.Add(5.5f, 1.5f, 0); // the start pose is not a tick, even on grass
        rec.Add(4.9f, 1.5f, 0); // road
        rec.Add(5.1f, 1.5f, 0); // grass
        rec.Add(6.9f, 1.9f, 0); // grass
        rec.Add(7.0f, 1.5f, 0); // road
        Assert.Equal(0.5f, LearnTest.GrassShare(rec, g));

        var empty = new Recording();
        empty.Add(5.5f, 1.5f, 0);
        Assert.Equal(0f, LearnTest.GrassShare(empty, g));
    }

    [Fact]
    public void PassesWhenACarFinishesWithinTheBudget()
    {
        var r = LearnTest.Run(TestTracks.Load("sprint.track"), budget: 30, seed: 1, threads: 4);
        Assert.True(r.Passed, r.Failure);
        Assert.Equal(0, r.FirstFinish);           // stops at the first finish without the road check
        Assert.Equal(r.FirstFinish + 1, r.GenerationsRun);
        Assert.True(float.IsFinite(r.BestFinishSeconds));
        Assert.Null(r.GrassShare);
    }

    [Fact]
    public void FailsWhenNoCarFinishesWithinTheBudget()
    {
        // wrong_turn needs a few generations (first finish at 3 with seed 1, NOTES.md).
        int seen = 0;
        var r = LearnTest.Run(TestTracks.Load("wrong_turn.track"), budget: 1, seed: 1, threads: 4, onGeneration: _ => seen++);
        Assert.False(r.Passed);
        Assert.Equal(-1, r.FirstFinish);
        Assert.Equal(1, r.GenerationsRun);
        Assert.Equal(1, seen);
        Assert.NotNull(r.Failure);
    }

    [Fact]
    public void RoadCheckRunsTheWholeBudgetAndMeasuresTheBestRoute()
    {
        var r = LearnTest.Run(TestTracks.Load("grass_shortcut.track"), budget: 3, seed: 1, threads: 4, requireRoadRoute: true);
        Assert.Equal(3, r.GenerationsRun);
        Assert.NotNull(r.GrassShare);
        Assert.Equal(r.GrassShare < LearnTest.MaxGrassShare, r.Passed);
    }

    [Fact]
    public void RoadCheckFailsWhenCuttingAcrossTheGrassPays()
    {
        // With grass as fast as road the V's grass apron is a real shortcut, and evolution takes it.
        var fastGrass = new RacingSettings { GrassSpeedFraction = 1f };
        var r = LearnTest.Run(TestTracks.Load("grass_shortcut.track"), budget: 15, seed: 1, threads: 4, requireRoadRoute: true, settings: fastGrass);
        Assert.False(r.Passed);
        Assert.True(r.GrassShare > LearnTest.MaxGrassShare, $"grass share {r.GrassShare}");
    }

    [Fact]
    public void RandomSearchEvaluatesAFreshRandomPopulationEveryGeneration()
    {
        var track = TestTracks.Load("labyrinth.track");
        var genomes = new List<float[]>();
        var r = LearnTest.Run(track, budget: 2, seed: 7, threads: 4, randomSearch: true, onGeneration: g => genomes.Add(g.BestGenome.Weights));
        Assert.False(r.Passed);
        Assert.Equal(2, r.GenerationsRun);

        // Generation 1's best genome comes from a fresh random population with that generation's seed (not bred).
        using var training = new RacingTraining(track, new RacingSettings(), 7);
        var fresh = new Population(training.Shape, training.Runner.PopulationSize);
        fresh.Randomize(training.Runner.GenerationSeed(1), RacingSettings.InitialOutputBias());
        Assert.Contains(Enumerable.Range(0, fresh.Size), i => Population.BitEquals(fresh.Genome(i), genomes[1]));
    }
}
