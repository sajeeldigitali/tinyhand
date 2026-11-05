using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using TinyHand.Core;

namespace TinyHand.UI;

/// <summary>A border that acts as a button: it lights up under the mouse and runs <see cref="Click"/> on release.</summary>
internal class Clickable : Border
{
    private Brush normal = Brushes.Transparent;
    private Brush? hover;
    private Brush? pressed;
    private bool down;

    public Action? Click { get; set; }

    public Clickable(Brush? normal = null, Brush? hover = null, Brush? pressed = null)
    {
        this.normal = normal ?? Brushes.Transparent;
        this.hover = hover;
        this.pressed = pressed;
        Background = this.normal;
        Focusable = false;
        SnapsToDevicePixels = true;
        MouseEnter += (_, _) => Update();
        MouseLeave += (_, _) => Update();
        MouseLeftButtonDown += (_, e) =>
        {
            down = true;
            CaptureMouse();
            Update();
            e.Handled = true;
        };
        MouseLeftButtonUp += (_, e) =>
        {
            bool fire = down && IsMouseOver;
            down = false;
            ReleaseMouseCapture();
            Update();
            e.Handled = true;
            if (fire && IsEnabled)
            {
                Click?.Invoke();
            }
        };
        LostMouseCapture += (_, _) =>
        {
            down = false;
            Update();
        };
    }

    public void SetBrushes(Brush? normal, Brush? hover = null, Brush? pressed = null)
    {
        this.normal = normal ?? Brushes.Transparent;
        this.hover = hover;
        this.pressed = pressed;
        Update();
    }

    private void Update()
    {
        if (down && IsMouseOver)
        {
            Background = pressed ?? hover ?? normal;
        }
        else if (IsMouseOver && IsEnabled)
        {
            Background = hover ?? normal;
        }
        else
        {
            Background = normal;
        }
    }
}

internal static class Ui
{
    public static TextBlock Text(string text, double size = 13, FontWeight? weight = null, Brush? brush = null, bool wrap = false)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = Theme.Font,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            Foreground = brush ?? Theme.Text,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static TextBlock Glyph(string glyph, double size, Brush brush)
    {
        return new TextBlock
        {
            Text = glyph,
            FontFamily = Theme.Icons,
            FontSize = size,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
    }

    public static StackPanel VStack(double spacing, params UIElement?[] children)
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        foreach (var child in children)
        {
            Add(stack, child, spacing);
        }
        return stack;
    }

