namespace Nitrogenesis.Sim.Brain;

/// <summary>
/// Layer sizes of the fixed-size MLP brain (PLAN §3.5): inputs → hidden 1 (tanh) → optional hidden 2 (tanh)
/// → outputs (tanh). Default 22 → 12 → 2.
/// </summary>
public readonly record struct BrainShape
{
    public const int DefaultHidden = 12;
    public const int MinHidden = 4;
    public const int MaxHidden = 32;

    /// <param name="inputs">Sensor count of the mode (≥ 1).</param>
    /// <param name="hidden1">First hidden layer, 4…32.</param>
    /// <param name="hidden2">Second hidden layer, 4…32, or 0 for none.</param>
    /// <param name="outputs">Values the mode reads (≥ 1).</param>
    public BrainShape(int inputs, int hidden1, int hidden2, int outputs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(inputs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputs, 1);
        if (hidden1 < MinHidden || hidden1 > MaxHidden) throw new ArgumentOutOfRangeException(nameof(hidden1));
        if (hidden2 != 0 && (hidden2 < MinHidden || hidden2 > MaxHidden)) throw new ArgumentOutOfRangeException(nameof(hidden2));
        Inputs = inputs;
        Hidden1 = hidden1;
        Hidden2 = hidden2;
        Outputs = outputs;
    }

    public int Inputs { get; }
    public int Hidden1 { get; }
    /// <summary>0 when there is no second hidden layer.</summary>
    public int Hidden2 { get; }
    public int Outputs { get; }

    /// <summary>Neurons in the last hidden layer (the one feeding the outputs).</summary>
    public int LastHidden => Hidden2 > 0 ? Hidden2 : Hidden1;

    /// <summary>Hidden neurons in total (scratch space one evaluation needs).</summary>
    public int HiddenCount => Hidden1 + Hidden2;

    /// <summary>
    /// Length of one genome: every layer's weights plus one bias per neuron (layout in <see cref="Mlp"/>).
    /// Default 22 → 12 → 2: 23·12 + 13·2 = 302.
    /// </summary>
    public int WeightCount =>
        (Inputs + 1) * Hidden1 + (Hidden2 > 0 ? (Hidden1 + 1) * Hidden2 : 0) + (LastHidden + 1) * Outputs;

    /// <summary>Index in a genome of the bias of output neuron <paramref name="output"/>.</summary>
    public int OutputBiasIndex(int output)
    {
        if ((uint)output >= (uint)Outputs) throw new ArgumentOutOfRangeException(nameof(output));
        int outputLayer = WeightCount - (LastHidden + 1) * Outputs;
        return outputLayer + output * (LastHidden + 1) + LastHidden;
    }

    /// <summary>The default shape for a mode: one hidden layer of <see cref="DefaultHidden"/>.</summary>
    public static BrainShape Default(int inputs, int outputs) => new(inputs, DefaultHidden, 0, outputs);
}
