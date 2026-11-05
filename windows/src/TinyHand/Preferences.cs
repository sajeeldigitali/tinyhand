using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using TinyHand.Core;

namespace TinyHand;

/// <summary>A global shortcut: a virtual-key code with MOD_* flags, and how it reads, like "Ctrl+Alt+H".</summary>
internal sealed record Shortcut(uint Vk, uint Modifiers, string Display)
{
    public static readonly Shortcut ToggleDefault = new(0x48, Native.MOD_CONTROL | Native.MOD_ALT, "Ctrl+Alt+H");
    public static readonly Shortcut ClearDefault = new(0x58, Native.MOD_CONTROL | Native.MOD_ALT, "Ctrl+Alt+X");
}

/// <summary>Settings kept in %AppData%\TinyHand\settings.json. A port of Preferences in the macOS app.</summary>
internal static class Preferences
{
    public static event Action? Changed;

    private static JsonObject? data;

    private static JsonObject Data
    {
        get
        {
            if (data is not null)
            {
                return data;
            }
            try
            {
                data = File.Exists(Paths.Settings) ? JsonNode.Parse(File.ReadAllText(Paths.Settings)) as JsonObject : null;
            }
            catch (Exception e)
            {
                Log.Write($"settings unreadable, starting fresh: {e.Message}");
            }
            data ??= [];
            return data;
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.Data);
            var temp = Paths.Settings + ".tmp";
            File.WriteAllText(temp, Data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, Paths.Settings, overwrite: true);
        }
        catch (Exception e)
        {
            Log.Write($"settings not saved: {e.Message}");
        }
    }

    private static void Set(string key, JsonNode? value, bool notify = true)
    {
        Data[key] = value;
        Save();
        if (notify)
        {
            NotifyChanged();
        }
    }

    public static void NotifyChanged() => Changed?.Invoke();

    private static T Get<T>(string key, T fallback)
    {
        try
        {
            if (Data[key] is JsonValue v && v.TryGetValue(out T? value) && value is not null)
            {
                return value;
            }
        }
        catch (Exception)
        {
        }
        return fallback;
    }

    public static Shortcut ToggleShortcut
    {
        get => LoadShortcut("shortcut.toggle") ?? Shortcut.ToggleDefault;
        set => SaveShortcut(value, "shortcut.toggle");
    }

    public static Shortcut ClearShortcut
    {
        get => LoadShortcut("shortcut.clear") ?? Shortcut.ClearDefault;
        set => SaveShortcut(value, "shortcut.clear");
    }

    public const double PointerSizeMin = 0.08;
    public const double PointerSizeMax = 0.20;

    public static double PointerSize
    {
        get => Math.Clamp(Get("pointerSize", 0.13), PointerSizeMin, PointerSizeMax);
        set => Set("pointerSize", value);
    }

    public static bool ShowShadow
    {
        get => Get("showShadow", true);
        set => Set("showShadow", value);
    }

    public static double FadeSeconds
    {
        get => Get("fadeSeconds", 3.0);
        set => Set("fadeSeconds", value);
    }

    public static bool HandSound
    {
        get => Get("sound.hands", true);
        set => Set("sound.hands", value);
    }

    public static TapSoundChoice TapSound
    {
        get => Choices.ParseEnum<TapSoundChoice>(Get<string?>("tapSound", null)) ?? TapSoundChoice.Thock;
        set => Set("tapSound", value.ToString().ToLowerInvariant());
    }

    public static bool LaserSound
    {
        get => Get("sound.laser", false);
        set => Set("sound.laser", value);
    }

    public static bool InteractKeepsHand
    {
        get => Get("interactKeepsHand", false);
        set => Set("interactKeepsHand", value);
    }

    public static InteractKey InteractKey
    {
        get => Choices.ParseEnum<InteractKey>(Get<string?>("interactKey", null)) ?? InteractKey.Alt;
        set => Set("interactKey", value.ToString().ToLowerInvariant());
    }

    public static InkChoice InkChoice
    {
        get => Choices.ParseEnum<InkChoice>(Get<string?>("inkChoice", null)) ?? InkChoice.Match;
        set => Set("inkChoice", value.ToString().ToLowerInvariant());
    }

    public static bool ShowInkButton
    {
        get => Get("showInkButton", true);
        set => Set("showInkButton", value);
    }

    public static bool AutoUpdate
    {
        get => Get("autoUpdate", true);
        set => Set("autoUpdate", value);
    }

    public static List<PointerStyle> PinnedStyles
    {
        get
        {
            List<string>? raw = null;
            if (Data["pinnedStyles"] is JsonArray array)
            {
                raw = array.Select(n => n?.GetValue<string>() ?? "").ToList();
            }
            return Pointers.NormalizePinned(raw);
        }
        set => Set("pinnedStyles", new JsonArray(value.Select(s => (JsonNode?)JsonValue.Create(s.Id)).ToArray()));
    }

    public static PointerStyle SavedStyle
    {
        get => Pointers.Parse(Get<string?>("pointerStyle", null)) ?? Pointers.Lavender;
        set => Set("pointerStyle", value.Id, notify: false);
    }

    public static bool Flag(string key) => Get(key, false);

    public static void SetFlag(string key, bool value) => Set(key, value, notify: false);

    public static bool LaunchAtLogin
    {
        get => TinyHand.LaunchAtLogin.Enabled;
        set
        {
            TinyHand.LaunchAtLogin.Enabled = value;
            NotifyChanged();
        }
    }

    public static void ResetAll()
    {
        foreach (var key in new[]
                 {
                     "shortcut.toggle", "shortcut.clear", "pointerSize", "showShadow", "fadeSeconds", "sound.hands", "sound.laser",
                     "interactKey", "interactKeepsHand", "pinnedStyles", "pointerStyle", "inkChoice", "showInkButton", "tapSound",
                 })
        {
            Data.Remove(key);
        }
        Save();
        NotifyChanged();
    }

    private static Shortcut? LoadShortcut(string key)
    {
        if (Data[key] is not JsonObject o)
        {
            return null;
        }
        try
        {
            uint vk = o["vk"]!.GetValue<uint>();
            uint mods = o["modifiers"]!.GetValue<uint>();
            string display = o["display"]!.GetValue<string>();
            return vk == 0 ? null : new Shortcut(vk, mods, display);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void SaveShortcut(Shortcut s, string key) =>
        Set(key, new JsonObject { ["vk"] = s.Vk, ["modifiers"] = s.Modifiers, ["display"] = s.Display });
}

/// <summary>Opens TinyHand when you sign in, through the Run key, and respects the switch in Task Manager's Startup apps.</summary>
internal static class LaunchAtLogin
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string Name = "TinyHand";

    private static string Command => $"\"{Environment.ProcessPath}\" --autostart";

    public static bool Enabled
    {
        get
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey);
                if (run?.GetValue(Name) is not string)
                {
                    return false;
                }
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
                return approved?.GetValue(Name) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
        set
        {
            try
            {
                using var run = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value)
                {
                    run.SetValue(Name, Command);
                    using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
                    approved?.DeleteValue(Name, throwOnMissingValue: false);
                }
                else
                {
                    run.DeleteValue(Name, throwOnMissingValue: false);
                }
            }
            catch (Exception e)
            {
                Log.Write($"open at sign-in change failed: {e.Message}");
            }
        }
    }

    /// <summary>Keeps the Run entry pointing at this copy if the app moved.</summary>
    public static void Refresh()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(Name) is string current && current != Command)
            {
                run.SetValue(Name, Command);
            }
        }
        catch (Exception)
        {
        }
    }
}
