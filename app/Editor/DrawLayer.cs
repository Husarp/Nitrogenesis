using System;
using Godot;

namespace Nitrogenesis.Editor;

/// <summary>A Node2D that draws itself with a callback: the editor's overlay layers (grid, brush preview, start arrow).</summary>
public partial class DrawLayer : Node2D
{
    public Action<DrawLayer>? OnDraw;

    public override void _Draw() => OnDraw?.Invoke(this);
}
