using System.Runtime.InteropServices;
using System.Windows.Interop;
using static TinyHand.Native;

namespace TinyHand.Tray;

/// <summary>
/// A hidden window that receives hotkeys, tray icon clicks and system broadcasts such as theme changes,
/// session locks and the taskbar restarting.
/// </summary>
internal sealed class MessageWindow : IDisposable
{
    private readonly HwndSource source;
    private readonly List<Func<int, IntPtr, IntPtr, bool>> handlers = [];

    public IntPtr Handle => source.Handle;

    public MessageWindow()
    {
        source = new HwndSource(new HwndSourceParameters("TinyHand")
        {
            WindowStyle = WS_POPUP,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
            PositionX = -32000,
            PositionY = -32000,
            Width = 1,
            Height = 1,
        });
        source.AddHook(Hook);
    }

    /// <summary>Adds a handler that returns true when it handled the message.</summary>
    public void Listen(Func<int, IntPtr, IntPtr, bool> handler) => handlers.Add(handler);

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        foreach (var h in handlers.ToArray())
        {
            if (h(msg, wParam, lParam))
            {
                handled = true;
                break;
            }
        }
        return IntPtr.Zero;
    }

    public static string? SettingName(IntPtr lParam) => lParam == IntPtr.Zero ? null : Marshal.PtrToStringUni(lParam);

    public void Dispose() => source.Dispose();
}

/// <summary>System-wide shortcuts through RegisterHotKey. A port of HotKeyCenter in the macOS app.</summary>
internal sealed class HotKeys
{
    private readonly MessageWindow window;
    private readonly Dictionary<int, Action> handlers = [];
    private int nextId = 1;

    public HotKeys(MessageWindow window)
    {
        this.window = window;
        window.Listen((msg, wParam, _) =>
        {
            if (msg != WM_HOTKEY)
            {
                return false;
            }
            if (handlers.TryGetValue((int)wParam, out var action))
            {
                action();
            }
            return true;
        });
    }

    public int? Register(uint vk, uint modifiers, Action action)
    {
        int id = nextId++;
        if (!RegisterHotKey(window.Handle, id, modifiers | MOD_NOREPEAT, vk))
        {
            return null;
        }
        handlers[id] = action;
        return id;
    }

    public void Unregister(int id)
    {
        if (handlers.Remove(id))
        {
            UnregisterHotKey(window.Handle, id);
        }
    }
}
