using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace TinyHand.UI;

/// <summary>The first-run card that says how to start the pointer. A port of WelcomeWindow in the macOS app.</summary>
internal sealed class WelcomeWindow
{
    private const double ShadowMargin = 24;

    public Action? OnStart { get; set; }

    private Window? window;

    public bool IsOpen => window?.IsVisible == true;

    public WelcomeWindow()
    {
        Theme.Changed += () =>
        {
            if (IsOpen && window is not null)
            {
                window.Content = Wrap(BuildContent());
            }
        };
    }

    public void Show()
    {
        Preferences.SetFlag("welcomeShown", true);
        window ??= MakeWindow();
        window.Content = Wrap(BuildContent());
        if (!window.IsVisible)
        {
            window.Show();
            CenterOnCursorMonitor(window);
        }
        window.Activate();
    }

    private void Start()
    {
        window?.Hide();
        OnStart?.Invoke();
    }

    private void Close() => window?.Hide();

    public FrameworkElement BuildContent()
    {
        var card = new Grid { Width = 340, Height = 300 };
        card.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(20),
            Background = Theme.PopupBackground,
            BorderBrush = Theme.Primary(0.08),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.3 },
        });

        var icon = Ui.Picture(Ui.AppIcon(), 76);
        var title = Ui.Text("You're all set", 19, FontWeights.SemiBold);
        var subtitle = Ui.Text($"Press {Preferences.ToggleShortcut.Display} any time to start.", 13, brush: Theme.Secondary);
        var start = Ui.PrimaryButton("Start", null, true, Start);
        start.Width = 180;
        start.Height = 32;
        start.CornerRadius = new CornerRadius(16);
        if (start.Child is FrameworkElement label)
        {
            label.HorizontalAlignment = HorizontalAlignment.Center;
        }
        var tip = Ui.Text("TinyHand lives in the system tray. If you can't see the hand, click ^ next to the clock.", 11,
            brush: Theme.Secondary, wrap: true);
        tip.TextAlignment = TextAlignment.Center;
        tip.MaxWidth = 260;

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        foreach (var (element, gap) in new (FrameworkElement, double)[] { (icon, 0), (title, 16), (subtitle, 6), (start, 22), (tip, 12) })
        {
            element.HorizontalAlignment = HorizontalAlignment.Center;
            element.Margin = new Thickness(0, gap, 0, 0);
            stack.Children.Add(element);
        }
        card.Children.Add(stack);

        var close = new Clickable(Brushes.Transparent, Theme.Primary(0.08), Theme.Primary(0.12))
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 14, 14, 0),
            Child = Ui.Glyph("", 10, Theme.Tertiary),
            ToolTip = "Close",
            Click = Close,
        };
        card.Children.Add(close);
        return card;
    }

    private static Border Wrap(FrameworkElement card) => new() { Padding = new Thickness(ShadowMargin), Child = card };

    private Window MakeWindow()
    {
        var w = new Window
        {
            Title = "Welcome to TinyHand",
            Icon = Ui.WindowIcon(),
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = true,
        };
        w.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                Start();
                e.Handled = true;
            }
        };
        w.MouseLeftButtonDown += (_, _) =>
        {
            try
            {
                w.DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        };
        w.Closed += (_, _) =>
        {
            if (ReferenceEquals(window, w))
            {
                window = null;
            }
        };
        return w;
    }

    private static void CenterOnCursorMonitor(Window w)
    {
        var monitor = Monitor.AtCursor();
        var handle = new WindowInteropHelper(w).Handle;
        const uint flags = Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE;
        Native.SetWindowPos(handle, IntPtr.Zero, monitor.Work.Left + 40, monitor.Work.Top + 40, 0, 0, flags);
        w.UpdateLayout();
        Native.GetWindowRect(handle, out var r);
        int x = monitor.Work.Left + (monitor.Work.Width - r.Width) / 2;
        int y = monitor.Work.Top + (monitor.Work.Height - r.Height) / 2;
        Native.SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, flags);
    }
}
