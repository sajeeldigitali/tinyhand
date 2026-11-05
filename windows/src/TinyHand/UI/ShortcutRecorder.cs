using System.Windows;
using System.Windows.Input;

namespace TinyHand.UI;

/// <summary>Shows a shortcut as keycaps; click it, then press new keys to change it. Esc cancels.</summary>
internal sealed class ShortcutRecorder : Clickable
{
    private Shortcut shortcut;
    private readonly Func<Shortcut, bool> onChange;
    private readonly Action<bool>? onRecording;
    private bool recording;

    public ShortcutRecorder(Shortcut shortcut, Func<Shortcut, bool> onChange, Action<bool>? onRecording)
        : base(Theme.Primary(0.05), Theme.Primary(0.07), Theme.Primary(0.09))
    {
        this.shortcut = shortcut;
        this.onChange = onChange;
        this.onRecording = onRecording;
        Width = 132;
        Height = 30;
        CornerRadius = new CornerRadius(7);
        Focusable = true;
        FocusVisualStyle = null;
        ToolTip = "Click, then press the new shortcut. Esc cancels.";
        Click = Start;
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += OnKeyUp;
        LostKeyboardFocus += (_, _) => Stop();
        SetLabel(shortcut.Display);
    }

    private void Start()
    {
        if (recording)
        {
            return;
        }
        recording = true;
        SetBrushes(Theme.Primary(0.09), Theme.Primary(0.09));
        SetLabel("Press keys", dim: true);
        onRecording?.Invoke(true);
        Keyboard.Focus(this);
    }

    private void Stop()
    {
        if (!recording)
        {
            return;
        }
        recording = false;
        SetBrushes(Theme.Primary(0.05), Theme.Primary(0.07), Theme.Primary(0.09));
        SetLabel(shortcut.Display);
        onRecording?.Invoke(false);
        if (IsKeyboardFocused)
        {
            Keyboard.ClearFocus();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!recording)
        {
            return;
        }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.ImeProcessed)
        {
            key = e.ImeProcessedKey;
        }
        if (key == Key.Escape)
        {
            Stop();
            return;
        }
        var mods = Keyboard.Modifiers;
        if (IsModifier(key))
        {
            mods |= ModifierFor(key);
            SetCaps(ModifierNames(mods));
            return;
        }
        if ((mods & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
        {
            SetLabel("Add Ctrl or Alt", dim: true);
            return;
        }
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
        {
            return;
        }
        uint flags = 0;
        if (mods.HasFlag(ModifierKeys.Control))
        {
            flags |= Native.MOD_CONTROL;
        }
        if (mods.HasFlag(ModifierKeys.Alt))
        {
            flags |= Native.MOD_ALT;
        }
        if (mods.HasFlag(ModifierKeys.Shift))
        {
            flags |= Native.MOD_SHIFT;
        }
        if (mods.HasFlag(ModifierKeys.Windows))
        {
            flags |= Native.MOD_WIN;
        }
        var names = ModifierNames(mods);
        names.Add(KeyName(key, vk));
        var s = new Shortcut(vk, flags, string.Join("+", names));
        if (onChange(s))
        {
            shortcut = s;
        }
        Stop();
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (!recording)
        {
            return;
        }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!IsModifier(key))
        {
            return;
        }
        var mods = Keyboard.Modifiers & ~ModifierFor(key);
        if (mods == ModifierKeys.None)
        {
            SetLabel("Press keys", dim: true);
        }
        else
        {
            SetCaps(ModifierNames(mods));
        }
    }

    private static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static ModifierKeys ModifierFor(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl => ModifierKeys.Control,
        Key.LeftAlt or Key.RightAlt => ModifierKeys.Alt,
        Key.LeftShift or Key.RightShift => ModifierKeys.Shift,
        Key.LWin or Key.RWin => ModifierKeys.Windows,
        _ => ModifierKeys.None,
    };

    private static List<string> ModifierNames(ModifierKeys mods)
    {
        var names = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control))
        {
            names.Add("Ctrl");
        }
        if (mods.HasFlag(ModifierKeys.Alt))
        {
            names.Add("Alt");
        }
        if (mods.HasFlag(ModifierKeys.Shift))
        {
            names.Add("Shift");
        }
        if (mods.HasFlag(ModifierKeys.Windows))
        {
            names.Add("Win");
        }
        return names;
    }

    private static string KeyName(Key key, uint vk)
    {
        if (key >= Key.A && key <= Key.Z)
        {
            return key.ToString();
        }
        if (key >= Key.D0 && key <= Key.D9)
        {
            return ((char)('0' + (key - Key.D0))).ToString();
        }
        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            return "Num " + (char)('0' + (key - Key.NumPad0));
        }
        if (key >= Key.F1 && key <= Key.F24)
        {
            return key.ToString();
        }
        switch (key)
        {
            case Key.Space: return "Space";
            case Key.Enter: return "Enter";
            case Key.Tab: return "Tab";
            case Key.Back: return "Backspace";
            case Key.Delete: return "Delete";
            case Key.Left: return "←";
            case Key.Right: return "→";
            case Key.Up: return "↑";
            case Key.Down: return "↓";
            case Key.Home: return "Home";
            case Key.End: return "End";
            case Key.PageUp: return "PgUp";
            case Key.PageDown: return "PgDn";
            case Key.Insert: return "Insert";
        }
        uint ch = Native.MapVirtualKey(vk, 2) & 0x7FFF;
        if (ch != 0)
        {
            var name = char.ToUpperInvariant((char)ch).ToString();
            return name == "+" ? "Plus" : name;
        }
        return key.ToString();
    }

    private void SetLabel(string text, bool dim = false)
    {
        if (dim)
        {
            var label = Ui.Text(text, 12, FontWeights.Medium, Theme.Secondary);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            Child = label;
            return;
        }
        SetCaps(text.Split('+', StringSplitOptions.RemoveEmptyEntries));
    }

    private void SetCaps(IEnumerable<string> caps)
    {
        var row = Ui.Keycaps(caps);
        row.VerticalAlignment = VerticalAlignment.Center;
        Child = row;
    }
}
