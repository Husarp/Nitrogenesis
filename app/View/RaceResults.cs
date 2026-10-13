using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Nitrogenesis.Sim.Racing;
using Nitrogenesis.Ui;

namespace Nitrogenesis.View;

/// <summary>
/// The results of a race (PLAN §6.5), shown over the map when the player finishes: everyone's place and time (the
/// ghosts' times are those they recorded), the gap to the winner, and "Race again" / "Back". The map behind shows the
/// route of you and of every ghost; the owner zooms it to fit beside the panel (<see cref="PanelWidth"/>).
/// </summary>
public partial class RaceResults : CanvasLayer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    /// <summary>The width of the panel, px.</summary>
    public const float PanelWidth = 470;

    private List<RaceStanding> _standings = null!;

    public event Action? RestartPressed, BackPressed;

    public static RaceResults Create(List<RaceStanding> standings) => new() { _standings = standings };

    public override void _Ready()
    {
        Layer = 6;
        var root = new Control { Theme = UiTheme.Theme, MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);
        var panel = new PanelContainer { Position = new Vector2(12, 12), CustomMinimumSize = new Vector2(PanelWidth, 0) };
        root.AddChild(panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 12);
        panel.AddChild(box);
        box.AddChild(UiTheme.Label("RESULTS", 28, UiTheme.Accent));

        var table = new GridContainer { Columns = 4 };
        table.AddThemeConstantOverride("h_separation", 14);
        table.AddThemeConstantOverride("v_separation", 6);
        box.AddChild(table);
        float? winner = null;
        for (int i = 0; i < _standings.Count; i++)
        {
            var line = _standings[i];
            Color text = line.IsPlayer ? UiTheme.Accent : UiTheme.Text;
            table.AddChild(UiTheme.Label((i + 1).ToString(Inv), 18, UiTheme.Muted, HorizontalAlignment.Right));

            var name = new HBoxContainer { CustomMinimumSize = new Vector2(200, 0) };
            name.AddThemeConstantOverride("separation", 8);
            name.AddChild(new ColorRect
            {
                Color = line.IsPlayer ? UiTheme.Accent : RaceView.GhostColor(line.Ghost), CustomMinimumSize = new Vector2(12, 12),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            });
            var label = UiTheme.Label(line.Name, 18, text);
            label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            name.AddChild(label);
            table.AddChild(name);

            table.AddChild(UiTheme.Label(line.Finished ? line.Seconds.ToString("0.00", Inv) + " s" : $"no finish · {(line.Progress * 100).ToString("0", Inv)} %", 18, text, HorizontalAlignment.Right));
            string gap = "";
            if (line.Finished)
            {
                winner ??= line.Seconds;
                if (i > 0) gap = "+" + (line.Seconds - winner.Value).ToString("0.00", Inv) + " s";
            }
            table.AddChild(UiTheme.Label(gap, 16, UiTheme.Muted, HorizontalAlignment.Right));
        }

        box.AddChild(UiTheme.Label("The map shows the route of every car.", 14, UiTheme.Muted));
        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 10);
        box.AddChild(buttons);
        buttons.AddChild(UiTheme.SmallButton("Race again  (R)", () => RestartPressed?.Invoke(), 18));
        buttons.AddChild(UiTheme.SmallButton("Back  (Esc)", () => BackPressed?.Invoke(), 18));
    }
}
