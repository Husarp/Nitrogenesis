using System;
using Godot;

namespace Nitrogenesis.Ui;

/// <summary>
/// A modal box in the app's look: the screen dims, a panel with a title, an optional body and buttons sits in the
/// middle and takes every click. A button closes the box, then runs its action. Esc closes it (like Cancel); Enter
/// presses the primary button (unless a text field takes the key).
/// </summary>
public partial class Modal : Control
{
    /// <summary>One button: its text, what it does after the box closes (null = just close), and whether Enter presses it.</summary>
    public readonly record struct Choice(string Text, Action? Action = null, bool Primary = false, bool Disabled = false, string? Tooltip = null);

    private string _title = "";
    private Control? _body;
    private Choice[] _choices = [];
    private Button? _primary;
    private bool _closed;

    /// <summary>Modals open right now (views ignore their own keys while one is open).</summary>
    public static int OpenCount { get; private set; }

    /// <summary>True while the editor's "Unsaved changes" question is the newest open box.</summary>
    public static bool UnsavedQuestionOpen => Instances.Count > 0 && Instances[^1]._title == "Unsaved changes";

    /// <summary>Closes every open box without running any button action.</summary>
    public static void CloseAll()
    {
        foreach (var m in Instances.ToArray()) m.Close();
    }

    private static readonly System.Collections.Generic.List<Modal> Instances = new();
    private readonly System.Collections.Generic.List<Button> _buttons = new();

    /// <summary>Debug scripts: presses the button with this text (ignoring case) on the newest open box. False when there is none.</summary>
    public static bool DebugPress(string text)
    {
        if (Instances.Count == 0) return false;
        foreach (var b in Instances[^1]._buttons)
        {
            if (!string.Equals(b.Text, text, StringComparison.OrdinalIgnoreCase) || b.Disabled) continue;
            b.EmitSignal(BaseButton.SignalName.Pressed);
            return true;
        }
        return false;
    }

    /// <summary>Raised when the box closes (by a button or Esc).</summary>
    public event Action? Closed;

    /// <summary>Opens a box over <paramref name="parent"/> (a full-screen control or a canvas layer).</summary>
    public static Modal Open(Node parent, string title, Control? body, params Choice[] choices)
    {
        var modal = new Modal { _title = title, _body = body, _choices = choices };
        parent.AddChild(modal);
        return modal;
    }

    /// <summary>A message with an OK button.</summary>
    public static Modal Message(Node parent, string title, string text) =>
        Open(parent, title, Text(text), new Choice("OK", null, true));

    /// <summary>A wrapping text body, about 460 px wide.</summary>
    public static Label Text(string text, Color? color = null)
    {
        var label = UiTheme.Label(text, 16, color ?? UiTheme.Text);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2(460, 0);
        return label;
    }

    public override void _Ready()
    {
        OpenCount++;
        Instances.Add(this);
        Theme = UiTheme.Theme;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = MouseFilterEnum.Ignore };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dim);
        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);
        var panel = new PanelContainer();
        center.AddChild(panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 14);
        panel.AddChild(box);
        box.AddChild(UiTheme.Label(_title, 22, UiTheme.Accent));
        if (_body is not null) box.AddChild(_body);
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        buttons.AddThemeConstantOverride("separation", 10);
        box.AddChild(buttons);
        foreach (var choice in _choices)
        {
            var c = choice;
            var button = new Button { Text = c.Text, CustomMinimumSize = new Vector2(110, 40), Disabled = c.Disabled, TooltipText = c.Tooltip ?? "" };
            button.Pressed += () => Close(c.Action);
            buttons.AddChild(button);
            _buttons.Add(button);
            if (c.Primary) _primary = button;
        }
        (_primary ?? (buttons.GetChildCount() > 0 ? buttons.GetChild<Button>(0) : null))?.CallDeferred(Control.MethodName.GrabFocus);
    }

    public override void _ExitTree()
    {
        Instances.Remove(this);
        if (!_closed)
        {
            _closed = true;
            OpenCount--;
        }
    }

    /// <summary>Closes the box, then runs <paramref name="then"/>.</summary>
    public void Close(Action? then = null)
    {
        if (_closed) return;
        _closed = true;
        OpenCount--;
        Instances.Remove(this);
        QueueFree();
        Closed?.Invoke();
        then?.Invoke();
    }

    public override void _Input(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (key.Keycode == Key.Escape)
        {
            GetViewport().SetInputAsHandled();
            Close();
        }
        else if (key.Keycode is Key.Enter or Key.KpEnter && _primary is { Disabled: false } && GetViewport().GuiGetFocusOwner() is not LineEdit)
        {
            GetViewport().SetInputAsHandled();
            _primary.EmitSignal(BaseButton.SignalName.Pressed);
        }
    }
}
