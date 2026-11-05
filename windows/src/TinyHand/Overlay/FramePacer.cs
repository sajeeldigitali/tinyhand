using System.Windows.Threading;

namespace TinyHand.Overlay;

/// <summary>
/// Drives the pointer's tick. While the hand moves it ticks once per display frame, following the compositor clock;
/// when the hand is still it drops to 20 ticks a second; when the pointer is off it stops.
/// The macOS app does the same with a 120 Hz and a 20 Hz timer.
/// </summary>
internal sealed class FramePacer : IDisposable
{
    public enum Pace
    {
        Stopped,
        Idle,
        Busy,
    }

    private readonly Dispatcher ui;
    private readonly Action tick;
    private readonly Thread thread;
    private readonly AutoResetEvent wake = new(false);
    private volatile Pace pace = Pace.Stopped;
    private volatile bool disposed;
    private int queued;
    private static bool? compositorClock;
    private static int quickReturns;

    public FramePacer(Dispatcher ui, Action tick)
    {
        this.ui = ui;
        this.tick = tick;
        thread = new Thread(Run) { IsBackground = true, Name = "TinyHand frame pacer", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    public Pace Current => pace;

    public void Set(Pace value)
    {
        if (pace == value)
        {
            return;
        }
        pace = value;
        wake.Set();
    }

    private void Run()
    {
        while (!disposed)
        {
            switch (pace)
            {
                case Pace.Stopped:
                    wake.WaitOne();
                    continue;
                case Pace.Idle:
                    wake.WaitOne(50);
                    break;
                default:
                    WaitForFrame();
                    break;
            }
            if (disposed || pace == Pace.Stopped)
            {
                continue;
            }
            if (Interlocked.Exchange(ref queued, 1) == 0)
            {
                ui.BeginInvoke(DispatcherPriority.Normal, () =>
                {
                    Volatile.Write(ref queued, 0);
                    tick();
                });
            }
        }
    }

    private static void WaitForFrame()
    {
        if (compositorClock != false)
        {
            try
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                uint result = Native.DCompositionWaitForCompositorClock(0, IntPtr.Zero, 16);
                double ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                quickReturns = ms < 1 ? quickReturns + 1 : 0;
                if (result == 0xFFFFFFFF || quickReturns > 20)
                {
                    Log.Write($"compositor clock unusable (result {result:X}); pacing with a timer");
                    compositorClock = false;
                    Native.timeBeginPeriod(1);
                }
                else
                {
                    compositorClock = true;
                    return;
                }
            }
            catch (EntryPointNotFoundException)
            {
                compositorClock = false;
                Native.timeBeginPeriod(1);
            }
            catch (DllNotFoundException)
            {
                compositorClock = false;
                Native.timeBeginPeriod(1);
            }
        }
        Thread.Sleep(8);
    }

    public void Dispose()
    {
        disposed = true;
        wake.Set();
    }
}
