using Nitrogenesis.Sim.Brain;

public class MlpTests
{
    [Fact]
    public void DefaultShapeHas302Weights()
    {
        var shape = BrainShape.Default(22, 2);
        Assert.Equal(23 * 12 + 13 * 2, shape.WeightCount);
        Assert.Equal(302, shape.WeightCount);
        Assert.Equal(12, shape.HiddenCount);
        // Output layer starts at 276; output k's row is 13 long with the bias last.
        Assert.Equal(276 + 12, shape.OutputBiasIndex(0));
        Assert.Equal(276 + 13 + 12, shape.OutputBiasIndex(1));
    }

    [Fact]
    public void SecondHiddenLayerCountsAndBiasIndex()
    {
        var shape = new BrainShape(5, 4, 6, 3);
        Assert.Equal(6 * 4 + 5 * 6 + 7 * 3, shape.WeightCount);
        Assert.Equal(6, shape.LastHidden);
        Assert.Equal(6 * 4 + 5 * 6 + 2 * 7 + 6, shape.OutputBiasIndex(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => shape.OutputBiasIndex(3));
    }

    /// <summary>The reference computation, written out separately: bias first, then weights in input order.</summary>
    private static float[] Reference(BrainShape shape, float[] g, float[] x)
    {
        int o = 0;
        float[] Layer(float[] input, int size)
        {
            var y = new float[size];
            for (int j = 0; j < size; j++)
            {
                float s = g[o + input.Length];
                for (int i = 0; i < input.Length; i++) s += g[o + i] * input[i];
                y[j] = FastMath.Tanh(s);
                o += input.Length + 1;
            }
            return y;
        }
        float[] h = Layer(x, shape.Hidden1);
        if (shape.Hidden2 > 0) h = Layer(h, shape.Hidden2);
        return Layer(h, shape.Outputs);
    }

    private static float[] Sequence(int n, float scale) =>
        Enumerable.Range(0, n).Select(i => scale * (((i * 37) % 23) - 11) / 11f).ToArray();

    [Theory]
    [InlineData(22, 12, 0, 2)]
    [InlineData(22, 4, 0, 2)]
    [InlineData(31, 32, 32, 2)]
    [InlineData(3, 5, 7, 4)]
    public void EvaluateMatchesTheReferenceBitForBit(int inputs, int h1, int h2, int outputs)
    {
        var shape = new BrainShape(inputs, h1, h2, outputs);
        float[] genome = Sequence(shape.WeightCount, 0.6f);
        float[] x = Sequence(inputs, 1f).Reverse().ToArray();
        var hidden = new float[shape.HiddenCount];
        var y = new float[outputs];
        Mlp.Evaluate(shape, genome, x, hidden, y);
        float[] expected = Reference(shape, genome, x);
        Assert.Equal(expected.Select(BitConverter.SingleToUInt32Bits), y.Select(BitConverter.SingleToUInt32Bits));
        Assert.All(y, v => Assert.InRange(v, -1f, 1f));
    }

    [Fact]
    public void HandComputedTinyNet()
    {
        // 2 inputs → 4 hidden → 1 output; only hidden 0 is wired: h0 = tanh(0.5 + 1·x0 − 2·x1), out = tanh(0.25 + 3·h0).
        var shape = new BrainShape(2, 4, 0, 1);
        var g = new float[shape.WeightCount];
        g[0] = 1f; g[1] = -2f; g[2] = 0.5f;
        int outRow = 3 * 4;
        g[outRow + 0] = 3f;
        g[outRow + 4] = 0.25f;
        var y = new float[1];
        Mlp.Evaluate(shape, g, [0.3f, 0.1f], new float[4], y);
        float h0 = FastMath.Tanh(0.5f + 1f * 0.3f + -2f * 0.1f);
        Assert.Equal(FastMath.Tanh(0.25f + 3f * h0), y[0]);
    }

    [Fact]
    public void ActUsesTheAgentsOwnGenome()
    {
        var shape = BrainShape.Default(4, 2);
        var weights = new float[3 * shape.WeightCount];
        // Only the output biases differ between the agents.
        for (int a = 0; a < 3; a++) weights[a * shape.WeightCount + shape.OutputBiasIndex(0)] = 0.5f * (a - 1);
        var mlp = new Mlp(shape, weights);
        Assert.Equal(3, mlp.AgentCount);
        var outputs = new float[2];
        for (int a = 0; a < 3; a++)
        {
            mlp.Act(a, new float[4], outputs);
            Assert.Equal(FastMath.Tanh(0.5f * (a - 1)), outputs[0]);
            Assert.Equal(0f, outputs[1]);
        }
        Assert.Throws<ArgumentException>(() => mlp.Weights = new float[shape.WeightCount]);
        Assert.Throws<ArgumentException>(() => new Mlp(shape, new float[shape.WeightCount + 1]));
    }

    [Fact]
    public void ActDoesNotAllocate()
    {
        var shape = BrainShape.Default(22, 2);
        var mlp = new Mlp(shape, Sequence(8 * shape.WeightCount, 0.4f));
        var inputs = Sequence(22, 1f);
        var outputs = new float[2];
        mlp.Act(3, inputs, outputs);
        Allocations.AssertSteadyStateFree(() =>
        {
            for (int i = 0; i < 1000; i++) mlp.Act(i & 7, inputs, outputs);
        });
    }
}
