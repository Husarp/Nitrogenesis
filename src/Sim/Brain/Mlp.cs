using Nitrogenesis.Sim.Core;

namespace Nitrogenesis.Sim.Brain;

/// <summary>
/// The fixed-size neural-net brain (PLAN §3.5): inputs → hidden 1 (tanh) → optional hidden 2 (tanh) → outputs
/// (tanh). As an <see cref="IAgentPolicy"/> it drives agent i with genome i of a flat population array
/// (<see cref="Weights"/>, genome i at [i·WeightCount, (i+1)·WeightCount)).
/// </summary>
/// <remarks>
/// <para>Genome layout, layer after layer: for each neuron its incoming weights in input order, then its bias.
/// A neuron's sum starts at the bias and adds weight × input for input 0, 1, 2, … in that fixed order (no SIMD,
/// no FMA), and goes through <see cref="FastMath.Tanh"/>, so the result is bit-identical on every platform.</para>
/// <para>Hidden activations go to per-agent scratch, so disjoint agents can be evaluated on different threads
/// at once. <see cref="Act"/> does not allocate.</para>
/// </remarks>
public sealed class Mlp : IAgentPolicy
{
    private readonly float[] _hidden;
    private float[] _weights;

    /// <param name="shape">Layer sizes.</param>
    /// <param name="weights">The genomes, one per agent (a whole number ≥ 1 of <see cref="BrainShape.WeightCount"/>).</param>
    public Mlp(BrainShape shape, float[] weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        if (weights.Length == 0 || weights.Length % shape.WeightCount != 0)
            throw new ArgumentException($"Weights must be a whole number (≥ 1) of {shape.WeightCount}-weight genomes.", nameof(weights));
        Shape = shape;
        AgentCount = weights.Length / shape.WeightCount;
        _hidden = new float[AgentCount * shape.HiddenCount];
        _weights = weights;
    }

    public BrainShape Shape { get; }
    public int AgentCount { get; }

    /// <summary>All genomes, agent-major (used, not copied). Set it to the next population to evaluate; same size.</summary>
    public float[] Weights
    {
        get => _weights;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length != AgentCount * Shape.WeightCount)
                throw new ArgumentException($"Expected {AgentCount} genomes of {Shape.WeightCount} weights.", nameof(value));
            _weights = value;
        }
    }

    public void Act(int agent, ReadOnlySpan<float> inputs, Span<float> outputs)
    {
        int w = Shape.WeightCount, h = Shape.HiddenCount;
        Evaluate(Shape, _weights.AsSpan(agent * w, w), inputs, _hidden.AsSpan(agent * h, h), outputs);
    }

    /// <summary>Runs one genome on one input vector.</summary>
    /// <param name="shape">Layer sizes.</param>
    /// <param name="genome">The genome (<see cref="BrainShape.WeightCount"/> values).</param>
    /// <param name="inputs">At least <see cref="BrainShape.Inputs"/> values.</param>
    /// <param name="hidden">Scratch for the hidden activations (<see cref="BrainShape.HiddenCount"/> values); holds them afterwards.</param>
    /// <param name="outputs">Receives <see cref="BrainShape.Outputs"/> values in (−1, 1).</param>
    public static void Evaluate(BrainShape shape, ReadOnlySpan<float> genome, ReadOnlySpan<float> inputs, Span<float> hidden, Span<float> outputs)
    {
        Span<float> h1 = hidden[..shape.Hidden1];
        int o = Layer(genome, 0, inputs[..shape.Inputs], h1);
        ReadOnlySpan<float> last = h1;
        if (shape.Hidden2 > 0)
        {
            Span<float> h2 = hidden.Slice(shape.Hidden1, shape.Hidden2);
            o = Layer(genome, o, h1, h2);
            last = h2;
        }
        Layer(genome, o, last, outputs[..shape.Outputs]);
    }

    /// <summary>One dense tanh layer whose weights start at <paramref name="offset"/>; returns the offset after it.</summary>
    /// <remarks>
    /// Speed: four neurons are summed side by side. Each neuron still has its own sum, started at its bias and
    /// added to in input order, so the result is bit-identical to one neuron at a time; the four independent
    /// add chains just let the CPU overlap them instead of waiting on one chain's latency.
    /// </remarks>
    private static int Layer(ReadOnlySpan<float> genome, int offset, ReadOnlySpan<float> x, Span<float> y)
    {
        int n = x.Length, stride = n + 1, j = 0;
        for (; j + 4 <= y.Length; j += 4)
        {
            ReadOnlySpan<float> r0 = genome.Slice(offset, stride), r1 = genome.Slice(offset + stride, stride),
                r2 = genome.Slice(offset + 2 * stride, stride), r3 = genome.Slice(offset + 3 * stride, stride);
            float s0 = r0[n], s1 = r1[n], s2 = r2[n], s3 = r3[n]; // biases
            for (int i = 0; i < n; i++)
            {
                float xi = x[i];
                s0 += r0[i] * xi;
                s1 += r1[i] * xi;
                s2 += r2[i] * xi;
                s3 += r3[i] * xi;
            }
            y[j] = FastMath.Tanh(s0);
            y[j + 1] = FastMath.Tanh(s1);
            y[j + 2] = FastMath.Tanh(s2);
            y[j + 3] = FastMath.Tanh(s3);
            offset += 4 * stride;
        }
        for (; j < y.Length; j++)
        {
            ReadOnlySpan<float> row = genome.Slice(offset, stride);
            float sum = row[n]; // bias
            for (int i = 0; i < n; i++) sum += row[i] * x[i];
            y[j] = FastMath.Tanh(sum);
            offset += stride;
        }
        return offset;
    }
}
