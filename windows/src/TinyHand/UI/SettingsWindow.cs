using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using TinyHand.Core;
using TinyHand.Hands;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace TinyHand.UI;

internal enum SettingsSection
{
    General,
    Pointers,
    Appearance,
    Drawing,
    Sound,
    About,
}

internal static class SettingsSections
{
    public static string Title(this SettingsSection section) => section.ToString();

    public static Color Tint(this SettingsSection section) => section switch
    {
        SettingsSection.General => Color.FromRgb(140, 140, 140),
        SettingsSection.Pointers => Color.FromRgb(140, 107, 242),
        SettingsSection.Appearance => Color.FromRgb(242, 115, 153),
        SettingsSection.Drawing => Color.FromRgb(245, 148, 51),
        SettingsSection.Sound => Color.FromRgb(51, 140, 250),
        _ => Color.FromRgb(115, 115, 115),
    };

    public static UIElement Icon(this SettingsSection section) => section switch
    {
        SettingsSection.Pointers => Ui.Tinted(Path.Combine(Paths.Assets, "menubar_on@3x.png"), 13, Brushes.White),
        SettingsSection.General => Ui.Glyph("", 12, Brushes.White),
        SettingsSection.Appearance => Ui.Glyph("", 12, Brushes.White),
        SettingsSection.Drawing => Ui.Glyph("", 12, Brushes.White),
        SettingsSection.Sound => Ui.Glyph("", 12, Brushes.White),
        _ => Ui.Glyph("", 12, Brushes.White),
    };
}

/// <summary>The Settings window: a sidebar of sections and a page for each. A port of SettingsView in the macOS app.</summary>
internal sealed class SettingsWindow
{
    public Func<Shortcut, bool>? ApplyToggleShortcut { get; set; }
    public Func<Shortcut, bool>? ApplyClearShortcut { get; set; }
    public Action<bool>? OnRecording { get; set; }
    public Updater? Updater { get; set; }

    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private Window? window;
    private ScrollViewer? scroller;
    private SettingsSection section = SettingsSection.General;
    private (SettingsSection, bool)? builtPage;
    private bool subscribed;
    private CustomHandStore? watchedHands;
    private bool rebuildQueued;
    private bool sliding;
    private bool rowDragging;

    private string? pinMessage;
    private string? handMessage;
    private string? shortcutError;
    private bool makingHand;
    private string handIdea = "";
    private string handName = "";
    private (Pixels Image, uint Ink)? draft;
    private string? draftError;
    private bool promptCopied;
    private DispatcherTimer? copiedReset;
    private TapSound? preview;

    public bool IsOpen => window is { IsVisible: true };

