using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TinyHand.Core;
using TinyHand.Hands;
using TinyHand.Overlay;
using TinyHand.Tray;
using TinyHand.UI;
using static TinyHand.Native;
using Frame = TinyHand.Overlay.Frame;

namespace TinyHand;

/// <summary>
/// `TinyHand.exe --self-test <folder>` exercises the real app on a Windows machine without a person at it:
/// it renders the pointer and every screen to PNGs, turns the pointer on, clicks and drags with synthetic
/// input, and writes report.txt. The macOS app has similar --render-test and --e2e-test switches.
/// </summary>
internal static class SelfTest
{
    private static readonly StringBuilder Report = new();
    private static int failures;
    private static string outDir = "";

    public static int Run(string output, string[] args)
    {
        outDir = Path.GetFullPath(output);
        Directory.CreateDirectory(outDir);
        Environment.SetEnvironmentVariable("TH_DATA_DIR", Path.Combine(outDir, "data"));
        bool interactive = !args.Contains("--no-input");
        int code = 1;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Fail("unhandled", e.Exception);
            e.Handled = true;
        };
        app.Startup += (_, _) =>
        {
            try
            {
                RunAll(interactive);
            }
            catch (Exception e)
            {
                Fail("self-test", e);
            }
            finally
            {
                CursorHider.Restore();
                Line($"done: {failures} failure(s)");
                File.WriteAllText(Path.Combine(outDir, "report.txt"), Report.ToString());
                code = failures == 0 ? 0 : 1;
                app.Shutdown(code);
            }
        };
        app.Run();
        return code;
    }

    private static void Line(string text)
    {
        Report.AppendLine(text);
        Console.WriteLine(text);
    }

    private static void Fail(string name, Exception e)
    {
        failures++;
        Line($"FAIL {name}: {e}");
    }

    private static void Check(string name, Action action)
    {
        try
        {
            action();
            Line($"PASS {name}");
        }
        catch (Exception e)
        {
            Fail(name, e);
        }
    }

    private static void Expect(bool condition, string what)
    {
        if (!condition)
        {
            throw new Exception("expected " + what);
        }
    }

    private static void RunAll(bool interactive)
    {
        Line($"TinyHand {Paths.Version} on Windows {Environment.OSVersion.Version}, {RuntimeInformation.ProcessArchitecture}");
        var primary = Monitor.Primary;
        Line($"primary display {primary.Bounds} scale {primary.Scale} work {primary.Work}");
        Pointers.Custom = new CustomHandStore(Paths.Hands);
        Theme.Forced = false;
        Theme.Refresh();

        Check("every hand loads and measures", () =>
        {
            foreach (var style in Pointers.BuiltInHands)
            {
                var a = HandAssets.Load(Pointers.AssetName(style)!);
                Expect(a is not null, $"{style.Id} to load");
            }
        });

        Check("thumbnails", () =>
        {
            var done = false;
            HandAssets.PreloadThumbnails(Pointers.AllCases.Select(Pointers.AssetName).OfType<string>(), () => done = true);
            for (int i = 0; i < 200 && !done; i++)
            {
                Pump(0.05);
            }
            Expect(done, "thumbnails to finish");
            Expect(HandAssets.CachedThumbnail("hand_lavender") is not null, "a lavender thumbnail");
        });

        Check("sounds load", () =>
        {
            foreach (var c in Enum.GetValues<TapSoundChoice>())
            {
                Expect(File.Exists(Paths.Sound(c.FileName())), c.FileName());
            }
            using var sound = new TapSound(TapSoundChoice.Thock);
            sound.Play();
        });

        string? draftPath = null;
        Check("make a hand", () =>
        {
            var hand = SyntheticHand();
            draftPath = Path.Combine(outDir, "synthetic-hand.png");
            File.WriteAllBytes(draftPath, ImageIO.EncodePng(hand));
            var (image, ink) = HandMaker.Make(ImageIO.Decode(draftPath));
            File.WriteAllBytes(Path.Combine(outDir, "made-hand.png"), ImageIO.EncodePng(image));
            Expect(HandMetrics.Measure(image) is not null, "the made hand to measure");
            Line($"  ink #{ink:X6}");
        });

        Check("render the pointer offscreen", RenderOffscreen);

        foreach (bool dark in new[] { false, true })
        {
            Theme.Forced = dark;
            Theme.Refresh();
            string look = dark ? "dark" : "light";
            Check($"settings ({look})", () =>
            {
                var settings = new SettingsWindow();
                foreach (var section in Enum.GetValues<SettingsSection>())
                {
                    Snapshot(settings.BuildForSnapshot(section), $"settings-{section.ToString().ToLowerInvariant()}-{look}.png", 780, 560);
                }
                Snapshot(settings.BuildForSnapshot(SettingsSection.Pointers, makingHand: true), $"settings-makehand-{look}.png", 780, 560);
                if (draftPath is not null)
                {
                    Snapshot(settings.BuildForSnapshot(SettingsSection.Pointers, makingHand: true, draftPath: draftPath), $"settings-draft-{look}.png", 780, 700);
                }
            });
            Check($"welcome ({look})", () => Snapshot(new WelcomeWindow().BuildContent(), $"welcome-{look}.png"));
        }
        Theme.Forced = null;
        Theme.Refresh();

        Check("toolbar", () =>
        {
            var toolbar = new Toolbar();
            toolbar.Select(Pointers.Lavender);
            toolbar.SetSoundOn(true);
            Snapshot(toolbar.BarContent, "toolbar.png", background: Theme.Hex(0x3A3F4A));
            Snapshot(toolbar.TrayContent, "toolbar-ink.png", background: Theme.Hex(0x3A3F4A));
            toolbar.ShowHint([("Click", "to tap"), ("Drag", "to draw"), ("Hold Alt", "to click for real"), ("Esc", "to exit")]);
            Pump(0.3);
            if (toolbar.HintContent is FrameworkElement hint)
            {
                Snapshot(hint, "toolbar-hint.png", background: Theme.Hex(0x3A3F4A));
            }
            toolbar.Hide();
        });

        Check("tray icon", () =>
        {
            using var messages = new MessageWindow();
            using var tray = new TrayIcon(messages);
            tray.SetState(true, "TinyHand self-test");
            Pump(0.2);
        });

        if (interactive)
        {
            Check("pointer on screen and clicks", LiveTest);
        }
        Check("cursor restored", () => Expect(!File.Exists(Paths.CursorMarker), "no hidden-cursor marker left"));
    }

    private static void RenderOffscreen()
    {
        const int w = 1600;
        const int h = 1000;
        using var renderer = OverlayRenderer.Offscreen(w, h);
        var asset = HandAssets.Load("hand_lavender")!;
        var rig = new ArmRig { HandLenFrac = 0.13, Unit = 1 };
        void Save(Frame frame, string name)
        {
            Expect(renderer.Render(frame), "the frame to draw");
            var pixels = renderer.ReadBack();
            var canvas = new Pixels(w, h);
            for (int i = 0; i < canvas.Data.Length; i += 4)
            {
                canvas.Data[i] = 0xE8;
                canvas.Data[i + 1] = 0xE4;
                canvas.Data[i + 2] = 0xE0;
                canvas.Data[i + 3] = 0xFF;
            }
            canvas.DrawOver(pixels, 0, 0);
            File.WriteAllBytes(Path.Combine(outDir, name), ImageIO.EncodePng(canvas));
            int covered = 0;
            for (int i = 3; i < pixels.Data.Length; i += 4)
            {
                if (pixels.Data[i] > 0)
                {
                    covered++;
                }
            }
            Line($"  {name}: {covered} drawn pixels");
            Expect(covered > 500, $"{name} to show something");
        }

        foreach (var (x, y, name, press) in new[] { (800.0, 500.0, "overlay-hand.png", false), (300.0, 200.0, "overlay-hand-left.png", false), (1300.0, 650.0, "overlay-hand-pressed.png", true) })
        {
            rig.Reset();
            if (press)
            {
                rig.Press();
                rig.Held = true;
            }
            rig.Step(0, x, w, h);
            var pose = rig.AimHand(x, h - y, h, asset.Metrics)!.Value;
            var scaled = HandAssets.Scaled(asset, 0.13 * h);
            renderer.SetHand(scaled.Key, scaled.Image);
            Save(new Frame { Height = h, Hand = pose, Metrics = asset.Metrics }, name);
        }

        var stroke = new InkStroke(new PathPoint(200, 700), InkColors.Purple, Math.Max(3, h * 0.0055), 1.5);
        for (int i = 1; i < 40; i++)
        {
            stroke.Extend(new PathPoint(200 + i * 20, 700 + Math.Sin(i / 4.0) * 80));
        }
        using var path = OverlayRenderer.BuildPath(stroke);
        rig.Reset();
        rig.Step(0, 900, w, h);
        var laserFrame = new Frame { Height = h, Laser = rig.AimLaser(900, h - 400, h) };
        laserFrame.Ink.Add(new InkDraw(path, stroke.Color, (float)stroke.Width, 1));
        Save(laserFrame, "overlay-laser-ink.png");
    }

    private static void LiveTest()
    {
        var primary = Monitor.Primary;
        using var messages = new MessageWindow();
        var hotKeys = new HotKeys(messages);
        var overlay = new OverlayController(Dispatcher.CurrentDispatcher, hotKeys);

        int clicks = 0;
        var catcher = new Window
        {
            Title = "TinyHand self-test target",
            Width = 520,
            Height = 360,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Background = Theme.Hex(0xF2F2F2),
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new TextBlock { Text = "click target", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 24 },
        };
        catcher.PreviewMouseLeftButtonDown += (_, _) => clicks++;
        catcher.Show();
        catcher.Activate();
        Pump(0.5);
        GetWindowRect(new System.Windows.Interop.WindowInteropHelper(catcher).Handle, out var target);
        int cx = target.Left + target.Width / 2;
        int cy = target.Top + target.Height / 2;
        Line($"  target {target}");

        Click(cx, cy);
        Pump(0.4);
        Line($"  clicks with pointer off: {clicks}");
        bool inputWorks = clicks > 0;
        if (!inputWorks)
        {
            Line("  synthetic input doesn't reach windows here; skipping the click checks");
        }

        SetCursorPos(cx, cy);
        bool hooked = overlay.Activate();
        Line($"  activate hooks={hooked}");
        Pump(1.0);
        Line($"  {overlay.DebugState}");
        Expect(overlay.IsActive, "the pointer to be on");
        Screenshot(primary, "live-pointer.png");

        if (inputWorks)
        {
            int before = clicks;
            Click(cx, cy);
            Pump(0.4);
            Line($"  clicks while on: {clicks - before} (should be 0)");
            Expect(clicks == before, "the click to be swallowed");

            SendInput(1, [INPUT.Mouse(0, 0, 0, MOUSEEVENTF_LEFTDOWN, UIntPtr.Zero)], Marshal.SizeOf<INPUT>());
            for (int i = 0; i < 30; i++)
            {
                SendInput(1, [INPUT.Mouse(6, 2, 0, MOUSEEVENTF_MOVE, UIntPtr.Zero)], Marshal.SizeOf<INPUT>());
                Pump(0.016);
            }
            SendInput(1, [INPUT.Mouse(0, 0, 0, MOUSEEVENTF_LEFTUP, UIntPtr.Zero)], Marshal.SizeOf<INPUT>());
            Pump(0.4);
            Line($"  after drag: {overlay.DebugState}");
            Expect(overlay.InkStrokes >= 1, "a drawn line");
            Expect(clicks == before, "the drag to be swallowed too");
            Screenshot(primary, "live-drawing.png");

            SendInput(1, [INPUT.Key(VK_LMENU, (ushort)MapVirtualKey(VK_LMENU, 0), 0, UIntPtr.Zero)], Marshal.SizeOf<INPUT>());
            Pump(0.2);
            Click(cx, cy);
            Pump(0.3);
            SendInput(1, [INPUT.Key(VK_LMENU, (ushort)MapVirtualKey(VK_LMENU, 0), KEYEVENTF_KEYUP, UIntPtr.Zero)], Marshal.SizeOf<INPUT>());
            Pump(0.3);
            Line($"  clicks while holding Alt: {clicks - before} (should be 1)");
            Expect(clicks == before + 1, "Alt+click to reach the app");
        }

        overlay.Exit();
        Pump(0.3);
        Expect(!overlay.IsActive, "the pointer to be off");
        Expect(!CursorHider.IsHidden, "the cursor to be back");
        if (inputWorks)
        {
            int before = clicks;
            Click(cx, cy);
            Pump(0.4);
            Expect(clicks == before + 1, "clicks to work again after turning off");
        }
        catcher.Close();
    }

    private static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        SendInput(2,
        [
            INPUT.Mouse(0, 0, 0, MOUSEEVENTF_LEFTDOWN, UIntPtr.Zero),
            INPUT.Mouse(0, 0, 0, MOUSEEVENTF_LEFTUP, UIntPtr.Zero),
        ], Marshal.SizeOf<INPUT>());
    }

    private static void Pump(double seconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Snapshot(FrameworkElement element, string name, double width = double.NaN, double height = double.NaN, Brush? background = null)
    {
        FrameworkElement root = element;
        if (element.Parent is null && VisualTreeHelper.GetParent(element) is null)
        {
            root = new Border { Child = element, Background = background ?? Theme.WindowBackground, Width = width, Height = height };
        }
        root.Measure(new Size(double.IsNaN(width) ? double.PositiveInfinity : width, double.IsNaN(height) ? double.PositiveInfinity : height));
        root.Arrange(new Rect(new Point(0, 0), root.DesiredSize));
        root.UpdateLayout();
        Pump(0.1);
        double w = Math.Max(1, root.ActualWidth > 0 ? root.ActualWidth : root.DesiredSize.Width);
        double h = Math.Max(1, root.ActualHeight > 0 ? root.ActualHeight : root.DesiredSize.Height);
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            ctx.DrawRectangle(background ?? Theme.WindowBackground, null, new Rect(0, 0, w, h));
            ctx.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, w, h));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(w * 2), (int)Math.Ceiling(h * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        ImageIO.SavePng(bitmap, Path.Combine(outDir, name));
        Line($"  {name} {w:0}x{h:0}");
    }

    private static void Screenshot(Monitor m, string name)
    {
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = m.Bounds.Width,
            biHeight = -m.Bounds.Height,
            biPlanes = 1,
            biBitCount = 32,
        };
        var bitmap = CreateDIBSection(memory, ref header, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(memory, bitmap);
        BitBlt(memory, 0, 0, m.Bounds.Width, m.Bounds.Height, screen, m.Bounds.Left, m.Bounds.Top, SRCCOPY | CAPTUREBLT);
        var pixels = new Pixels(m.Bounds.Width, m.Bounds.Height);
        Marshal.Copy(bits, pixels.Data, 0, pixels.Data.Length);
        for (int i = 3; i < pixels.Data.Length; i += 4)
        {
            pixels.Data[i] = 255;
        }
        SelectObject(memory, old);
        DeleteObject(bitmap);
        DeleteDC(memory);
        ReleaseDC(IntPtr.Zero, screen);
        File.WriteAllBytes(Path.Combine(outDir, name), ImageIO.EncodePng(pixels));
        Line($"  screenshot {name}");
    }

    private static Pixels SyntheticHand()
    {
        const int w = 600;
        const int h = 900;
        var p = new Pixels(w, h);
        void Fill(int x0, int y0, int x1, int y1, uint rgb)
        {
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int i = (y * w + x) * 4;
                    p.Data[i] = (byte)(rgb & 0xFF);
                    p.Data[i + 1] = (byte)((rgb >> 8) & 0xFF);
                    p.Data[i + 2] = (byte)((rgb >> 16) & 0xFF);
                    p.Data[i + 3] = 255;
                }
            }
        }
        Fill(150, 400, 450, 800, 0xE07040);
        Fill(170, 100, 240, 400, 0xE07040);
        Fill(150, 760, 450, 800, 0x8A3A20);
        return p;
    }
}
