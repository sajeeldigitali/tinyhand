using static TinyHand.Native;

namespace TinyHand.Input;

/// <summary>
/// Finds the places where clicks should stay real while the pointer is on: the taskbar (Windows' menu bar and
/// tray in one), open menus, and shell flyouts like Start. The macOS app does the same for its menu bar and menus.
/// </summary>
internal static class ShellZones
{
    private static readonly HashSet<string> MenuClasses =
    [
        "#32768",
        "Xaml_WindowedPopupClass",
        "Net UI Tool Window",
        "Net UI Tool Window Layered",
        "MozillaDropShadowWindowClass",
        "ComboLBox",
        "TopLevelWindowForOverflowXamlIsland",
        "NotifyIconOverflowWindow",
        "Chrome_WidgetWin_2",
        "ControlCenterWindow",
    ];

    private static readonly HashSet<string> ShellHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartMenuExperienceHost.exe",
        "SearchHost.exe",
        "SearchApp.exe",
        "ShellExperienceHost.exe",
    };

    private static readonly Dictionary<uint, bool> ShellProcessCache = [];

    public static List<RECT> Taskbars()
    {
        var result = new List<RECT>();
        var main = FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero && GetWindowRect(main, out var r) && !r.IsEmpty)
        {
            result.Add(r);
        }
        var prev = IntPtr.Zero;
        for (int i = 0; i < 16; i++)
        {
            var next = FindWindowEx(IntPtr.Zero, prev, "Shell_SecondaryTrayWnd", null);
            if (next == IntPtr.Zero)
            {
                break;
            }
            if (GetWindowRect(next, out var sr) && !sr.IsEmpty)
            {
                result.Add(sr);
            }
            prev = next;
        }
        return result;
    }

    public static List<RECT> OpenMenus()
    {
        var result = new List<RECT>();
        uint self = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }
            var cls = ClassName(hwnd);
            bool menu = MenuClasses.Contains(cls);
            if (!menu && cls == "Windows.UI.Core.CoreWindow")
            {
                GetWindowThreadProcessId(hwnd, out uint shellPid);
                menu = IsShellHost(shellPid);
            }
            if (!menu)
            {
                return true;
            }
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == self || IsCloaked(hwnd) || !GetWindowRect(hwnd, out var r) || r.Width < 4 || r.Height < 4)
            {
                return true;
            }
            result.Add(r);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static bool IsShellHost(uint pid)
    {
        if (ShellProcessCache.TryGetValue(pid, out bool known))
        {
            return known;
        }
        var path = ProcessPath(pid);
        bool shell = path is not null && ShellHosts.Contains(Path.GetFileName(path));
        if (ShellProcessCache.Count > 256)
        {
            ShellProcessCache.Clear();
        }
        ShellProcessCache[pid] = shell;
        return shell;
    }
}
