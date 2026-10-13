namespace Nitrogenesis.Sim.Brain;

/// <summary>What one genome computes for one input vector, layer by layer, for the brain view (PLAN §6.2).</summary>
public static class BrainProbe
{
    /// <summary>Length of the values array: the inputs, the hidden neurons of each layer, then the outputs.</summary>
    public static int ValueCount(BrainShape shape) => shape.Inputs + shape.HiddenCount + shape.Outputs;

    /// <summary>
    /// Fills <paramref name="values"/> with the inputs, then hidden layer 1, hidden layer 2 (if any) and the outputs,
    /// computed with <see cref="Mlp.Evaluate"/> (the very code that drives the cars).
    /// </summary>
    public static void Fill(BrainShape shape, ReadOnlySpan<float> genome, ReadOnlySpan<float> inputs, Span<float> values)
    {
        inputs[..shape.Inputs].CopyTo(values);
        Mlp.Evaluate(shape, genome, inputs, values.Slice(shape.Inputs, shape.HiddenCount), values.Slice(shape.Inputs + shape.HiddenCount, shape.Outputs));
    }
}
