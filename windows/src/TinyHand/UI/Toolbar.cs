using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using TinyHand.Core;
using TinyHand.Hands;

namespace TinyHand.UI;

/// <summary>
/// The small dark bar at the bottom of the screen while the pointer is on: pinned pointers, ink colour,
/// tap sound and exit, plus the ink colour tray and the first-run hint. A port of Toolbar in the macOS app.
/// </summary>
internal sealed class Toolbar
{
    private const double ShadowMargin = 14;
    private const uint Move = Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER;

    public Action<PointerStyle>? OnPick { get; set; }
    public Action? OnToggleSound { get; set; }
    public Action? OnExit { get; set; }
    public Action? OnWarning { get; set; }

    private readonly Window bar;
    private readonly IntPtr barHandle;
    private readonly Border barGlass;
    private readonly StackPanel stack = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<FrameworkElement> fixedItems;
    private readonly Dictionary<PointerStyle, Clickable> styleButtons = [];
    private List<PointerStyle> shownStyles = [];
    private PointerStyle? current;
    private readonly Clickable inkButton;
    private readonly Clickable soundButton;
    private readonly TextBlock soundGlyph;
    private readonly Clickable warning;

    private readonly Window tray;
    private readonly IntPtr trayHandle;
    private readonly Border trayGlass;
    private readonly StackPanel trayStack = new() { Orientation = Orientation.Horizontal };
    private readonly Dictionary<InkChoice, Clickable> swatches = [];
    private bool inkOpen;

    private Window? hint;
    private Border? hintGlass;

    private double anchorX;
    private int anchorBottom;
    private bool anchored;

    public bool Revealed { get; private set; }
    public IntPtr Handle => barHandle;
    public FrameworkElement BarContent => barGlass;
    public FrameworkElement? HintContent => hintGlass;

    public FrameworkElement TrayContent
    {
        get
        {
            EnsureSwatches();
            RefreshInk();
            return trayGlass;
        }
    }

    public Toolbar()
    {
        barGlass = Glass(stack, 16, new Thickness(10, 8, 10, 8));
        barGlass.IsHitTestVisible = false;
        bar = MakeWindow("TinyHand toolbar", barGlass, clickThrough: false);
        barHandle = new WindowInteropHelper(bar).EnsureHandle();
        bar.SizeChanged += (_, _) => Reanchor();

        trayGlass = Glass(trayStack, 14, new Thickness(6, 5, 6, 5));
        tray = MakeWindow("TinyHand ink", trayGlass, clickThrough: false);
        trayHandle = new WindowInteropHelper(tray).EnsureHandle();

        var divider = new Border { Width = 1, Height = 24, Background = White(0.18), VerticalAlignment = VerticalAlignment.Center };
        inkButton = MakeButton(new Grid(), "Ink colour", 40, () => SetInkOpen(!inkOpen));
        soundGlyph = Ui.Glyph("", 15, White(0.85));
        soundButton = MakeButton(soundGlyph, "Tap sound", 40, () => OnToggleSound?.Invoke());
        var exit = MakeButton(Ui.Glyph("", 14, White(0.85)), "Exit  (Esc)", 40, () => OnExit?.Invoke());
        warning = new Clickable(Theme.Solid(Color.FromRgb(0xFF, 0x9F, 0x0A), 0.9), Theme.Solid(Color.FromRgb(0xFF, 0x9F, 0x0A)))
        {
            Height = 32,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Ui.HStack(0, Ui.Glyph("", 12, Brushes.Black), Ui.Text("  Clicks can't be caught  ", 12, FontWeights.SemiBold, Brushes.Black)),
            ToolTip = "TinyHand couldn't start catching clicks.",
            Click = () => OnWarning?.Invoke(),
            Visibility = Visibility.Collapsed,
        };
        fixedItems = [divider, inkButton, soundButton, exit, warning];

        RebuildStyles();
        RefreshInk();
        Preferences.Changed += () =>
        {
            RebuildStyles();
            RefreshInk();
        };
    }

    public Native.RECT Frame
    {
        get
        {
            if (!bar.IsVisible)
            {
                return default;
            }
            var frame = Inner(barHandle, bar);
            if (inkOpen && tray.IsVisible)
            {
                frame = frame.Union(Inner(trayHandle, tray));
            }
            return frame;
        }
    }

    public void Select(PointerStyle style)
    {
        current = style;
        RebuildStyles();
        Highlight(style);
        RefreshInk();
    }

    public void SetSoundOn(bool on)
    {
        soundGlyph.Text = on ? "" : "";
        soundGlyph.Foreground = White(on ? 0.85 : 0.45);
        var tip = on ? "Tap sound on — click to mute" : "Tap sound off — click to unmute";
        soundButton.ToolTip = tip;
        AutomationProperties.SetName(soundButton, tip);
    }

    public void SetWarning(bool needed)
    {
        var visibility = needed ? Visibility.Visible : Visibility.Collapsed;
        if (warning.Visibility == visibility)
        {
            return;
        }
        warning.Visibility = visibility;
        Space();
    }

