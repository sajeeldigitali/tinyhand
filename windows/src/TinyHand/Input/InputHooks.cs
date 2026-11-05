using System.Runtime.InteropServices;
using System.Windows.Threading;
using TinyHand.Core;
using static TinyHand.Native;

namespace TinyHand.Input;

/// <summary>
/// Low-level mouse and keyboard hooks on their own thread. While the pointer is on they swallow clicks so
/// nothing underneath gets clicked, let clicks through in pass-through zones, and hold back the
/// "click for real" key so apps see a plain click. A port of InputTap in the macOS app.
/// </summary>
internal sealed class InputHooks
{
    /// <summary>Marks input TinyHand sends itself so the hooks let it straight through.</summary>
    public static readonly UIntPtr OwnInput = new(0x54484B59);

    private readonly Dispatcher ui;
    private readonly LowLevelProc mouseProc;
    private readonly LowLevelProc keyboardProc;
    private Thread? thread;
    private uint threadId;
    private IntPtr mouseHook;
    private IntPtr keyboardHook;

    private readonly object gate = new();
    private RECT[] passRects = [];
    private volatile bool active;
    private volatile InteractKey interactKey = InteractKey.Off;
    private volatile bool capturing;
    private readonly List<POINT> captured = [];
    private int lastEventTick = Environment.TickCount;
    private int wakeQueued;

    private bool passingThrough;
    private volatile bool interactPress;
    private volatile bool keyHeld;
    private bool keyReplayed;
    private uint heldVk;

    public Action<POINT>? MouseDown;
    public Action? MouseUp;
    public Action? InteractDown;
    public Action? InteractUp;
    public Action<bool>? InteractKeyChanged;
    public Action<POINT>? PassThroughPress;
    public Action? Wake;

    public InputHooks(Dispatcher ui)
    {
        this.ui = ui;
        mouseProc = MouseHook;
        keyboardProc = KeyboardHook;
    }

    public bool IsInstalled => thread is not null;
    public bool InteractHeld => keyHeld;
    public bool InteractPressing => interactPress;
    public int LastEventTick => Volatile.Read(ref lastEventTick);

    public InteractKey InteractKey
    {
        get => interactKey;
        set => interactKey = value;
    }

    public RECT[] PassThroughRects
    {
        get
        {
            lock (gate)
            {
                return passRects;
            }
        }
        set
        {
            lock (gate)
            {
                passRects = value;
            }
        }
    }

    /// <summary>Collects every raw mouse position while a button is down, so lines follow the mouse exactly.</summary>
    public bool Capturing
    {
        get => capturing;
        set
        {
            lock (gate)
            {
                captured.Clear();
            }
            capturing = value;
        }
    }

    public List<POINT> TakeCaptured()
    {
        lock (gate)
        {
            if (captured.Count == 0)
            {
                return [];
            }
            var points = new List<POINT>(captured);
            captured.Clear();
            return points;
        }
    }

