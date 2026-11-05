using System.ComponentModel;
using System.Runtime.InteropServices;
using static TinyHand.Native;

namespace TinyHand.Overlay;

/// <summary>
/// A borderless, topmost window covering one display that every click falls straight through.
/// It has no pixels of its own; DirectComposition shows the renderer's swap chain in it.
/// </summary>
internal sealed class OverlayWindow : IDisposable
{
    private const string ClassName = "TinyHandOverlay";
    private static readonly WndProc Proc = WindowProc;
    private static bool registered;

    public IntPtr Handle { get; }
    public RECT Bounds { get; private set; }

    public OverlayWindow()
    {
        var instance = GetModuleHandle(null);
        if (!registered)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Proc,
                hInstance = instance,
                lpszClassName = ClassName,
            };
            if (RegisterClassEx(ref wc) == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx");
            }
            registered = true;
        }
        int exStyle = WS_EX_NOREDIRECTIONBITMAP | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        Handle = CreateWindowEx(exStyle, ClassName, "TinyHand pointer", WS_POPUP, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (Handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx");
        }
        SetLayeredWindowAttributes(Handle, 0, 255, LWA_ALPHA);
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch ((int)msg)
        {
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;
            case WM_NCHITTEST:
                return HTTRANSPARENT;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Place(RECT bounds)
    {
        Bounds = bounds;
        SetWindowPos(Handle, HWND_TOPMOST, bounds.Left, bounds.Top, bounds.Width, bounds.Height, SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    public void Show() =>
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOOWNERZORDER);

    public void Hide() => ShowWindow(Handle, SW_HIDE);

    /// <summary>Keeps the hand above other always-on-top windows, the toolbar included.</summary>
    public void BringToTop() =>
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

    public void Dispose() => DestroyWindow(Handle);
}