    public void Place(Monitor monitor)
    {
        SetInkOpen(false);
        Native.SetWindowPos(barHandle, Native.HWND_TOPMOST, monitor.Work.Left + 20, monitor.Work.Top + 20, 0, 0, Move);
        bar.UpdateLayout();
        anchorX = monitor.Bounds.Left + monitor.Bounds.Width / 2.0;
        anchorBottom = monitor.Work.Bottom - (int)Math.Round(14 * monitor.Scale);
        anchored = true;
        Reanchor();
    }

    public void Show()
    {
        bar.BeginAnimation(UIElement.OpacityProperty, null);
        bar.Opacity = Revealed ? 1 : 0;
        barGlass.IsHitTestVisible = Revealed;
        if (!bar.IsVisible)
        {
            bar.Show();
        }
        bar.UpdateLayout();
        Reanchor();
        Native.SetWindowPos(barHandle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Move);
    }

    public void Hide()
    {
        SetInkOpen(false);
        HideHint();
        Revealed = false;
        barGlass.IsHitTestVisible = false;
        bar.BeginAnimation(UIElement.OpacityProperty, null);
        bar.Opacity = 0;
        bar.Hide();
    }

    public void SetRevealed(bool value)
    {
        if (value == Revealed)
        {
            return;
        }
        Revealed = value;
        if (!value)
        {
            SetInkOpen(false);
        }
        barGlass.IsHitTestVisible = value;
        Fade(bar, value ? 1 : 0, value ? 0.15 : 0.35);
    }