    public static StackPanel HStack(double spacing, params UIElement?[] children)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children)
        {
            Add(stack, child, spacing);
        }
        return stack;
    }

    /// <summary>Adds a child with a gap before it, so stacks space like SwiftUI's VStack and HStack.</summary>
    public static void Add(StackPanel stack, UIElement? child, double spacing)
    {
        if (child is null)
        {
            return;
        }
        if (stack.Children.Count > 0 && child is FrameworkElement fe)
        {
            var m = fe.Margin;
            fe.Margin = stack.Orientation == Orientation.Vertical
                ? new Thickness(m.Left, m.Top + spacing, m.Right, m.Bottom)
                : new Thickness(m.Left + spacing, m.Top, m.Right, m.Bottom);
        }
        stack.Children.Add(child);
    }

    public static Image Picture(ImageSource? source, double size)
    {
        var image = new Image { Source = source, Width = size, Height = size, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    /// <summary>A PNG drawn in one colour through its alpha, like an SF Symbol or template image.</summary>
    public static FrameworkElement Tinted(string pngPath, double size, Brush brush)
    {
        var shape = new Rectangle { Width = size, Height = size, Fill = brush };
        if (File.Exists(pngPath))
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(pngPath);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            shape.OpacityMask = new ImageBrush(bitmap) { Stretch = Stretch.Uniform };
        }
        return shape;
    }

    public static BitmapSource? AppIcon(int size = 256)
    {
        var path = Path.Combine(Paths.Assets, "AppIcon.png");
        if (!File.Exists(path))
        {
            return null;
        }
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(path);
        bitmap.DecodePixelWidth = size;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public static ImageSource? WindowIcon()
    {
        var path = Path.Combine(Paths.Assets, "TinyHand.ico");
        try
        {
            return File.Exists(path) ? BitmapFrame.Create(new Uri(path)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static FrameworkElement Card(string? title, params FrameworkElement?[] rows)
    {
        var body = new StackPanel();
        var list = rows.OfType<FrameworkElement>().ToList();
        for (int i = 0; i < list.Count; i++)
        {
            body.Children.Add(list[i]);
            if (i < list.Count - 1)
            {
                body.Children.Add(new Border { Height = 1, Background = Theme.Primary(0.07), Margin = new Thickness(14, 0, 0, 0) });
            }
        }
        var box = new Border { CornerRadius = new CornerRadius(12), Background = Theme.Primary(0.045), Child = body };
        if (title is null)
        {
            return box;
        }
        return VStack(8, Text(title, 12, FontWeights.SemiBold, Theme.Secondary).Also(t => t.Margin = new Thickness(4, 0, 0, 0)), box);
    }

    public static FrameworkElement Row(string title, string? detail, UIElement trailing)
    {
        var grid = new Grid { MinHeight = 44, Margin = new Thickness(14, 10, 14, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = VStack(2, Text(title, 13), detail is null ? null : Text(detail, 11, brush: Theme.Secondary, wrap: true));
        labels.VerticalAlignment = VerticalAlignment.Center;
        labels.Margin = new Thickness(0, 0, 12, 0);
        grid.Children.Add(labels);
        if (trailing is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
        }
        Grid.SetColumn(trailing, 1);
        grid.Children.Add(trailing);
        return new Border { Child = grid, MinHeight = 44 };
    }

    public static Clickable TextButton(string title, Action action, double size = 11)
    {
        var label = Text(title, size, FontWeights.Medium, Theme.Secondary);
        var button = new Clickable { Child = label, Click = action, Padding = new Thickness(2, 1, 2, 1) };
        button.MouseEnter += (_, _) => label.Foreground = Theme.Text;
        button.MouseLeave += (_, _) => label.Foreground = Theme.Secondary;
        return button;
    }

    public static Clickable PillButton(string title, Action action)
    {
        return new Clickable(Theme.Primary(0.09), Theme.Primary(0.14), Theme.Primary(0.18))
        {
            Child = Text(title, 12, FontWeights.Medium),
            Padding = new Thickness(12, 6, 12, 6),
            CornerRadius = new CornerRadius(14),
            Click = action,
        };
    }

    public static Clickable PrimaryButton(string title, string? glyph, bool primary, Action action)
    {
        var fg = primary ? Brushes.White : Theme.Text;
        var content = HStack(6,
            glyph is null ? null : Glyph(glyph, 11, fg).Also(g => g.Width = 14),
            Text(title, 12, FontWeights.SemiBold, fg));
        content.VerticalAlignment = VerticalAlignment.Center;
        return new Clickable(
            primary ? Theme.AccentBrush() : Theme.Primary(0.09),
            primary ? Theme.AccentBrush(0.88) : Theme.Primary(0.14),
            primary ? Theme.AccentBrush(0.75) : Theme.Primary(0.18))
        {
            Child = content,
            Height = 30,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(15),
            Click = action,
        };
    }

    /// <summary>A Windows 11 style switch.</summary>
    public static FrameworkElement Toggle(bool on, Action<bool> changed, bool enabled = true)
    {
        var knob = new Ellipse { Width = 12, Height = 12, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        var move = new TranslateTransform(on ? 23 : 4, 0);
        knob.RenderTransform = move;
        var track = new Clickable
        {
            Width = 40,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Child = knob,
            IsEnabled = enabled,
            Opacity = enabled ? 1 : 0.5,
        };
        void Paint()
        {
            track.SetBrushes(on ? Theme.AccentBrush() : Brushes.Transparent, on ? Theme.AccentBrush(0.9) : Theme.Primary(0.05));
            track.BorderBrush = on ? Theme.AccentBrush() : Theme.Primary(0.55);
            knob.Fill = on ? Brushes.White : Theme.Primary(0.7);
        }
        Paint();
        track.Click = () =>
        {
            on = !on;
            Paint();
            move.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(on ? 23 : 4, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() });
            changed(on);
        };
        return track;
    }

    public static Border PopupCard(UIElement child, double radius = 8)
    {
        return new Border
        {
            Background = Theme.PopupBackground,
            BorderBrush = Theme.Primary(0.12),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radius),
            Child = child,
            Margin = new Thickness(12),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Direction = 270, Opacity = 0.25 },
        };
    }

    public static Popup Popup(UIElement target, UIElement content, PlacementMode placement = PlacementMode.Bottom)
    {
        return new Popup
        {
            PlacementTarget = target,
            Placement = placement,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = content,
            HorizontalOffset = -12,
            VerticalOffset = -8,
        };
    }

    /// <summary>A drop-down menu button, like a SwiftUI Picker with the menu style.</summary>
    public static FrameworkElement Picker<T>(IReadOnlyList<(T Value, string Title)> items, T current, Action<T> changed)
    {
        var title = items.FirstOrDefault(i => EqualityComparer<T>.Default.Equals(i.Value, current)).Title ?? "";
        var button = new Clickable(Theme.Primary(0.06), Theme.Primary(0.1), Theme.Primary(0.14))
        {
            CornerRadius = new CornerRadius(6),
            BorderBrush = Theme.Primary(0.1),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 4, 8, 4),
            MinHeight = 28,
            Child = HStack(10, Text(title, 12.5), Glyph("", 9, Theme.Secondary)),
        };
        button.Click = () =>
        {
            var list = new StackPanel { Margin = new Thickness(4) };
            Popup? popup = null;
            foreach (var (value, name) in items)
            {
                bool selected = EqualityComparer<T>.Default.Equals(value, current);
                var row = new Clickable(null, Theme.Primary(0.07), Theme.Primary(0.1))
                {
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(8, 6, 16, 6),
                    Child = HStack(8, Glyph(selected ? "" : "", 11, Theme.Text).Also(g => g.Width = 14), Text(name, 12.5)),
                };
                var picked = value;
                row.Click = () =>
                {
                    popup!.IsOpen = false;
                    changed(picked);
                };
                list.Children.Add(row);
            }
            popup = Popup(button, PopupCard(list));
            popup.MinWidth = button.ActualWidth;
            popup.IsOpen = true;
        };
        return button;
    }

    /// <summary>A Windows 11 style slider.</summary>
    public static FrameworkElement Slider(double min, double max, double value, double width, Action<double> changed, Action<bool>? dragging = null)
    {
        const double thumb = 20;
        var grid = new Grid { Width = width, Height = 24, Background = Brushes.Transparent };
        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.Primary(0.18), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(thumb / 2, 0, thumb / 2, 0) };
        var fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.AccentBrush(), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(thumb / 2, 0, 0, 0) };
        var knob = new Grid { Width = thumb, Height = thumb, HorizontalAlignment = HorizontalAlignment.Left };
        knob.Children.Add(new Ellipse { Fill = Theme.Dark ? Theme.Hex(0x454545) : Brushes.White, Stroke = Theme.Primary(0.12), StrokeThickness = 1 });
        var dot = new Ellipse { Width = 12, Height = 12, Fill = Theme.AccentBrush() };
        knob.Children.Add(dot);
        grid.Children.Add(track);
        grid.Children.Add(fill);
        grid.Children.Add(knob);

        void Layout()
        {
            double usable = width - thumb;
            double t = (value - min) / (max - min);
            knob.Margin = new Thickness(usable * t, 0, 0, 0);
            fill.Width = Math.Max(0, usable * t);
        }
        void SetFrom(double x)
        {
            double usable = width - thumb;
            double t = Math.Clamp((x - thumb / 2) / usable, 0, 1);
            value = min + t * (max - min);
            Layout();
            changed(value);
        }
        Layout();
        grid.MouseLeftButtonDown += (_, e) =>
        {
            grid.CaptureMouse();
            dragging?.Invoke(true);
            dot.Width = dot.Height = 14;
            SetFrom(e.GetPosition(grid).X);
            e.Handled = true;
        };
        grid.MouseMove += (_, e) =>
        {
            if (grid.IsMouseCaptured)
            {
                SetFrom(e.GetPosition(grid).X);
            }
        };
        grid.MouseLeftButtonUp += (_, e) =>
        {
            if (grid.IsMouseCaptured)
            {
                grid.ReleaseMouseCapture();
            }
            e.Handled = true;
        };
        grid.LostMouseCapture += (_, _) =>
        {
            dot.Width = dot.Height = 12;
            dragging?.Invoke(false);
        };
        return grid;
    }

    public static FrameworkElement TextField(string placeholder, string text, Action<string> changed, int maxLength = 0)
    {
        var box = new TextBox
        {
            Text = text,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Theme.Text,
            CaretBrush = Theme.Text,
            SelectionBrush = Theme.AccentBrush(0.5),
            FontFamily = Theme.Font,
            FontSize = 13,
            Padding = new Thickness(6, 5, 6, 5),
            VerticalContentAlignment = VerticalAlignment.Center,
            MaxLength = maxLength,
        };
        var hint = Text(placeholder, 13, brush: Theme.Tertiary);
        hint.Margin = new Thickness(10, 0, 8, 0);
        hint.IsHitTestVisible = false;
        hint.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) =>
        {
            hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            changed(box.Text);
        };
        var grid = new Grid();
        grid.Children.Add(box);
        grid.Children.Add(hint);
        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = Theme.Primary(0.06),
            BorderBrush = Theme.Primary(0.1),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
    }

    public static FrameworkElement IconTile(UIElement content, Color tint)
    {
        var top = Color.FromArgb((byte)(255 * 0.85), tint.R, tint.G, tint.B);
        var grid = new Grid { Width = 24, Height = 24 };
        grid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new LinearGradientBrush(top, tint, 90),
        });
        grid.Children.Add(content);
        return grid;
    }

    public static FrameworkElement Keycaps(IEnumerable<string> caps)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var cap in caps)
        {
            Add(row, new Border
            {
                MinWidth = 22,
                Height = 20,
                CornerRadius = new CornerRadius(5),
                Background = Theme.Primary(0.1),
                BorderBrush = Theme.Primary(0.18),
                BorderThickness = new Thickness(0.5, 0.5, 0.5, 1.5),
                Padding = new Thickness(6, 0, 6, 0),
                Child = Text(cap, 12, FontWeights.Medium).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center),
            }, 4);
        }
        return row;
    }

    /// <summary>A coloured dot with a soft shadow and shine, the ink button and swatches.</summary>
    public static FrameworkElement InkDot(uint? rgb, double size)
    {
        double r = size * 13 / 40;
        var grid = new Grid { Width = size, Height = size, IsHitTestVisible = false };
        grid.Children.Add(new Ellipse
        {
            Width = 2 * r + 3,
            Height = 2 * r + 3,
            Fill = Theme.Solid(Colors.Black, 0.35),
            Margin = new Thickness(0, 2, 0, 0),
        });
        if (rgb is uint color)
        {
            grid.Children.Add(new Ellipse { Width = 2 * r, Height = 2 * r, Fill = Theme.Hex(color) });
            grid.Children.Add(new Ellipse
            {
                Width = r * 0.7,
                Height = r * 0.5,
                Fill = Theme.Solid(Colors.White, 0.35),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(-r * 0.4, -r * 0.6, 0, 0),
            });
        }
        else
        {
            grid.Children.Add(Rainbow(r));
        }
        return grid;
    }

    /// <summary>Six slices of colour, for "match the pointer".</summary>
    public static FrameworkElement Rainbow(double r)
    {
        uint[] colors = [0xFF3B30, 0xFF9500, 0xFFCC00, 0x34C759, 0x007AFF, 0xAF52DE];
        var canvas = new Canvas { Width = 2 * r, Height = 2 * r };
        for (int i = 0; i < colors.Length; i++)
        {
            double a0 = (i * 60 - 90) * Math.PI / 180;
            double a1 = ((i + 1) * 60 - 90) * Math.PI / 180;
            var figure = new PathFigure { StartPoint = new Point(r, r), IsClosed = true };
            figure.Segments.Add(new LineSegment(new Point(r + r * Math.Cos(a0), r + r * Math.Sin(a0)), true));
            figure.Segments.Add(new ArcSegment(new Point(r + r * Math.Cos(a1), r + r * Math.Sin(a1)), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
            canvas.Children.Add(new System.Windows.Shapes.Path { Data = new PathGeometry([figure]), Fill = Theme.Hex(colors[i]) });
        }
        return canvas;
    }

    /// <summary>The laser dot's toolbar icon: a red glow, a dot and a pale core.</summary>
    public static FrameworkElement LaserIcon(double size)
    {
        double k = size / 64;
        var red = Rgb.ToColor(InkColors.Red);
        var grid = new Grid { Width = size, Height = size, IsHitTestVisible = false };
        var glow = new RadialGradientBrush(Color.FromArgb(140, red.R, red.G, red.B), Color.FromArgb(0, red.R, red.G, red.B));
        grid.Children.Add(new Ellipse { Width = 36 * k, Height = 36 * k, Fill = glow });
        grid.Children.Add(new Ellipse { Width = 14 * k, Height = 14 * k, Fill = new SolidColorBrush(red) });
        grid.Children.Add(new Ellipse { Width = 6 * k, Height = 6 * k, Fill = new SolidColorBrush(Color.FromArgb(242, 255, 219, 209)) });
        return grid;
    }

    public static FrameworkElement PointerIcon(PointerStyle style, double size)
    {
        if (Pointers.AssetName(style) is not string name)
        {
            return LaserIcon(size);
        }
        return Picture(Hands.HandAssets.CachedThumbnail(name), size);
    }

    /// <summary>Thin scroll bars, as in Windows 11 Settings.</summary>
    public static ScrollViewer Scroller(UIElement content)
    {
        var viewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = content,
            Focusable = false,
        };
        viewer.Resources[typeof(ScrollBar)] = SlimScrollBar.Value;
        return viewer;
    }

    private static readonly Lazy<Style> SlimScrollBar = new(() =>
    {
        const string xaml = """
            <ControlTemplate TargetType="ScrollBar"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Grid Background="Transparent">
                <Track x:Name="PART_Track" IsDirectionReversed="True" Orientation="{TemplateBinding Orientation}">
                  <Track.Thumb>
                    <Thumb>
                      <Thumb.Template>
                        <ControlTemplate TargetType="Thumb">
                          <Border CornerRadius="3" Background="#80808080" Margin="3,2,3,2" />
                        </ControlTemplate>
                      </Thumb.Template>
                    </Thumb>
                  </Track.Thumb>
                </Track>
              </Grid>
            </ControlTemplate>
            """;
        var style = new Style(typeof(ScrollBar));
        style.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse(xaml)));
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 12.0));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 12.0));
        style.Seal();
        return style;
    });

    public static T Also<T>(this T value, Action<T> configure)
    {
        configure(value);
        return value;
    }

    public static double DpiScale(Visual visual) => VisualTreeHelper.GetDpi(visual).DpiScaleX;
}