    public bool Install()
    {
        if (thread is not null)
        {
            return true;
        }
        bool ok = false;
        using var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            threadId = GetCurrentThreadId();
            var module = GetModuleHandle(null);
            mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, module, 0);
            keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, module, 0);
            ok = mouseHook != IntPtr.Zero && keyboardHook != IntPtr.Zero;
            if (!ok)
            {
                Log.Write($"hooks failed: mouse={mouseHook != IntPtr.Zero} keyboard={keyboardHook != IntPtr.Zero} error={Marshal.GetLastWin32Error()}");
                Unhook();
                ready.Set();
                return;
            }
            Touch();
            ready.Set();
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            Unhook();
        })
        {
            IsBackground = true,
            Name = "TinyHand input hooks",
            Priority = ThreadPriority.Highest,
        };
        t.Start();
        ready.Wait(3000);
        if (!ok)
        {
            t.Join(500);
            return false;
        }
        thread = t;
        return true;
    }

    public void Uninstall()
    {
        var t = thread;
        if (t is null)
        {
            return;
        }
        thread = null;
        PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        t.Join(1000);
    }

    private void Unhook()
    {
        if (mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(mouseHook);
            mouseHook = IntPtr.Zero;
        }
        if (keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(keyboardHook);
            keyboardHook = IntPtr.Zero;
        }
    }

    public void SetActive(bool value)
    {
        lock (gate)
        {
            active = value;
            interactPress = false;
            passingThrough = false;
            keyHeld = false;
            keyReplayed = false;
        }
    }

    /// <summary>Asks for a <see cref="Wake"/> call on the next mouse move, used while the pointer idles.</summary>
    public void ArmWake() => Volatile.Write(ref wakeQueued, 0);

    private void Touch() => Volatile.Write(ref lastEventTick, Environment.TickCount);

    private void Post(Action action) => ui.BeginInvoke(DispatcherPriority.Send, action);

    private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }
        Touch();
        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        if (!active || ((info.flags & LLMHF_INJECTED) != 0 && info.dwExtraInfo == OwnInput))
        {
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }
        int msg = (int)wParam;
        switch (msg)
        {
            case WM_MOUSEMOVE:
                if (capturing)
                {
                    lock (gate)
                    {
                        captured.Add(info.pt);
                    }
                }
                if (Interlocked.Exchange(ref wakeQueued, 1) == 0)
                {
                    Post(() => Wake?.Invoke());
                }
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

            case WM_MOUSEWHEEL:
            case WM_MOUSEHWHEEL:
                if (keyHeld && !keyReplayed)
                {
                    // Ctrl+scroll should still zoom: let the held key reach the app first, in order.
                    keyReplayed = true;
                    uint flag = msg == WM_MOUSEWHEEL ? MOUSEEVENTF_WHEEL : MOUSEEVENTF_HWHEEL;
                    SendInput(2,
                    [
                        KeyInput(heldVk, up: false),
                        INPUT.Mouse(0, 0, (uint)(short)(info.mouseData >> 16), flag, OwnInput),
                    ], Marshal.SizeOf<INPUT>());
                    return 1;
                }
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        bool down = msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN;
        bool up = msg is WM_LBUTTONUP or WM_RBUTTONUP or WM_MBUTTONUP or WM_XBUTTONUP;
        if (!down && !up)
        {
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        if (down && interactKey != InteractKey.Off && keyHeld)
        {
            interactPress = true;
            Post(() => InteractDown?.Invoke());
        }
        if (interactPress)
        {
            if (up)
            {
                interactPress = false;
                Post(() => InteractUp?.Invoke());
            }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        if (down)
        {
            var pt = info.pt;
            RECT[] rects;
            lock (gate)
            {
                rects = passRects;
            }
            passingThrough = rects.Any(r => r.Contains(pt));
            if (passingThrough)
            {
                Post(() => PassThroughPress?.Invoke(pt));
            }
        }
        if (passingThrough)
        {
            if (up)
            {
                passingThrough = false;
            }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        if (down)
        {
            var pt = info.pt;
            Post(() => MouseDown?.Invoke(pt));
        }
        else
        {
            Post(() => MouseUp?.Invoke());
        }
        return 1;
    }

    private bool IsInteractVk(uint vk) => interactKey switch
    {
        InteractKey.Alt => vk is VK_LMENU or VK_RMENU or VK_MENU,
        InteractKey.Ctrl => vk is VK_LCONTROL or VK_RCONTROL or VK_CONTROL,
        InteractKey.Shift => vk is VK_LSHIFT or VK_RSHIFT or VK_SHIFT,
        _ => false,
    };

    private IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }
        Touch();
        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if ((info.flags & LLKHF_INJECTED) != 0 && info.dwExtraInfo == OwnInput)
        {
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }
        if (!active || interactKey == InteractKey.Off)
        {
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }
        int msg = (int)wParam;
        bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool up = msg is WM_KEYUP or WM_SYSKEYUP;

        if (IsInteractVk(info.vkCode))
        {
            if (down)
            {
                if (!keyHeld)
                {
                    keyHeld = true;
                    keyReplayed = false;
                    heldVk = info.vkCode;
                    Post(() => InteractKeyChanged?.Invoke(true));
                }
                // Held back, so a tap of Alt doesn't open an app's menu bar and a click arrives without it.
                return keyReplayed ? CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam) : 1;
            }
            if (up && keyHeld && info.vkCode == heldVk)
            {
                bool replayed = keyReplayed;
                keyHeld = false;
                keyReplayed = false;
                Post(() => InteractKeyChanged?.Invoke(false));
                return replayed ? CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam) : 1;
            }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        if (down && keyHeld && !keyReplayed)
        {
            // Another key with the held one, like Alt+Tab: send the held key first, then this key, in that order.
            keyReplayed = true;
            uint flags = (info.flags & LLKHF_EXTENDED) != 0 ? KEYEVENTF_EXTENDEDKEY : 0;
            SendInput(2,
            [
                KeyInput(heldVk, up: false),
                INPUT.Key((ushort)info.vkCode, (ushort)info.scanCode, flags, OwnInput),
            ], Marshal.SizeOf<INPUT>());
            return 1;
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private static INPUT KeyInput(uint vk, bool up)
    {
        uint flags = up ? KEYEVENTF_KEYUP : 0;
        if (vk is VK_RMENU or VK_RCONTROL)
        {
            flags |= KEYEVENTF_EXTENDEDKEY;
        }
        return INPUT.Key((ushort)vk, (ushort)MapVirtualKey(vk, 0), flags, OwnInput);
    }
}
