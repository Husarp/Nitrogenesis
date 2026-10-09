using System;
using Godot;

namespace Nitrogenesis.View;

/// <summary>
/// The view's camera (PLAN §6.2): zoom with the wheel (around the cursor), pan by dragging or with WASD/arrows,
/// follow the best car (default), a selected car, or nothing (free). A left click that does not drag is reported
/// through <see cref="Clicked"/> (the view picks a car with it). World units are cells; 100 % zoom = 6 px per cell
/// (PLAN §3.1).
/// </summary>
public partial class ViewCamera : Camera2D
{
    public const float PixelsPerCell = 6f;
    public const float MinZoom = 0.25f, MaxZoom = 8f;
    private const float WheelStep = 1.15f;
    private const float PanSpeed = 900f; // screen px per second
    private const float FollowRate = 8f; // 1/s, exponential approach
    /// <summary>A press that moves less than this (screen px) before its release is a click, not a drag.</summary>
    private const float ClickSlop = 5f;

    private float _zoom = 1f;
    private bool _pressed, _dragging, _leftPress;
    private Vector2 _pressAt;

    public enum CameraMode { FollowBest, FollowSelected, Free }

    public CameraMode Mode { get; set; } = CameraMode.FollowBest;

    /// <summary>A left click (press and release without dragging) at this screen position.</summary>
    public event Action<Vector2>? Clicked;

    /// <summary>Cells per screen pixel at the current zoom.</summary>
    public float WorldPerPixel => 1f / Zoom.X;

    /// <summary>Zoom where 1 = 100 % (6 px per cell).</summary>
    public float UserZoom
    {
        get => _zoom;
        set
        {
            _zoom = Mathf.Clamp(value, MinZoom, MaxZoom);
            Zoom = Vector2.One * (PixelsPerCell * _zoom);
        }
    }

    public override void _Ready()
    {
        UserZoom = _zoom;
        PositionSmoothingEnabled = false;
    }

    private int _followed = -1, _followedGeneration = -1;
    private Vector2 _lastTarget;

    /// <summary>
    /// Follows a car (call every frame with its interpolated position). The camera moves rigidly with the car, so it
    /// keeps up at any speed; when the followed car changes it glides over while already moving with the new car
    /// (<paramref name="motion"/>: the new car's own move since the last frame, when the caller knows it); a new
    /// generation snaps.
    /// </summary>
    public void Follow(Vector2 target, int car, int generation, double delta, Vector2? motion = null)
    {
        if (Mode == CameraMode.Free)
        {
            _followed = -1;
            return;
        }
        if (generation != _followedGeneration || _followed < 0 && _followedGeneration < 0) Position = target;
        else if (car == _followed) Position += target - _lastTarget;
        else if (motion is { } m) Position += m;
        Position = Position.Lerp(target, 1f - Mathf.Exp(-FollowRate * (float)delta));
        _followed = car;
        _followedGeneration = generation;
        _lastTarget = target;
    }

    /// <summary>WASD/arrow panning (only while not driving; the view decides).</summary>
    public void PanWithKeys(double delta)
    {
        var dir = Vector2.Zero;
        if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) dir.X -= 1;
        if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) dir.X += 1;
        if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) dir.Y -= 1;
        if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) dir.Y += 1;
        if (dir == Vector2.Zero) return;
        Mode = CameraMode.Free;
        Position += dir.Normalized() * PanSpeed * (float)delta / Zoom.X;
    }

    /// <summary>Wheel zoom and drag pan. Returns true when the event was used.</summary>
    public bool HandleInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel:
                ZoomAt(wheel.Position, wheel.ButtonIndex == MouseButton.WheelUp ? WheelStep : 1f / WheelStep);
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left or MouseButton.Right or MouseButton.Middle } button:
                if (button.Pressed)
                {
                    _pressed = true;
                    _dragging = false;
                    _leftPress = button.ButtonIndex == MouseButton.Left;
                    _pressAt = button.Position;
                }
                else if (_pressed)
                {
                    bool click = !_dragging && _leftPress;
                    _pressed = _dragging = false;
                    if (click) Clicked?.Invoke(button.Position);
                }
                return true;
            case InputEventMouseMotion motion when _pressed:
                if (!_dragging && motion.Position.DistanceTo(_pressAt) >= ClickSlop)
                {
                    _dragging = true;
                    Mode = CameraMode.Free;
                    Position -= (motion.Position - _pressAt - motion.Relative) / Zoom.X; // the part moved before the drag began
                }
                if (_dragging) Position -= motion.Relative / Zoom.X;
                return true;
        }
        return false;
    }

    /// <summary>Zooms keeping the world point under <paramref name="screen"/> in place (in free mode).</summary>
    private void ZoomAt(Vector2 screen, float factor)
    {
        Vector2 before = ScreenToWorld(screen);
        UserZoom = _zoom * factor;
        if (Mode == CameraMode.Free) Position += before - ScreenToWorld(screen);
    }

    /// <summary>The world point (cells) under a screen position.</summary>
    public Vector2 ScreenToWorld(Vector2 screen) => Position + (screen - GetViewportRect().Size / 2) / Zoom.X;

    /// <summary>The screen position of a world point (cells).</summary>
    public Vector2 WorldToScreen(Vector2 world) => (world - Position) * Zoom.X + GetViewportRect().Size / 2;
}