    public void Show(SettingsSection? section = null)
    {
        if (section is SettingsSection s)
        {
            this.section = s;
        }
        shortcutError = null;
        pinMessage = null;
        window ??= BuildWindow();
        Subscribe();
        Rebuild();
        HandAssets.PreloadThumbnails(Pointers.AllCases.Select(Pointers.AssetName).OfType<string>(), QueueRebuild);
        if (!window.IsVisible)
        {
            window.Show();
        }
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }
        window.Activate();
    }

    public FrameworkElement BuildForSnapshot(SettingsSection section, bool makingHand = false, string? draftPath = null)
    {
        this.section = section;
        this.makingHand = makingHand;
        foreach (var name in Pointers.AllCases.Select(Pointers.AssetName).OfType<string>())
        {
            HandAssets.Thumbnail(name);
        }
        if (draftPath is not null)
        {
            LoadDraft(draftPath);
        }
        var content = BuildContent();
        content.Width = 780;
        content.Height = 560;
        return content;
    }

    private Window BuildWindow()
    {
        var w = new Window
        {
            Title = "TinyHand Settings",
            Width = 780,
            Height = 560,
            MinWidth = 760,
            MinHeight = 540,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Icon = Ui.WindowIcon(),
            Background = Theme.WindowBackground,
            FontFamily = Theme.Font,
            UseLayoutRounding = true,
        };
        w.SourceInitialized += (_, _) => ApplyTitleBar(w);
        w.Closed += (_, _) =>
        {
            window = null;
            scroller = null;
            builtPage = null;
            Unsubscribe();
            OnRecording?.Invoke(false);
        };
        return w;
    }

    private static void ApplyTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        int dark = Theme.Dark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    private void Subscribe()
    {
        if (!subscribed)
        {
            subscribed = true;
            Preferences.Changed += QueueRebuild;
            Theme.Changed += OnThemeChanged;
        }
        if (watchedHands != Pointers.Custom)
        {
            if (watchedHands is not null)
            {
                watchedHands.Changed -= QueueRebuild;
            }
            watchedHands = Pointers.Custom;
            if (watchedHands is not null)
            {
                watchedHands.Changed += QueueRebuild;
            }
        }
    }

    private void Unsubscribe()
    {
        if (subscribed)
        {
            subscribed = false;
            Preferences.Changed -= QueueRebuild;
            Theme.Changed -= OnThemeChanged;
        }
        if (watchedHands is not null)
        {
            watchedHands.Changed -= QueueRebuild;
            watchedHands = null;
        }
    }

    private void OnThemeChanged()
    {
        if (window is null)
        {
            return;
        }
        window.Background = Theme.WindowBackground;
        ApplyTitleBar(window);
        QueueRebuild();
    }

    private void QueueRebuild()
    {
        if (rebuildQueued || window is null)
        {
            return;
        }
        rebuildQueued = true;
        dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            rebuildQueued = false;
            if (!sliding && !rowDragging)
            {
                Rebuild();
            }
        });
    }

    private void Rebuild()
    {
        if (window is null)
        {
            return;
        }
        var page = (section, makingHand && section == SettingsSection.Pointers);
        double offset = builtPage == page ? scroller?.VerticalOffset ?? 0 : 0;
        window.Background = Theme.WindowBackground;
        window.Content = BuildContent();
        builtPage = page;
        if (offset > 0)
        {
            var current = scroller;
            dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => current?.ScrollToVerticalOffset(offset));
        }
    }

    private FrameworkElement BuildContent()
    {
        var root = new Grid { Background = Theme.WindowBackground };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var sidebar = BuildSidebar();
        root.Children.Add(sidebar);
        var page = BuildPage();
        Grid.SetColumn(page, 1);
        root.Children.Add(page);
        return root;
    }

    private FrameworkElement BuildSidebar()
    {
        var list = new StackPanel { Margin = new Thickness(10, 14, 10, 0) };
        foreach (var s in Enum.GetValues<SettingsSection>())
        {
            bool selected = s == section;
            var row = new Clickable(selected ? Theme.Primary(0.1) : Brushes.Transparent, selected ? Theme.Primary(0.1) : Theme.Primary(0.05))
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 2),
                Child = Ui.HStack(10, Ui.IconTile(s.Icon(), s.Tint()), Ui.Text(s.Title(), 13, selected ? FontWeights.SemiBold : FontWeights.Normal)),
            };
            var target = s;
            row.Click = () =>
            {
                section = target;
                Rebuild();
            };
            list.Children.Add(row);
        }
        var footer = Ui.HStack(8, Ui.Picture(Ui.AppIcon(64), 22), Ui.Text("TinyHand", 12, FontWeights.SemiBold));
        footer.Margin = new Thickness(20, 0, 10, 16);
        var dock = new DockPanel { Background = Theme.SidebarBackground, LastChildFill = true };
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        dock.Children.Add(list);
        return dock;
    }

    private FrameworkElement BuildPage()
    {
        bool making = section == SettingsSection.Pointers && makingHand;
        var stack = new StackPanel { Margin = new Thickness(28, 20, 28, 28) };
        if (making)
        {
            var back = new Clickable
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = Ui.HStack(4, Ui.Glyph("", 10, Theme.Secondary), Ui.Text("Pointers", 12, FontWeights.Medium, Theme.Secondary)),
                Click = () =>
                {
                    makingHand = false;
                    Rebuild();
                },
            };
            stack.Children.Add(back);
        }
        var title = Ui.Text(making ? "Make your own hand" : section.Title(), 22, FontWeights.Bold);
        title.FontFamily = Theme.DisplayFont;
        title.Margin = new Thickness(0, making ? 8 : 6, 0, 0);
        stack.Children.Add(title);
        Ui.Add(stack, PageContent(), 22);
        scroller = Ui.Scroller(stack);
        return scroller;
    }

    private FrameworkElement PageContent() => section switch
    {
        SettingsSection.General => GeneralPage(),
        SettingsSection.Pointers => makingHand ? MakeHandPage() : PointersPage(),
        SettingsSection.Appearance => AppearancePage(),
        SettingsSection.Drawing => DrawingPage(),
        SettingsSection.Sound => SoundPage(),
        _ => AboutPage(),
    };

    private FrameworkElement GeneralPage()
    {
        var interact = Preferences.InteractKey;
        var keys = Enum.GetValues<InteractKey>().Select(k => (k, k.Title())).ToList();
        bool keepsHand = Preferences.InteractKeepsHand;
        var startup = new List<FrameworkElement?>
        {
            Ui.Row("Open TinyHand when you sign in", null, Ui.Toggle(Preferences.LaunchAtLogin, v => Preferences.LaunchAtLogin = v)),
        };
        if (Updater is { IsAvailable: true } updater)
        {
            startup.Add(Ui.Row("Check for updates automatically", null, Ui.Toggle(updater.AutomaticallyChecks, v => updater.AutomaticallyChecks = v)));
        }
        return Ui.VStack(22,
            Ui.Card("Shortcuts",
                Ui.Row("Turn the pointer on or off", null,
                    new ShortcutRecorder(Preferences.ToggleShortcut, s => ApplyShortcut(s, ApplyToggleShortcut), r => OnRecording?.Invoke(r))),
                Ui.Row("Clear the drawing", null,
                    new ShortcutRecorder(Preferences.ClearShortcut, s => ApplyShortcut(s, ApplyClearShortcut), r => OnRecording?.Invoke(r)))),
            shortcutError is null ? null : Ui.Text(shortcutError, 12, brush: Theme.Red, wrap: true),
            Ui.Card("Clicking for real",
                Ui.Row("Hold to use the app", "Clicks, drags and scrolls reach the app.",
                    Ui.Picker(keys, interact, k => Preferences.InteractKey = k)),
                interact == InteractKey.Off
                    ? null
                    : Ui.Row("While holding", keepsHand ? "The hand stays, in menus too." : "The normal cursor comes back, in menus too.",
                        Ui.Picker([(false, "Show the cursor"), (true, "Keep the hand")], keepsHand, v => Preferences.InteractKeepsHand = v))),
            Ui.Card("Startup", startup.ToArray()));
    }

    private bool ApplyShortcut(Shortcut s, Func<Shortcut, bool>? apply)
    {
        bool ok = apply?.Invoke(s) ?? false;
        shortcutError = ok ? null : $"{s.Display} is already used by another app. Pick a different shortcut.";
        QueueRebuild();
        return ok;
    }

    private FrameworkElement PointersPage()
    {
        bool full = Preferences.PinnedStyles.Count >= Pointers.MaxPinned;
        var header = new Grid { Margin = new Thickness(4, 0, 4, 0) };
        header.Children.Add(Ui.Text("In your toolbar", 12, FontWeights.SemiBold, Theme.Secondary));
        header.Children.Add(Ui.TextButton("Reset", ResetToolbar).Also(b => b.HorizontalAlignment = HorizontalAlignment.Right));
        var help = Ui.Text(full
                ? "The toolbar is full. Remove a pointer to add another. Drag to reorder."
                : "Press + to add a pointer. Drag to reorder, or out to remove.",
            11, brush: Theme.Secondary, wrap: true);
        help.Margin = new Thickness(4, 0, 4, 0);
        var message = pinMessage is null ? null : Ui.Text(pinMessage, 12, brush: Theme.Red, wrap: true).Also(t => t.Margin = new Thickness(4, 0, 4, 0));
        return Ui.VStack(30, Ui.VStack(8, header, ToolbarRow(), help, message), YourHands());
    }

    private FrameworkElement ToolbarRow()
    {
        var basePinned = Preferences.PinnedStyles;
        bool full = basePinned.Count >= Pointers.MaxPinned;
        var grid = new Grid();
        for (int i = 0; i < Pointers.MaxPinned; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        var slots = new Dictionary<PointerStyle, Grid>();
        var badges = new Dictionary<PointerStyle, FrameworkElement>();
        for (int i = 0; i < basePinned.Count; i++)
        {
            var (slot, badge) = Slot(basePinned[i]);
            Grid.SetColumn(slot, i);
            grid.Children.Add(slot);
            slots[basePinned[i]] = slot;
            badges[basePinned[i]] = badge;
        }
        int next = basePinned.Count;
        if (!full)
        {
            var add = AddSlot();
            Grid.SetColumn(add, next++);
            grid.Children.Add(add);
        }
        for (; next < Pointers.MaxPinned; next++)
        {
            var empty = new Border
            {
                Height = 58,
                Margin = new Thickness(3, 0, 3, 0),
                CornerRadius = new CornerRadius(10),
                Background = Theme.Primary(0.035),
            };
            Grid.SetColumn(empty, next);
            grid.Children.Add(empty);
        }
        var box = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = Theme.Primary(0.045),
            Padding = new Thickness(7, 10, 7, 10),
            Child = grid,
        };

        PointerStyle? candidate = null;
        var start = new Point();
        bool dragging = false;
        bool removing = false;
        bool keepOne = false;
        var order = basePinned;

        foreach (var (style, slot) in slots)
        {
            slot.MouseLeftButtonDown += (_, e) =>
            {
                candidate = style;
                start = e.GetPosition(box);
                box.CaptureMouse();
                e.Handled = true;
            };
        }
        box.MouseMove += (_, e) =>
        {
            if (candidate is not PointerStyle style || !box.IsMouseCaptured)
            {
                return;
            }
            var p = e.GetPosition(box);
            if (!dragging)
            {
                if ((p - start).Length < 3)
                {
                    return;
                }
                dragging = true;
                rowDragging = true;
                pinMessage = null;
                foreach (var s in slots.Values)
                {
                    if (s.Tag is FrameworkElement close)
                    {
                        close.Visibility = Visibility.Hidden;
                    }
                }
            }
            double step = Math.Max(1, (box.ActualWidth - 14) / Pointers.MaxPinned);
            bool inside = p.X >= -16 && p.X <= box.ActualWidth + 16 && p.Y >= -28 && p.Y <= box.ActualHeight + 28;
            var rest = basePinned.Where(s => s != style).ToList();
            keepOne = false;
            if (inside)
            {
                int index = Math.Clamp((int)((p.X - 7) / step), 0, rest.Count);
                rest.Insert(index, style);
                order = rest;
                removing = false;
            }
            else if (basePinned.Count > 1)
            {
                order = rest;
                removing = true;
            }
            else
            {
                order = basePinned;
                removing = false;
                keepOne = true;
            }
            for (int i = 0; i < order.Count; i++)
            {
                Grid.SetColumn(slots[order[i]], i);
            }
            var dragged = slots[style];
            if (removing)
            {
                Grid.SetColumn(dragged, order.Count);
            }
            double center = 7 + (Grid.GetColumn(dragged) + 0.5) * step;
            var lift = new TransformGroup();
            lift.Children.Add(new ScaleTransform(1.06, 1.06));
            lift.Children.Add(new TranslateTransform(p.X - center, inside ? 0 : p.Y - box.ActualHeight / 2));
            dragged.RenderTransformOrigin = new Point(0.5, 0.5);
            dragged.RenderTransform = lift;
            Panel.SetZIndex(dragged, 10);
            dragged.Opacity = removing ? 0.6 : 1;
            badges[style].Visibility = removing ? Visibility.Visible : Visibility.Collapsed;
        };
        box.MouseLeftButtonUp += (_, _) =>
        {
            if (box.IsMouseCaptured)
            {
                box.ReleaseMouseCapture();
            }
        };
        box.LostMouseCapture += (_, _) =>
        {
            if (candidate is null)
            {
                return;
            }
            bool wasDragging = dragging;
            candidate = null;
            dragging = false;
            rowDragging = false;
            if (!wasDragging)
            {
                return;
            }
            if (keepOne)
            {
                pinMessage = "Keep at least one pointer in the toolbar.";
                QueueRebuild();
            }
            else if (!order.SequenceEqual(basePinned))
            {
                Preferences.PinnedStyles = order.ToList();
            }
            else
            {
                QueueRebuild();
            }
        };
        return box;
    }

    private (Grid Slot, FrameworkElement Badge) Slot(PointerStyle style)
    {
        var grid = new Grid
        {
            Height = 58,
            Margin = new Thickness(3, 0, 3, 0),
            Background = Brushes.Transparent,
            ToolTip = Pointers.Title(style),
        };
        grid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = Theme.SlotBackground,
            Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Direction = 270, Opacity = 0.08 },
            Child = Ui.PointerIcon(style, 40),
        });
        var close = new Clickable(Theme.Solid(Colors.Black, 0.55), Theme.Solid(Colors.Black, 0.7))
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -5, -5, 0),
            Child = Ui.Glyph("", 7, Brushes.White),
            Visibility = Visibility.Hidden,
            ToolTip = "Remove from the toolbar",
            Click = () => Unpin(style),
        };
        grid.Children.Add(close);
        grid.Tag = close;
        var badge = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = Theme.Red,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -6, -6, 0),
            Child = new Border { Width = 8, Height = 2, Background = Brushes.White },
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        grid.Children.Add(badge);
        grid.MouseEnter += (_, _) =>
        {
            if (!rowDragging)
            {
                close.Visibility = Visibility.Visible;
            }
        };
        grid.MouseLeave += (_, _) => close.Visibility = Visibility.Hidden;
        return (grid, badge);
    }

    private FrameworkElement AddSlot()
    {
        var content = new Grid();
        content.Children.Add(new Rectangle
        {
            RadiusX = 10,
            RadiusY = 10,
            Stroke = Theme.Primary(0.16),
            StrokeThickness = 1,
            StrokeDashArray = [4, 3],
        });
        content.Children.Add(Ui.Glyph("", 15, Theme.Secondary));
        var add = new Clickable(Theme.Primary(0.05), Theme.Primary(0.09))
        {
            Height = 58,
            Margin = new Thickness(3, 0, 3, 0),
            CornerRadius = new CornerRadius(10),
            ToolTip = "Add a pointer",
            Child = content,
        };
        add.Click = () => OpenPicker(add);
        return add;
    }

    private void OpenPicker(FrameworkElement target)
    {
        Popup? popup = null;
        var pinned = Preferences.PinnedStyles.ToHashSet();
        var body = new StackPanel { Margin = new Thickness(12) };

        FrameworkElement Section(string? title, IEnumerable<PointerStyle> styles)
        {
            var cells = new WrapPanel();
            foreach (var style in styles)
            {
                bool added = pinned.Contains(style);
                var title2 = Pointers.Title(style);
                var cell = new Clickable(null, added ? null : Theme.Primary(0.08))
                {
                    Width = 62,
                    Height = 58,
                    Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(10),
                    Child = Ui.PointerIcon(style, 42),
                    Opacity = added ? 0.35 : 1,
                    ToolTip = added ? $"{title2} is already in the toolbar" : title2,
                };
                var picked = style;
                cell.Click = () =>
                {
                    if (added)
                    {
                        return;
                    }
                    Pin(picked);
                    popup!.IsOpen = false;
                };
                cells.Children.Add(cell);
            }
            if (title is null)
            {
                return cells;
            }
            return Ui.VStack(6, Ui.Text(title, 11, FontWeights.SemiBold, Theme.Secondary).Also(t => t.Margin = new Thickness(4, 0, 0, 0)), cells);
        }

        Ui.Add(body, Section(null, [.. Pointers.BuiltInHands, Pointers.Laser]), 14);
        if (Pointers.CustomHands.Count > 0)
        {
            Ui.Add(body, Section("Your hands", Pointers.CustomHands), 14);
        }
        var list = Ui.Scroller(body);
        list.Height = 340;
        var make = new Clickable(null, Theme.Primary(0.05))
        {
            Padding = new Thickness(14, 10, 14, 10),
            Child = Ui.HStack(6, Ui.Glyph("", 11, Theme.Secondary), Ui.Text("Make your own hand…", 12, brush: Theme.Secondary)),
        };
        make.Click = () =>
        {
            popup!.IsOpen = false;
            StartMakingHand();
        };
        var content = new StackPanel { Width = 400 };
        content.Children.Add(list);
        content.Children.Add(new Border { Height = 1, Background = Theme.Primary(0.08) });
        content.Children.Add(make);
        popup = Ui.Popup(target, Ui.PopupCard(content, 10));
        popup.IsOpen = true;
    }

    private FrameworkElement YourHands()
    {
        var header = new Grid { Margin = new Thickness(4, 0, 4, 0) };
        header.Children.Add(Ui.Text("Your hands", 12, FontWeights.SemiBold, Theme.Secondary));
        header.Children.Add(Ui.TextButton("Make your own…", StartMakingHand).Also(b => b.HorizontalAlignment = HorizontalAlignment.Right));
        FrameworkElement body;
        var custom = Pointers.CustomHands;
        if (custom.Count == 0)
        {
            body = Ui.Text("Turn any idea into a hand with ChatGPT or another image tool.", 11, brush: Theme.Secondary, wrap: true)
                .Also(t => t.Margin = new Thickness(4, 0, 4, 0));
        }
        else
        {
            var chips = new WrapPanel();
            foreach (var style in custom)
            {
                chips.Children.Add(Chip(style));
            }
            body = chips;
        }
        var message = handMessage is null
            ? null
            : Ui.Text(handMessage, 11, brush: Theme.Secondary, wrap: true).Also(t => t.Margin = new Thickness(4, 0, 4, 0));
        return Ui.VStack(8, header, body, message);
    }

    private FrameworkElement Chip(PointerStyle style)
    {
        var pinned = Preferences.PinnedStyles;
        bool isPinned = pinned.Contains(style);
        bool full = pinned.Count >= Pointers.MaxPinned;
        var title = Pointers.Title(style);
        var close = new Clickable(null, Theme.Primary(0.12))
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Child = Ui.Glyph("", 8, Theme.Secondary),
            Opacity = 0,
            ToolTip = $"Delete {title}",
            Click = () => RemoveHand(style),
        };
        var chip = new Clickable(Theme.Primary(0.05), isPinned ? Theme.Primary(0.05) : Theme.Primary(0.09))
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(6, 5, 6, 5),
            Margin = new Thickness(0, 0, 6, 6),
            Child = Ui.HStack(6, Ui.PointerIcon(style, 24), Ui.Text(title, 12), close),
            ToolTip = isPinned ? $"{title} is in your toolbar" : $"Click to add {title} to the toolbar",
        };
        chip.MouseEnter += (_, _) => close.Opacity = 1;
        chip.MouseLeave += (_, _) => close.Opacity = 0;
        chip.Click = () =>
        {
            if (isPinned)
            {
                return;
            }
            if (full)
            {
                OpenSwap(chip, style);
            }
            else
            {
                Pin(style);
            }
        };
        return chip;
    }

    private void OpenSwap(FrameworkElement target, PointerStyle style)
    {
        Popup? popup = null;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var old in Preferences.PinnedStyles)
        {
            var replaced = old;
            var cell = new Clickable(Theme.Primary(0.04), Theme.Primary(0.1))
            {
                Width = 44,
                Height = 44,
                CornerRadius = new CornerRadius(9),
                Child = Ui.PointerIcon(old, 32),
                ToolTip = $"Replace {Pointers.Title(old)}",
                Click = () =>
                {
                    popup!.IsOpen = false;
                    Swap(replaced, style);
                },
            };
            Ui.Add(row, cell, 4);
        }
        var content = Ui.VStack(8, Ui.Text($"The toolbar is full. Swap {Pointers.Title(style)} in for:", 12, brush: Theme.Secondary), row);
        content.Margin = new Thickness(12);
        popup = Ui.Popup(target, Ui.PopupCard(content, 10));
        popup.IsOpen = true;
    }

    private void Pin(PointerStyle style)
    {
        var pinned = Preferences.PinnedStyles;
        if (pinned.Contains(style))
        {
            Unpin(style);
            return;
        }
        if (pinned.Count >= Pointers.MaxPinned)
        {
            pinMessage = $"The toolbar is full. Remove one above to add {Pointers.Title(style)}.";
            QueueRebuild();
            return;
        }
        pinned.Add(style);
        pinMessage = null;
        Preferences.PinnedStyles = pinned;
    }

    private void Swap(PointerStyle old, PointerStyle replacement)
    {
        var pinned = Preferences.PinnedStyles;
        int i = pinned.IndexOf(old);
        if (i < 0 || pinned.Contains(replacement))
        {
            return;
        }
        pinned[i] = replacement;
        pinMessage = null;
        Preferences.PinnedStyles = pinned;
    }

    private void Unpin(PointerStyle style)
    {
        var pinned = Preferences.PinnedStyles;
        int i = pinned.IndexOf(style);
        if (i < 0)
        {
            return;
        }
        if (pinned.Count == 1)
        {
            pinMessage = "Keep at least one pointer in the toolbar.";
            QueueRebuild();
            return;
        }
        pinned.RemoveAt(i);
        pinMessage = null;
        Preferences.PinnedStyles = pinned;
    }

    private void ResetToolbar()
    {
        pinMessage = null;
        Preferences.PinnedStyles = Pointers.DefaultPinned.ToList();
    }

    private void RemoveHand(PointerStyle style)
    {
        var pinned = Preferences.PinnedStyles.Where(s => s != style).ToList();
        Preferences.PinnedStyles = pinned.Count == 0 ? Pointers.DefaultPinned.ToList() : pinned;
        if (Preferences.SavedStyle == style)
        {
            Preferences.SavedStyle = Pointers.Lavender;
        }
        Pointers.Custom?.Remove(style);
        if (Pointers.AssetName(style) is string name)
        {
            HandAssets.Forget(name);
        }
        handMessage = null;
        Preferences.NotifyChanged();
    }

    private void StartMakingHand()
    {
        draft = null;
        draftError = null;
        promptCopied = false;
        handMessage = null;
        makingHand = true;
        section = SettingsSection.Pointers;
        Rebuild();
    }

    private FrameworkElement MakeHandPage()
    {
        var idea = Ui.TextField("e.g. knitted wool in mustard yellow", handIdea, v => handIdea = v);
        idea.MaxWidth = 420;
        idea.HorizontalAlignment = HorizontalAlignment.Left;
        idea.Width = 420;
        var buttons = Ui.HStack(8,
            Ui.PrimaryButton("Open in ChatGPT", "", true, OpenChatGpt),
            Ui.PrimaryButton(promptCopied ? "Copied" : "Copy prompt", promptCopied ? "" : "", false, CopyPrompt));
        return Ui.VStack(26,
            Step(1, "Describe it", "What should your hand look like?", idea),
            Step(2, "Make the image",
                "Open it in ChatGPT, or copy the prompt for another image tool. Download the result as a PNG with a transparent background.",
                buttons),
            Step(3, "Add it", null, draft is null ? DropZone() : DraftPreview()));
    }

    private static FrameworkElement Step(int number, string title, string? detail, FrameworkElement content)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var badge = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = Theme.Primary(0.75),
            VerticalAlignment = VerticalAlignment.Top,
            Child = Ui.Text(number.ToString(), 11, FontWeights.Bold, Theme.WindowBackground).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center),
        };
        grid.Children.Add(badge);
        var right = Ui.VStack(10,
            Ui.VStack(2,
                Ui.Text(title, 13, FontWeights.SemiBold),
                detail is null ? null : Ui.Text(detail, 12, brush: Theme.Secondary, wrap: true)),
            content);
        right.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    private void OpenChatGpt() =>
        Shell.Open("https://chatgpt.com/?q=" + Uri.EscapeDataString(HandMaker.PromptFor(handIdea)));

    private void CopyPrompt()
    {
        try
        {
            Clipboard.SetText(HandMaker.PromptFor(handIdea));
        }
        catch (Exception e)
        {
            Log.Write($"copy prompt failed: {e.Message}");
            return;
        }
        promptCopied = true;
        copiedReset?.Stop();
        copiedReset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        copiedReset.Tick += (_, _) =>
        {
            copiedReset?.Stop();
            promptCopied = false;
            QueueRebuild();
        };
        copiedReset.Start();
        QueueRebuild();
    }

    private FrameworkElement DropZone()
    {
        bool error = draftError is not null;
        var message = Ui.Text(draftError ?? "Drop the PNG here, or click to choose it", 12,
            brush: error ? Theme.Text : Theme.Secondary, wrap: true);
        message.TextAlignment = TextAlignment.Center;
        message.HorizontalAlignment = HorizontalAlignment.Center;
        var inner = Ui.VStack(8,
            Ui.Glyph(error ? "" : "", 20, error ? Theme.Orange : Theme.Secondary),
            message,
            error ? Ui.Text("Try again with another image", 11, brush: Theme.Secondary).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center) : null);
        inner.Margin = new Thickness(24, 0, 24, 0);
        inner.VerticalAlignment = VerticalAlignment.Center;
        inner.HorizontalAlignment = HorizontalAlignment.Center;
        var dash = new Rectangle
        {
            RadiusX = 12,
            RadiusY = 12,
            Stroke = Theme.Primary(0.14),
            StrokeThickness = 1.2,
            StrokeDashArray = [5, 4],
        };
        var content = new Grid();
        content.Children.Add(dash);
        content.Children.Add(inner);
        var zone = new Clickable(Theme.Primary(0.03), Theme.Primary(0.05))
        {
            Height = 130,
            CornerRadius = new CornerRadius(12),
            AllowDrop = true,
            Child = content,
            Click = ChooseHandFile,
        };
        void Highlight(bool on)
        {
            zone.SetBrushes(on ? Theme.Primary(0.07) : Theme.Primary(0.03), Theme.Primary(0.05));
            dash.Stroke = on ? Theme.AccentBrush() : Theme.Primary(0.14);
        }
        zone.DragEnter += (_, e) =>
        {
            bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
            Highlight(files);
            e.Handled = true;
        };
        zone.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        zone.DragLeave += (_, _) => Highlight(false);
        zone.Drop += (_, e) =>
        {
            Highlight(false);
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
            {
                LoadDraft(paths[0]);
            }
            e.Handled = true;
        };
        return zone;
    }

    private void ChooseHandFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick the PNG of your hand",
            Filter = "Images|*.png;*.webp;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff",
            Multiselect = false,
        };
        bool? picked = window is null ? dialog.ShowDialog() : dialog.ShowDialog(window);
        if (picked == true)
        {
            LoadDraft(dialog.FileName);
        }
    }

    private void LoadDraft(string path)
    {
        try
        {
            draft = HandMaker.Make(ImageIO.Decode(path));
            draftError = null;
            handName = Pointers.Custom?.SuggestedName(path) ?? "My hand";
        }
        catch (HandProblem problem)
        {
            draft = null;
            draftError = problem.Message;
        }
        catch (Exception)
        {
            draft = null;
            draftError = "TinyHand couldn't read that file.";
        }
        QueueRebuild();
    }

    private FrameworkElement DraftPreview()
    {
        var (image, _) = draft!.Value;
        var cropped = image.Crop(112, 90, 800, 1000);
        var picture = Ui.Picture(ImageIO.ToBitmap(cropped), 144);
        picture.Height = 180;
        var card = new Border
        {
            Width = 170,
            CornerRadius = new CornerRadius(12),
            Background = Theme.Hex(0xE8CFB5),
            Padding = new Thickness(13, 10, 13, 10),
            Child = picture,
        };
        var name = Ui.TextField("My hand", handName, v => handName = v, 16);
        name.Width = 220;
        name.HorizontalAlignment = HorizontalAlignment.Left;
        var advice = Ui.Text("It should show the back of the hand, like the ones in TinyHand. If you see the palm, try another image.",
            11, brush: Theme.Secondary, wrap: true);
        advice.MaxWidth = 300;
        advice.HorizontalAlignment = HorizontalAlignment.Left;
        var right = Ui.VStack(12,
            Ui.VStack(4, Ui.Text("Name", 11, FontWeights.Medium, Theme.Secondary), name),
            advice,
            Ui.HStack(10, Ui.PrimaryButton("Add hand", null, true, SaveDraft), Ui.TextButton("Use another image", ChooseHandFile)));
        right.VerticalAlignment = VerticalAlignment.Center;
        return Ui.HStack(22, card, right);
    }

    private void SaveDraft()
    {
        if (draft is not (Pixels image, uint ink) || Pointers.Custom is not CustomHandStore store)
        {
            return;
        }
        try
        {
            var style = store.Add(ImageIO.EncodePng(image), ink, handName);
            if (Pointers.AssetName(style) is string asset)
            {
                HandAssets.Thumbnail(asset);
            }
            var title = Pointers.Title(style);
            var pinned = Preferences.PinnedStyles;
            if (pinned.Count < Pointers.MaxPinned)
            {
                int at = Math.Max(0, pinned.Count - (pinned.Count > 0 && pinned[^1] == Pointers.Laser ? 1 : 0));
                pinned.Insert(at, style);
                Preferences.PinnedStyles = pinned;
                handMessage = $"{title} is in your toolbar.";
            }
            else
            {
                handMessage = $"{title} is ready. Click it to add it to the toolbar.";
            }
            Preferences.SavedStyle = style;
            draft = null;
            handIdea = "";
            makingHand = false;
            Preferences.NotifyChanged();
        }
        catch (Exception e)
        {
            Log.Write($"saving hand failed: {e.Message}");
            draftError = "TinyHand couldn't save that hand.";
        }
        QueueRebuild();
    }

    private FrameworkElement AppearancePage()
    {
        var hand = Path.Combine(Paths.Assets, "menubar_on@3x.png");
        var size = Ui.HStack(10,
            Ui.Tinted(hand, 11, Theme.Secondary),
            Ui.Slider(Preferences.PointerSizeMin, Preferences.PointerSizeMax, Preferences.PointerSize, 180,
                v => Preferences.PointerSize = v,
                d =>
                {
                    sliding = d;
                    if (!d)
                    {
                        QueueRebuild();
                    }
                }),
            Ui.Tinted(hand, 17, Theme.Secondary));
        return Ui.Card("Pointer",
            Ui.Row("Size", null, size),
            Ui.Row("Shadow under the pointer", null, Ui.Toggle(Preferences.ShowShadow, v => Preferences.ShowShadow = v)));
    }

    private FrameworkElement DrawingPage()
    {
        var choice = Preferences.InkChoice;
        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in Enum.GetValues<InkChoice>())
        {
            bool selected = c == choice;
            var picked = c;
            var swatch = new Clickable
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(2),
                BorderBrush = selected ? Theme.AccentBrush() : Brushes.Transparent,
                Child = Ui.InkDot(c.Color(), 26),
                ToolTip = c.Title(),
                Click = () => Preferences.InkChoice = picked,
            };
            Ui.Add(swatches, swatch, 4);
        }
        var detail = choice == InkChoice.Match ? "Matches each pointer." : $"All pointers draw in {choice.Title().ToLowerInvariant()}.";
        var fades = Choices.FadeChoices.Select(f => (f.Seconds, f.Title)).ToList();
        return Ui.Card("Ink",
            Ui.Row("Ink colour", detail, swatches),
            Ui.Row("Lines fade", $"Clear them any time with {Preferences.ClearShortcut.Display}.",
                Ui.Picker(fades, Preferences.FadeSeconds, v => Preferences.FadeSeconds = v)),
            Ui.Row("Ink button in the toolbar", "Change the colour while you present.",
                Ui.Toggle(Preferences.ShowInkButton, v => Preferences.ShowInkButton = v)));
    }

    private FrameworkElement SoundPage()
    {
        bool on = Preferences.HandSound;
        var current = Preferences.TapSound;
        WarmPreview();
        var tiles = new WrapPanel();
        tiles.Children.Add(SoundTile("Off", b => Ui.Glyph("", 18, b), !on, () => Preferences.HandSound = false));
        foreach (var c in Enum.GetValues<TapSoundChoice>())
        {
            var picked = c;
            tiles.Children.Add(SoundTile(c.Title(), Waveform, on && current == c, () =>
            {
                Preferences.TapSound = picked;
                Preferences.HandSound = true;
                PlayPreview(picked);
            }));
        }
        var header = Ui.Text("Tap sound", 12, FontWeights.SemiBold, Theme.Secondary);
        header.Margin = new Thickness(4, 0, 4, 0);
        var help = Ui.Text("Click a sound to hear it. It plays each time the hand taps.", 11, brush: Theme.Secondary, wrap: true);
        help.Margin = new Thickness(4, 0, 4, 0);
        var laser = Ui.Card(null,
            Ui.Row("Laser dot taps too", "Off by default, for quiet pointing.",
                Ui.Toggle(Preferences.LaserSound, v => Preferences.LaserSound = v, enabled: on)));
        laser.Opacity = on ? 1 : 0.5;
        return Ui.VStack(22, Ui.VStack(8, header, tiles, help), laser);
    }

    private static FrameworkElement Waveform(Brush brush)
    {
        var bars = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = 22 };
        foreach (var h in new double[] { 8, 16, 22, 13, 6 })
        {
            bars.Children.Add(new Border
            {
                Width = 3,
                Height = h,
                CornerRadius = new CornerRadius(1.5),
                Background = brush,
                Margin = new Thickness(1.5, 0, 1.5, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        return bars;
    }

    private static FrameworkElement SoundTile(string title, Func<Brush, FrameworkElement> icon, bool selected, Action action)
    {
        var iconBrush = selected ? Theme.AccentBrush() : Theme.Secondary;
        var symbol = icon(iconBrush);
        symbol.Height = 22;
        var label = Ui.Text(title, 12, selected ? FontWeights.SemiBold : FontWeights.Normal, selected ? Theme.Text : Theme.Secondary);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var content = Ui.VStack(8, symbol, label);
        content.VerticalAlignment = VerticalAlignment.Center;
        content.HorizontalAlignment = HorizontalAlignment.Center;
        return new Clickable(selected ? Theme.AccentBrush(0.12) : Theme.Primary(0.035), selected ? Theme.AccentBrush(0.12) : Theme.Primary(0.07))
        {
            Width = 118,
            Height = 76,
            Margin = new Thickness(0, 0, 8, 8),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1.5),
            BorderBrush = selected ? Theme.AccentBrush(0.7) : Brushes.Transparent,
            Child = content,
            Click = action,
        };
    }

    private void WarmPreview()
    {
        if (window is null)
        {
            return;
        }
        preview ??= new TapSound(Preferences.TapSound);
        preview.Warm();
    }

    private void PlayPreview(TapSoundChoice choice)
    {
        preview ??= new TapSound(choice);
        preview.Enabled = true;
        preview.Load(choice);
        preview.Play();
    }

    private FrameworkElement AboutPage()
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Ui.Picture(Ui.AppIcon(144), 72));
        var info = Ui.VStack(4,
            Ui.Text("TinyHand", 18, FontWeights.Bold),
            Ui.Text($"Version {Paths.Version}", 12, brush: Theme.Secondary),
            Ui.Text("A tiny hand for your PC. Free and open source.", 12, brush: Theme.Secondary));
        info.Margin = new Thickness(16, 0, 0, 0);
        info.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(info, 1);
        header.Children.Add(info);
        if (Updater is { IsAvailable: true } updater)
        {
            var check = Ui.PillButton("Check for Updates", () => updater.CheckForUpdates(true));
            check.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(check, 2);
            header.Children.Add(check);
        }
        var links = Ui.Card(null,
            LinkRow("Website", "tinyhand.app", "https://tinyhand.app"),
            LinkRow("Source code", "github.com/nilbuild/tinyhand", "https://github.com/nilbuild/tinyhand"),
            LinkRow("Windows version", "github.com/sajeeldigitali/tinyhand", "https://github.com/sajeeldigitali/tinyhand/tree/windows"),
            LinkRow("Made by", "@nilbuild on X", "https://x.com/nilbuild"),
            LinkRow("License", "MIT", "https://github.com/nilbuild/tinyhand/blob/main/LICENSE"));
        var footer = new Grid();
        footer.Children.Add(Ui.TextButton("Show log file", Shell.ShowLogFile).Also(b => b.HorizontalAlignment = HorizontalAlignment.Left));
        footer.Children.Add(Ui.TextButton("Reset all settings…", ResetAll).Also(b => b.HorizontalAlignment = HorizontalAlignment.Right));
        return Ui.VStack(22, header, links, footer);
    }

    private static FrameworkElement LinkRow(string title, string detail, string url)
    {
        var grid = new Grid();
        grid.Children.Add(Ui.Text(title, 13));
        var right = Ui.HStack(6, Ui.Text(detail, 12, brush: Theme.Secondary), Ui.Glyph("", 10, Theme.Secondary));
        right.HorizontalAlignment = HorizontalAlignment.Right;
        grid.Children.Add(right);
        return new Clickable(Brushes.Transparent, Theme.Primary(0.04))
        {
            MinHeight = 44,
            Padding = new Thickness(14, 0, 14, 0),
            Child = grid,
            Click = () => Shell.Open(url),
        };
    }

    private void ResetAll()
    {
        const string caption = "Reset all settings?";
        const string text = "Shortcuts, toolbar pointers, size, shadow, drawing and sound go back to how they were at first. Open at sign-in stays as it is.";
        var answer = window is null
            ? MessageBox.Show(text, caption, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(window, text, caption, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }
        Preferences.ResetAll();
        ApplyToggleShortcut?.Invoke(Shortcut.ToggleDefault);
        ApplyClearShortcut?.Invoke(Shortcut.ClearDefault);
        shortcutError = null;
        pinMessage = null;
        QueueRebuild();
    }
}
