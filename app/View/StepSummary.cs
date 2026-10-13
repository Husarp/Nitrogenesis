using System;
using System.Globalization;
using Godot;
using Nitrogenesis.Sim.Racing;
using Nitrogenesis.Ui;

namespace Nitrogenesis.View;

/// <summary>
/// Mode Step (PLAN §6.2): the summary of the generation that just ended, with a "Next" button that starts the next
/// one (Enter), and a shortcut back to Auto.
/// </summary>
public partial class StepSummary : PanelContainer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private Label _title = null!;
    private Label _finished = null!, _bestTime = null!, _bestScore = null!, _average = null!, _progress = null!, _mutation = null!;
    private int _shown = -1;

    public event Action? NextPressed, AutoPressed;

    public override void _Ready()
    {
        Theme = UiTheme.Theme;
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        AddChild(box);
        _title = UiTheme.Label("", 22, UiTheme.Accent);
        box.AddChild(_title);
        var grid = new GridContainer { Columns = 4 };
        grid.AddThemeConstantOverride("h_separation", 18);
        grid.AddThemeConstantOverride("v_separation", 4);
        box.AddChild(grid);
        _finished = Cell(grid, "Finished");
        _bestTime = Cell(grid, "Best time");
        _bestScore = Cell(grid, "Best score");
        _average = Cell(grid, "Average score");
        _progress = Cell(grid, "Best progress");
        _mutation = Cell(grid, "Next bred with");
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        buttons.AddThemeConstantOverride("separation", 10);
        var auto = new Button { Text = "Back to Auto", FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(140, 40) };
        auto.Pressed += () => AutoPressed?.Invoke();
        auto.TooltipText = "Mode Auto: the next generation starts at once (M)";
        var next = new Button { Text = "Next  (Enter)", FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(160, 40) };
        next.Pressed += () => NextPressed?.Invoke();
        buttons.AddChild(auto);
        buttons.AddChild(next);
        box.AddChild(buttons);
    }

    private static Label Cell(GridContainer grid, string name)
    {
        grid.AddChild(UiTheme.Label(name, 15, UiTheme.Muted));
        var value = UiTheme.Label("—", 15, UiTheme.Text);
        value.CustomMinimumSize = new Vector2(140, 0);
        grid.AddChild(value);
        return value;
    }

    /// <summary>Shows the summary of the last generation (texts change only when another generation ended).</summary>
    /// <param name="startDistance">The track's start distance (cells), to show progress as a share.</param>
    /// <param name="mutation">How the waiting generation was bred (σ, chance, boost).</param>
    public void Show(in TrainingStats s, float startDistance, string mutation)
    {
        if (s.LastGeneration == _shown) return;
        _shown = s.LastGeneration;
        var l = s.LastStats;
        _title.Text = $"Generation {s.LastGeneration} ended";
        _finished.Text = $"{l.FinishedCount} of {s.PopulationSize}";
        _bestTime.Text = l.FinishedCount > 0 ? (l.BestFinishTicks / RacingSettings.TicksPerSecond).ToString("0.00", Inv) + " s" : "—";
        _bestScore.Text = l.BestScore.ToString("0.000", Inv);
        _average.Text = l.AverageScore.ToString("0.000", Inv);
        _progress.Text = startDistance > 0 ? (Math.Min(1f, l.BestProgress / startDistance) * 100f).ToString("0", Inv) + " %" : "—";
        _mutation.Text = mutation;
    }
}
