using System.Runtime.InteropServices;
using TinyHand.Core;
using TinyHand.Hands;
using static TinyHand.Native;

namespace TinyHand.Tray;

internal readonly record struct MenuItem(int Id, string Text, bool Checked = false, bool Separator = false)
{
    public static MenuItem Line => new(0, "", Separator: true);
}

/// <summary>
/// The hand in the notification area: click to turn the pointer on or off, right-click for the menu.
/// It stands in for the menu bar icon of the macOS app.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = WM_APP + 1;
    private const uint IconId = 1;

    private readonly MessageWindow window;
    private readonly uint taskbarCreated;
    private IntPtr iconOn;
    private IntPtr iconOff;
    private bool on;
    private string tip = "TinyHand";
    private bool added;

    public Action? OnClick { get; set; }
    public Action? OnMenu { get; set; }

    public TrayIcon(MessageWindow window)
    {
        this.window = window;
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        window.Listen(Handle);
        BuildIcons();
        Add();
    }

    public void SetState(bool active, string tooltip)
    {
        on = active;
        tip = tooltip;
        Modify();
    }

    private bool Handle(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            switch (LowWord(lParam))
            {
                case WM_LBUTTONUP:
                case NIN_KEYSELECT:
                    OnClick?.Invoke();
                    break;
                case WM_CONTEXTMENU:
                    OnMenu?.Invoke();
                    break;
            }
            return true;
        }
        if (msg == taskbarCreated)
        {
            added = false;
            BuildIcons();
            Add();
            return false;
        }
        if (msg == WM_SETTINGCHANGE || msg == WM_DPICHANGED || msg == WM_DISPLAYCHANGE)
        {
            BuildIcons();
            Modify();
        }
        return false;
    }

    private NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = window.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = on ? iconOn : iconOff,
        szTip = tip,
        szInfo = "",
        szInfoTitle = "",
    };

    private void Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        added = Shell_NotifyIcon(NIM_ADD, ref data);
        if (!added)
        {
            Shell_NotifyIcon(NIM_DELETE, ref data);
            added = Shell_NotifyIcon(NIM_ADD, ref data);
        }
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
        Log.Write($"tray icon added={added}");
    }

    private void Modify()
    {
        if (!added)
        {
            Add();
            return;
        }
        var data = Data(NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>Shows a native menu at the cursor and returns the chosen item's id, or 0.</summary>
    public int ShowMenu(IEnumerable<MenuItem> items)
    {
        var menu = CreatePopupMenu();
        foreach (var item in items)
        {
            if (item.Separator)
            {
                AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            }
            else
            {
                AppendMenu(menu, MF_STRING | (item.Checked ? MF_CHECKED : 0), (UIntPtr)item.Id, item.Text);
            }
        }
        GetCursorPos(out var p);
        SetForegroundWindow(window.Handle);
        int chosen = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, p.X, p.Y, window.Handle, IntPtr.Zero);
        PostMessage(window.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
        return chosen;
    }

    /// <summary>Makes white or black icons from the menu bar images, to suit a dark or light taskbar.</summary>
    private void BuildIcons()
    {
        var oldOn = iconOn;
        var oldOff = iconOff;
        int size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem());
        uint color = UI.Theme.TaskbarLight ? 0x000000u : 0xFFFFFFu;
        iconOn = MakeIcon("menubar_on@3x.png", size, color);
        iconOff = MakeIcon("menubar_off@3x.png", size, color);
        if (oldOn != IntPtr.Zero)
        {
            DestroyIcon(oldOn);
        }
        if (oldOff != IntPtr.Zero)
        {
            DestroyIcon(oldOff);
        }
    }

    private static IntPtr MakeIcon(string file, int size, uint rgb)
    {
        try
        {
            var source = ImageIO.Decode(Path.Combine(Paths.Assets, file));
            var scaled = Resample.Resize(source, size, size);
            return CreateIcon(scaled, rgb);
        }
        catch (Exception e)
        {
            Log.Write($"tray icon {file} failed: {e.Message}");
            return IntPtr.Zero;
        }
    }

    public static IntPtr CreateIcon(Pixels p, uint rgb)
    {
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = p.Width,
            biHeight = -p.Height,
            biPlanes = 1,
            biBitCount = 32,
        };
        var color = CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        var buffer = new byte[p.Width * p.Height * 4];
        for (int i = 0; i < p.Width * p.Height; i++)
        {
            buffer[i * 4] = (byte)(rgb & 0xFF);
            buffer[i * 4 + 1] = (byte)((rgb >> 8) & 0xFF);
            buffer[i * 4 + 2] = (byte)((rgb >> 16) & 0xFF);
            buffer[i * 4 + 3] = p.Data[i * 4 + 3];
        }
        Marshal.Copy(buffer, 0, bits, buffer.Length);
        var mask = CreateBitmap(p.Width, p.Height, 1, 1, new byte[(p.Width + 15) / 16 * 2 * p.Height]);
        var info = new ICONINFO { fIcon = true, hbmMask = mask, hbmColor = color };
        var icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    public void Dispose()
    {
        if (added)
        {
            var data = Data(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            added = false;
        }
        DestroyIcon(iconOn);
        DestroyIcon(iconOff);
    }
}
