using System.Windows.Media;
using Microsoft.Win32;

namespace TinyHand.UI;

/// <summary>Colours and fonts that follow Windows' light or dark app mode and accent colour.</summary>
internal static class Theme
{
    public static event Action? Changed;

    public static bool Dark { get; private set; }
    public static Color Accent { get; private set; } = Color.FromRgb(0x00, 0x67, 0xC0);

    /// <summary>Lets self-tests render both looks.</summary>
    public static bool? Forced { get; set; }

    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static void Refresh()
    {
        bool dark = Forced ?? ReadDark();
        var accent = ReadAccent() ?? Color.FromRgb(0x00, 0x67, 0xC0);
        if (dark == Dark && accent == Accent)
        {
            return;
        }
        Dark = dark;
        Accent = accent;
        Changed?.Invoke();
    }

    private static bool ReadDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The taskbar can be dark while apps are light, so the tray icon follows its own setting.</summary>
    public static bool TaskbarLight
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("SystemUsesLightTheme") is int light && light == 1;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private static Color? ReadAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
            }
        }
        catch (Exception)
        {
        }
        return null;
    }

    public static SolidColorBrush Solid(Color color, double opacity = 1)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(color.A * opacity), color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush Hex(uint rgb, double opacity = 1) => Solid(Rgb.ToColor(rgb), opacity);

    /// <summary>Black in light mode and white in dark mode, like SwiftUI's Color.primary.</summary>
    public static SolidColorBrush Primary(double opacity) => Solid(Dark ? Colors.White : Colors.Black, opacity);

    public static Brush Text => Primary(Dark ? 0.92 : 0.88);
    public static Brush Secondary => Primary(Dark ? 0.6 : 0.55);
    public static Brush Tertiary => Primary(0.32);
    public static Brush WindowBackground => Dark ? Hex(0x202020) : Hex(0xF5F5F5);
    public static Brush SidebarBackground => Dark ? Hex(0x1A1A1A) : Hex(0xEAEAEA);
    public static Brush PopupBackground => Dark ? Hex(0x2C2C2C) : Hex(0xFBFBFB);
    public static Brush SlotBackground => Dark ? Hex(0x2B2B2B, 0.95) : Hex(0xFFFFFF, 0.95);
    public static Brush AccentBrush(double opacity = 1) => Solid(Accent, opacity);
    public static Brush Red => Hex(Dark ? 0xFF6961u : 0xD13438u);
    public static Brush Orange => Hex(0xF7A30Bu);
}
