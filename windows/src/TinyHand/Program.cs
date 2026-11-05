using System.Windows;
using Velopack;

namespace TinyHand;

internal static class Program
{
    private const string InstanceName = @"Local\TinyHand.Windows";
    private const string ShowName = @"Local\TinyHand.Windows.Show";

    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build().Run();

        int guard = Array.IndexOf(args, "--cursor-guard");
        if (guard >= 0 && guard + 1 < args.Length && int.TryParse(args[guard + 1], out int pid))
        {
            return CursorHider.RunGuard(pid);
        }

        int test = Array.IndexOf(args, "--self-test");
        if (test >= 0)
        {
            var output = test + 1 < args.Length ? args[test + 1] : Path.Combine(Path.GetTempPath(), "tinyhand-self-test");
            return SelfTest.Run(output, args);
        }

        using var instance = new Mutex(true, InstanceName, out bool first);
        if (!first)
        {
            try
            {
                EventWaitHandle.OpenExisting(ShowName).Set();
            }
            catch (Exception)
            {
            }
            return 0;
        }
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowName);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Write($"crash: {e.ExceptionObject}");
            CursorHider.Restore();
        };
        CursorHider.RecoverIfNeeded();

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string? lastError = null;
        var lastErrorAt = DateTime.MinValue;
        app.DispatcherUnhandledException += (_, e) =>
        {
            var text = e.Exception.ToString();
            if (text != lastError || DateTime.UtcNow - lastErrorAt > TimeSpan.FromSeconds(5))
            {
                Log.Write($"error: {text}");
            }
            lastError = text;
            lastErrorAt = DateTime.UtcNow;
            e.Handled = true;
        };
        var controller = new AppController(show);
        app.Startup += (_, _) => controller.Start();
        app.Exit += (_, _) => controller.Stop();
        return app.Run();
    }
}
