using System.Diagnostics;
using System.Reflection;
using TinyHand.Core;

namespace TinyHand;

internal static class Paths
{
    /// <summary>Settings, custom hands and the log live in %AppData%\TinyHand.</summary>
    public static string Data { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("TH_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TinyHand"));

    public static string Hands => Path.Combine(Data, "Hands");
    public static string Settings => Path.Combine(Data, "settings.json");
    public static string LogFile => Path.Combine(Data, "TinyHand.log");
    public static string CursorMarker => Path.Combine(Data, "cursor-hidden");

    public static string Assets => Path.Combine(AppContext.BaseDirectory, "Assets");
    public static string HandAsset(string name) => Path.Combine(Assets, "hands", name + ".png");
    public static string Sound(string file) => Path.Combine(Assets, "sounds", file);

    public static string Version { get; } =
        typeof(Paths).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "dev";
}

internal static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Paths.Data);
                var info = new FileInfo(Paths.LogFile);
                if (info.Exists && info.Length > 2_000_000)
                {
                    File.Move(Paths.LogFile, Paths.LogFile + ".old", overwrite: true);
                }
                File.AppendAllText(Paths.LogFile, line);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        Debug.Write(line);
    }
}

/// <summary>A display in physical pixels, with its scale relative to 96 DPI.</summary>
internal readonly record struct Monitor(IntPtr Handle, Native.RECT Bounds, Native.RECT Work, double Scale)
{
    public static Monitor FromPoint(Native.POINT p)
    {
        var handle = Native.MonitorFromPoint(p, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(handle, ref info);
        double scale = Native.GetDpiForMonitor(handle, Native.MDT_EFFECTIVE_DPI, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        return new Monitor(handle, info.rcMonitor, info.rcWork, scale);
    }

    public static Monitor AtCursor()
    {
        Native.GetCursorPos(out var p);
        return FromPoint(p);
    }

    public static Monitor Primary => FromPoint(new Native.POINT(0, 0));
}

internal static class Rgb
{
    public static System.Windows.Media.Color ToColor(uint rgb, byte alpha = 255) =>
        System.Windows.Media.Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static Vortice.Mathematics.Color4 ToColor4(uint rgb, float alpha = 1) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);
}

internal static class Shell
{
    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Log.Write($"open {target} failed: {e.Message}");
        }
    }

    public static void ShowLogFile()
    {
        try
        {
            if (File.Exists(Paths.LogFile))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Paths.LogFile}\"") { UseShellExecute = true });
            }
            else
            {
                Open(Paths.Data);
            }
        }
        catch (Exception e)
        {
            Log.Write($"show log failed: {e.Message}");
        }
    }
}

/// <summary>Lets the pointer run at full speed while it is on, so Windows doesn't slow it down to save power.</summary>
internal static class PowerThrottling
{
    public static void SetHighPerformance(bool on)
    {
        var state = new Native.PROCESS_POWER_THROTTLING_STATE
        {
            Version = 1,
            ControlMask = on ? Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
            StateMask = 0,
        };
        Native.SetProcessInformation(Native.GetCurrentProcess(), Native.ProcessPowerThrottling, ref state,
            System.Runtime.InteropServices.Marshal.SizeOf<Native.PROCESS_POWER_THROTTLING_STATE>());
    }
}
