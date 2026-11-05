using System.Diagnostics;

namespace TinyHand;

/// <summary>
/// Hides the mouse cursor across the whole system while the hand stands in for it.
/// Windows has no switch for that, so every standard cursor is swapped for a blank one and
/// SPI_SETCURSORS puts the user's cursors back. A marker file and a small guard process make
/// sure the cursor comes back even if TinyHand crashes or is ended from Task Manager.
/// </summary>
internal static class CursorHider
{
    private static readonly uint[] CursorIds =
    [
        32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651, 32671, 32672,
    ];

    private static bool hidden;
    private static Process? guard;

    public static bool IsHidden => hidden;

    public static void Hide()
    {
        if (hidden)
        {
            return;
        }
        hidden = true;
        try
        {
            Directory.CreateDirectory(Paths.Data);
            File.WriteAllText(Paths.CursorMarker, Environment.ProcessId.ToString());
        }
        catch (Exception)
        {
        }
        EnsureGuard();
        var blank = CreateBlankCursor();
        if (blank == IntPtr.Zero)
        {
            Log.Write("could not make a blank cursor");
            return;
        }
        foreach (var id in CursorIds)
        {
            var copy = Native.CopyIcon(blank);
            if (copy != IntPtr.Zero && !Native.SetSystemCursor(copy, id))
            {
                Native.DestroyCursor(copy);
            }
        }
        Native.DestroyCursor(blank);
    }

    public static void Show()
    {
        if (!hidden)
        {
            return;
        }
        hidden = false;
        Restore();
    }

    /// <summary>Puts the user's cursors back and forgets the marker. Safe to call at any time.</summary>
    public static void Restore()
    {
        Native.SystemParametersInfo(Native.SPI_SETCURSORS, 0, IntPtr.Zero, 0);
        try
        {
            File.Delete(Paths.CursorMarker);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Run at launch: if the last run left the cursor hidden, bring it back.</summary>
    public static void RecoverIfNeeded()
    {
        if (File.Exists(Paths.CursorMarker))
        {
            Log.Write("cursor was left hidden by an earlier run; restoring it");
            Restore();
        }
    }

    private static IntPtr CreateBlankCursor()
    {
        const int size = 32;
        var andPlane = new byte[size * size / 8];
        var xorPlane = new byte[size * size / 8];
        Array.Fill(andPlane, (byte)0xFF);
        return Native.CreateCursor(Native.GetModuleHandle(null), 0, 0, size, size, andPlane, xorPlane);
    }

    private static void EnsureGuard()
    {
        if (guard is { HasExited: false } || Environment.ProcessPath is not string exe)
        {
            return;
        }
        try
        {
            guard = Process.Start(new ProcessStartInfo(exe, $"--cursor-guard {Environment.ProcessId}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception e)
        {
            Log.Write($"cursor guard did not start: {e.Message}");
        }
    }

    /// <summary>The guard process: waits for TinyHand to end, then brings the cursor back if it was left hidden.</summary>
    public static int RunGuard(int pid)
    {
        var handle = Native.OpenProcess(Native.SYNCHRONIZE, false, (uint)pid);
        if (handle != IntPtr.Zero)
        {
            Native.WaitForSingleObject(handle, 0xFFFFFFFF);
            Native.CloseHandle(handle);
        }
        Thread.Sleep(200);
        if (File.Exists(Paths.CursorMarker))
        {
            Restore();
        }
        return 0;
    }

    public static void StopGuard()
    {
        try
        {
            if (guard is { HasExited: false })
            {
                guard.Kill();
            }
        }
        catch (Exception)
        {
        }
        guard = null;
    }
}