    public void ShowHint(IReadOnlyList<(string Key, string Text)> parts)
    {
        HideHint();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (key, text) in parts)
        {
            var label = new TextBlock { FontFamily = Theme.Font, FontSize = 12.5, Foreground = Brushes.White };
            label.Inlines.Add(new Run(key) { FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            label.Inlines.Add(new Run(" " + text) { Foreground = White(0.7) });
            Ui.Add(row, label, 14);
        }
        var glass = Glass(row, 12, new Thickness(16, 9, 16, 9));
        glass.IsHitTestVisible = false;
        var w = MakeWindow("TinyHand hint", glass, clickThrough: true);
        var handle = new WindowInteropHelper(w).EnsureHandle();
        w.Show();
        if (bar.IsVisible)
        {
            var glassRect = Inner(barHandle, bar);
            PlaceAbove(w, handle, glassRect.Left + glassRect.Width / 2.0, glassRect.Top, 12);
        }
        Fade(w, 1, 0.2);
        hint = w;
        hintGlass = glass;
    }

    public void HideHint()
    {
        var w = hint;
        if (w is null)
        {
            return;
        }
        hint = null;
        hintGlass = null;
        Fade(w, 0, 0.3, w.Close);
    }

    public Native.POINT? Center(PointerStyle style)
    {
        if (!styleButtons.TryGetValue(style, out var button) || !button.IsVisible)
        {
            return null;
        }
        try
        {
            var p = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
            return new Native.POINT((int)Math.Round(p.X), (int)Math.Round(p.Y));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void SetInkOpen(bool open)
    {
        if (open == inkOpen)
        {
            return;
        }
        inkOpen = open;
        if (open)
        {
            EnsureSwatches();
            RefreshInk();
            tray.BeginAnimation(UIElement.OpacityProperty, null);
            tray.Opacity = 0;
            if (!tray.IsVisible)
            {
                tray.Show();
            }
            tray.UpdateLayout();
            try
            {
                var center = inkButton.PointToScreen(new Point(inkButton.ActualWidth / 2, inkButton.ActualHeight / 2));
                PlaceAbove(tray, trayHandle, center.X, Inner(barHandle, bar).Top, 8);
            }
            catch (InvalidOperationException)
            {
            }
            Fade(tray, 1, 0.12);
        }
        else
        {
            Fade(tray, 0, 0.12, () =>
            {
                if (!inkOpen)
                {
                    tray.Hide();
                }
            });
        }
        RefreshInk();
    }

    private void EnsureSwatches()
    {
        if (swatches.Count > 0)
        {
            return;
        }
        foreach (var choice in Enum.GetValues<InkChoice>())
        {
            var picked = choice;
            var button = MakeButton(Ui.InkDot(choice.Color(), 19), choice.Title(), 30, () =>
            {
                Preferences.InkChoice = picked;
                SetInkOpen(false);
            });
            swatches[choice] = button;
            Ui.Add(trayStack, button, 2);
        }
    }

    private void RefreshInk()
    {
        var style = current ?? Preferences.SavedStyle;
        inkButton.Child = Ui.InkDot(Preferences.InkChoice.Resolved(style), 25);
        inkButton.SetBrushes(inkOpen ? White(0.18) : Brushes.Transparent, White(inkOpen ? 0.22 : 0.10), White(0.26));
        var visibility = Preferences.ShowInkButton ? Visibility.Visible : Visibility.Collapsed;
        if (inkButton.Visibility != visibility)
        {
            inkButton.Visibility = visibility;
            if (visibility == Visibility.Collapsed)
            {
                SetInkOpen(false);
            }
            Space();
        }
        foreach (var (choice, button) in swatches)
        {
            bool selected = choice == Preferences.InkChoice;
            button.SetBrushes(selected ? White(0.2) : Brushes.Transparent, White(selected ? 0.24 : 0.10), White(0.28));
        }
    }

    private void RebuildStyles()
    {
        var styles = Preferences.PinnedStyles;
        if (current is PointerStyle c && !styles.Contains(c))
        {
            styles.Add(c);
        }
        if (styles.SequenceEqual(shownStyles))
        {
            return;
        }
        shownStyles = styles;
        styleButtons.Clear();
        stack.Children.Clear();
        foreach (var style in styles)
        {
            var picked = style;
            var button = MakeButton(StyleIcon(style), Pointers.Title(style), 40, () => OnPick?.Invoke(picked));
            styleButtons[style] = button;
            stack.Children.Add(button);
        }
        foreach (var item in fixedItems)
        {
            stack.Children.Add(item);
        }
        Space();
        if (current is PointerStyle selected)
        {
            Highlight(selected);
        }
    }

    private void Highlight(PointerStyle style)
    {
        foreach (var (s, button) in styleButtons)
        {
            bool on = s == style;
            button.SetBrushes(on ? White(0.18) : Brushes.Transparent, White(on ? 0.22 : 0.10), White(0.26));
        }
    }

    /// <summary>Spaces visible items 6 apart, skipping hidden ones as NSStackView does.</summary>
    private void Space()
    {
        bool first = true;
        foreach (FrameworkElement child in stack.Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }
            child.Margin = new Thickness(first ? 0 : 6, 0, 0, 0);
            first = false;
        }
    }

    private void Reanchor()
    {
        if (!anchored)
        {
            return;
        }
        Native.GetWindowRect(barHandle, out var r);
        int margin = (int)Math.Round(ShadowMargin * Ui.DpiScale(bar));
        int x = (int)Math.Round(anchorX - r.Width / 2.0);
        int y = anchorBottom + margin - r.Height;
        if (x != r.Left || y != r.Top)
        {
            Native.SetWindowPos(barHandle, Native.HWND_TOPMOST, x, y, 0, 0, Move);
        }
    }

    /// <summary>Puts a window's glass <paramref name="gapDip"/> above <paramref name="top"/>, centred on <paramref name="centerX"/>.</summary>
    private static void PlaceAbove(Window w, IntPtr handle, double centerX, int top, double gapDip)
    {
        Native.SetWindowPos(handle, Native.HWND_TOPMOST, (int)centerX, top - 20, 0, 0, Move);
        w.UpdateLayout();
        Native.GetWindowRect(handle, out var r);
        double scale = Ui.DpiScale(w);
        int margin = (int)Math.Round(ShadowMargin * scale);
        int gap = (int)Math.Round(gapDip * scale);
        int x = (int)Math.Round(centerX - r.Width / 2.0);
        int y = top - gap - r.Height + margin;
        Native.SetWindowPos(handle, Native.HWND_TOPMOST, x, y, 0, 0, Move);
    }

    private static Native.RECT Inner(IntPtr handle, Visual visual)
    {
        Native.GetWindowRect(handle, out var r);
        int margin = (int)Math.Round(ShadowMargin * Ui.DpiScale(visual));
        return r.Inflate(-margin, -margin);
    }

    private static FrameworkElement StyleIcon(PointerStyle style) =>
        Pointers.AssetName(style) is string name ? Ui.Picture(HandAssets.Thumbnail(name), 40) : Ui.LaserIcon(40);

    private static Clickable MakeButton(UIElement content, string tip, double side, Action action)
    {
        var button = new Clickable(Brushes.Transparent, White(0.10), White(0.14))
        {
            Width = side,
            Height = side,
            CornerRadius = new CornerRadius(side >= 40 ? 10 : side / 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = content,
            ToolTip = tip,
            Click = action,
        };
        AutomationProperties.SetName(button, tip);
        return button;
    }

    private static Border Glass(UIElement content, double radius, Thickness padding) => new()
    {
        Background = Theme.Solid(Color.FromArgb(0xE8, 0x1C, 0x1C, 0x1E)),
        BorderBrush = White(0.12),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(radius),
        Padding = padding,
        Margin = new Thickness(ShadowMargin),
        Child = content,
        Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.35 },
    };

    private static Window MakeWindow(string title, UIElement content, bool clickThrough)
    {
        var w = new Window
        {
            Title = title,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Focusable = false,
            Opacity = 0,
            Content = content,
        };
        w.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(w).Handle;
            Native.SetExStyle(handle, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | (clickThrough ? Native.WS_EX_TRANSPARENT : 0));
        };
        return w;
    }

    private static void Fade(Window w, double to, double seconds, Action? done = null)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromSeconds(seconds));
        if (done is not null)
        {
            animation.Completed += (_, _) => done();
        }
        w.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private static SolidColorBrush White(double opacity) => Theme.Solid(Colors.White, opacity);
}
