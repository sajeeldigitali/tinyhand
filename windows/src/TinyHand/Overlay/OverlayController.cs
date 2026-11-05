using System.Diagnostics;
using System.Windows.Threading;
using TinyHand.Core;
using TinyHand.Hands;
using TinyHand.Input;
using TinyHand.Tray;
using TinyHand.UI;
using Vortice.Direct2D1;
using static TinyHand.Native;

namespace TinyHand.Overlay;

/// <summary>
/// Turns the pointer on and off and runs it: the overlay, the hooks, the toolbar, ink and sound.
/// A port of OverlayController in the macOS app.
/// </summary>
internal sealed class OverlayController
{
    private sealed class InkItem(InkStroke stroke)
    {
        public InkStroke Stroke { get; } = stroke;
        public ID2D1PathGeometry? Geometry;
        public int BuiltVersion = -1;
    }

    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(0.5);
    private const double FadeDuration = 0.6;

    private readonly Dispatcher ui;
    private readonly HotKeys hotKeys;
    private readonly InputHooks hooks;
    private readonly TapSound tapSound;
    private readonly FramePacer pacer;
    private readonly ArmRig rig = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private OverlayWindow? window;
    private OverlayRenderer? renderer;

    private PointerStyle style;
    private HandAsset? asset;
    private readonly List<InkItem> ink = [];
    private InkStroke? drawing;
    private bool inkDirty = true;

    private Monitor? monitor;
    private POINT? lastMouse;
    private (double X, double Y)? downPoint;
    private double lastTick;
    private long lastPoseKey;
    private bool pointerHidden;
    private bool normalCursor;
    private bool ownMenuOpen;
    private DateTime stillSince = DateTime.UtcNow;
    private DateTime revealUntil = DateTime.MinValue;
    private DateTime menuOpeningUntil = DateTime.MinValue;
    private DateTime lastButtonEvent = DateTime.MinValue;
    private DateTime lastMenuScan = DateTime.MinValue;
    private DateTime lastTaskbarScan = DateTime.MinValue;
    private DateTime lastHookCheck = DateTime.MinValue;
    private DateTime lastZOrder = DateTime.MinValue;
    private List<RECT> menuRects = [];
    private List<RECT> taskbars = [];
    private int? escHotKey;
    private int tickErrors;

    private double pointerSize = Preferences.PointerSize;
    private bool showShadow = Preferences.ShowShadow;
    private bool keepsHand = Preferences.InteractKeepsHand;
    private double fadeSeconds = Preferences.FadeSeconds;
    private InkChoice inkChoice = Preferences.InkChoice;

    public Toolbar Toolbar { get; }
    public bool IsActive { get; private set; }
    public event Action? ActiveChanged;
    public event Action? SoundChanged;

    /// <summary>Called when the toolbar's warning is clicked, to show where to look.</summary>
    public Action? OnWarning { get; set; }

    public OverlayController(Dispatcher ui, HotKeys hotKeys)
    {
        this.ui = ui;
        this.hotKeys = hotKeys;
        hooks = new InputHooks(ui)
        {
            MouseDown = OnMouseDown,
            MouseUp = () =>
            {
                Release();
                NoteButtonEvent();
            },
            InteractDown = () =>
            {
                rig.Press();
                rig.Held = true;
                NoteButtonEvent();
            },
            InteractUp = () =>
            {
                rig.Held = false;
                NoteButtonEvent();
            },
            InteractKeyChanged = _ => Wake(),
            PassThroughPress = pt =>
            {
                if (taskbars.Any(r => r.Contains(pt)))
                {
                    menuOpeningUntil = DateTime.UtcNow.AddSeconds(1);
                }
                NoteButtonEvent();
            },
            Wake = Wake,
        };
        tapSound = new TapSound(Preferences.TapSound);
        pacer = new FramePacer(ui, SafeTick);
        Toolbar = new Toolbar
        {
            OnPick = Pick,
            OnToggleSound = () => SoundOn = !SoundOn,
            OnExit = Exit,
            OnWarning = () =>
            {
                Exit();
                OnWarning?.Invoke();
            },
        };
        style = Preferences.SavedStyle;
        LoadStyle(style);
        Toolbar.Select(style);
        tapSound.Enabled = SoundOn;
        Toolbar.SetSoundOn(SoundOn);
        Preferences.Changed += OnPreferencesChanged;
    }

    private void OnPreferencesChanged()
    {
        pointerSize = Preferences.PointerSize;
        showShadow = Preferences.ShowShadow;
        keepsHand = Preferences.InteractKeepsHand;
        fadeSeconds = Preferences.FadeSeconds;
        inkChoice = Preferences.InkChoice;
        SyncSound();
        hooks.InteractKey = Preferences.InteractKey;
        tapSound.Load(Preferences.TapSound);
        var saved = Preferences.SavedStyle;
        if (saved != style || (asset is null && !style.IsLaser))
        {
            Pick(saved);
        }
        lastPoseKey = 0;
        if (IsActive)
        {
            Wake();
        }
    }

    public PointerStyle Style => style;

    public bool SoundOn
    {
        get => style.IsLaser ? Preferences.LaserSound : Preferences.HandSound;
        set
        {
            if (style.IsLaser)
            {
                Preferences.LaserSound = value;
            }
            else
            {
                Preferences.HandSound = value;
            }
        }
    }

    public void Pick(PointerStyle next)
    {
        LoadStyle(next);
        Preferences.SavedStyle = style;
        Toolbar.Select(style);
        SyncSound();
        lastPoseKey = 0;
    }

    private void LoadStyle(PointerStyle next)
    {
        style = Pointers.Exists(next.Id) ? next : Pointers.Lavender;
        asset = Pointers.AssetName(style) is string name ? HandAssets.Load(name) : null;
        if (!style.IsLaser && asset is null)
        {
            style = Pointers.Laser;
        }
    }

    private void SyncSound()
    {
        tapSound.Enabled = SoundOn;
        Toolbar.SetSoundOn(SoundOn);
        SoundChanged?.Invoke();
    }

    public void Toggle()
    {
        if (IsActive)
        {
            Deactivate();
        }
        else
        {
            Activate();
        }
    }

    public void Exit()
    {
        ClearInk();
        Deactivate();
    }

    public bool Activate()
    {
        if (IsActive)
        {
            return hooks.IsInstalled;
        }
        IsActive = true;
        monitor = null;
        lastPoseKey = 0;
        lastTick = clock.Elapsed.TotalSeconds;
        rig.Reset();
        tapSound.Warm();
        PowerThrottling.SetHighPerformance(true);
        CursorHider.Hide();
        normalCursor = false;
        pointerHidden = false;
        try
        {
            window ??= new OverlayWindow();
        }
        catch (Exception e)
        {
            Log.Write($"overlay window failed: {e}");
            IsActive = false;
            CursorHider.Show();
            PowerThrottling.SetHighPerformance(false);
            return false;
        }

        bool installed = hooks.Install();
        hooks.InteractKey = Preferences.InteractKey;
        hooks.SetActive(installed);
        Log.Write($"activate hooks={installed}");

        Tick();
        window.Show();
        stillSince = DateTime.UtcNow;
        pacer.Set(FramePacer.Pace.Busy);

        if (monitor is Monitor m)
        {
            Toolbar.Place(m);
        }
        Toolbar.SetWarning(!installed);
        Toolbar.Show();
        window.BringToTop();
        revealUntil = DateTime.UtcNow.AddSeconds(3);
        if (installed && !Preferences.Flag("firstRunHintShown"))
        {
            Preferences.SetFlag("firstRunHintShown", true);
            var parts = new List<(string, string)> { ("Click", "to tap"), ("Drag", "to draw") };
            if (Preferences.InteractKey != InteractKey.Off)
            {
                parts.Add(($"Hold {Preferences.InteractKey.Title()}", "to click for real"));
            }
            parts.Add(("Esc", "to exit"));
            Toolbar.ShowHint(parts);
            revealUntil = DateTime.UtcNow.AddSeconds(10);
            var hide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            hide.Tick += (_, _) =>
            {
                hide.Stop();
                Toolbar.HideHint();
            };
            hide.Start();
        }
        SetEscEnabled(true);
        ActiveChanged?.Invoke();
        return installed;
    }

    public void Deactivate()
    {
        if (!IsActive)
        {
            return;
        }
        IsActive = false;
        pacer.Set(FramePacer.Pace.Stopped);
        hooks.SetActive(false);
        hooks.Uninstall();
        hooks.PassThroughRects = [];
        hooks.Capturing = false;
        rig.Held = false;
        downPoint = null;
        ClearInk();
        window?.Hide();
        renderer?.Dispose();
        renderer = null;
        monitor = null;
        Toolbar.Hide();
        normalCursor = false;
        pointerHidden = false;
        SetEscEnabled(false);
        ownMenuOpen = false;
        CursorHider.Show();
        PowerThrottling.SetHighPerformance(false);
        Log.Write("deactivate");
        ActiveChanged?.Invoke();
    }

    /// <summary>Lets clicks reach TinyHand's own tray menu while it is open.</summary>
    public void SuspendInput(bool suspended)
    {
        if (!IsActive || !hooks.IsInstalled)
        {
            return;
        }
        ownMenuOpen = suspended;
        hooks.SetActive(!suspended);
        if (monitor is Monitor m)
        {
            GetCursorPos(out var p);
            UpdateZones(p, m, DateTime.UtcNow);
        }
    }

    public void ClearInk()
    {
        foreach (var item in ink)
        {
            item.Geometry?.Dispose();
        }
        ink.Clear();
        drawing = null;
        inkDirty = true;
    }

    public int InkStrokes => ink.Count;

    public string DebugState =>
        $"held={rig.Held} down={downPoint is not null} drawing={drawing is not null} strokes={ink.Count} hooks={hooks.IsInstalled} pace={pacer.Current}";

    private void OnMouseDown(POINT pt)
    {
        if (!IsActive)
        {
            return;
        }
        rig.Press();
        rig.Held = true;
        tapSound.Play();
        Toolbar.HideHint();
        if (monitor is Monitor m)
        {
            downPoint = (pt.X - m.Bounds.Left, pt.Y - m.Bounds.Top);
        }
        hooks.Capturing = true;
        Log.Write($"press at {pt.X},{pt.Y}");
        NoteButtonEvent();
    }

    private void Release()
    {
        if (downPoint is not null)
        {
            Log.Write($"release, stroke drawn={drawing is not null}");
        }
        rig.Held = false;
        downPoint = null;
        hooks.Capturing = false;
        if (drawing is not null)
        {
            drawing.End(clock.Elapsed.TotalSeconds);
            drawing = null;
            inkDirty = true;
        }
    }

    private void NoteButtonEvent()
    {
        lastButtonEvent = DateTime.UtcNow;
        Wake();
    }

    private void Wake()
    {
        stillSince = DateTime.UtcNow;
        if (!IsActive || pacer.Current == FramePacer.Pace.Busy)
        {
            return;
        }
        pacer.Set(FramePacer.Pace.Busy);
    }

    private void SafeTick()
    {
        try
        {
            Tick();
            tickErrors = 0;
        }
        catch (Exception e)
        {
            Log.Write($"tick failed: {e}");
            renderer?.Dispose();
            renderer = null;
            monitor = null;
            if (++tickErrors >= 3)
            {
                Log.Write("turning the pointer off after repeated errors");
                Deactivate();
            }
        }
    }

    private void Tick()
    {
        if (!IsActive || window is null)
        {
            return;
        }
        var now = DateTime.UtcNow;
        double seconds = clock.Elapsed.TotalSeconds;
        double dt = seconds - lastTick;
        lastTick = seconds;

        GetCursorPos(out var mouse);
        var m = Monitor.FromPoint(mouse);
        if (monitor is not Monitor current || current.Bounds.Left != m.Bounds.Left || current.Bounds.Top != m.Bounds.Top
            || current.Bounds.Width != m.Bounds.Width || current.Bounds.Height != m.Bounds.Height || current.Scale != m.Scale
            || renderer is null)
        {
            bool moving = monitor is not null;
            monitor = m;
            if (moving)
            {
                Release();
                revealUntil = now.AddSeconds(1.5);
            }
            ClearInk();
            window.Place(m.Bounds);
            if (renderer is null)
            {
                renderer = OverlayRenderer.ForWindow(window.Handle, m.Bounds.Width, m.Bounds.Height);
                Log.Write($"renderer {m.Bounds.Width}x{m.Bounds.Height} scale={m.Scale} warp={renderer.UsingWarp}");
            }
            else
            {
                renderer.Resize(m.Bounds.Width, m.Bounds.Height);
            }
            rig.Reset();
            rig.Unit = m.Scale;
            lastPoseKey = 0;
            if (moving)
            {
                Toolbar.Place(m);
            }
        }

        if (lastMouse is not POINT last || last.X != mouse.X || last.Y != mouse.Y)
        {
            lastMouse = mouse;
            stillSince = now;
        }

        if (downPoint is not null)
        {
            var points = hooks.TakeCaptured();
            points.Add(mouse);
            foreach (var p in points)
            {
                Drag(p.X - m.Bounds.Left, p.Y - m.Bounds.Top, m);
            }
        }

        UpdateZones(mouse, m, now);
        CheckHooks(now);
        if (now - lastZOrder > TimeSpan.FromSeconds(1))
        {
            lastZOrder = now;
            window.BringToTop();
        }

        bool fading = Draw(mouse, m, dt, seconds);

        if (downPoint is null)
        {
            UpdatePace(fading, now);
        }
    }

    private void Drag(double x, double y, Monitor m)
    {
        if (downPoint is not (double sx, double sy))
        {
            return;
        }
        if (drawing is null)
        {
            if (Math.Sqrt((x - sx) * (x - sx) + (y - sy) * (y - sy)) < 4 * m.Scale)
            {
                return;
            }
            double width = Math.Max(3 * m.Scale, m.Bounds.Height * 0.0055);
            drawing = new InkStroke(new PathPoint(sx, sy), inkChoice.Resolved(style), width, 1.5 * m.Scale);
            ink.Add(new InkItem(drawing));
            inkDirty = true;
        }
        if (drawing.Extend(new PathPoint(x, y)))
        {
            inkDirty = true;
        }
    }

    /// <summary>Poses the pointer and draws a frame if anything changed. Returns true while lines are fading.</summary>
    private bool Draw(POINT mouse, Monitor m, double dt, double seconds)
    {
        if (renderer is null)
        {
            return false;
        }
        double h = m.Bounds.Height;
        double x = mouse.X - m.Bounds.Left;
        double yUp = h - (mouse.Y - m.Bounds.Top);
        rig.HandLenFrac = pointerSize;
        rig.ShowShadow = showShadow;
        rig.Step(dt, x, m.Bounds.Width, h);

        var frame = new Frame { Height = (float)h, Unit = (float)m.Scale };
        bool keep = fadeSeconds <= 0;
        double delay = Math.Max(0.5, fadeSeconds);
        bool fading = false;
        for (int i = ink.Count - 1; i >= 0; i--)
        {
            var item = ink[i];
            double opacity = item.Stroke.Opacity(seconds, delay, FadeDuration, keep);
            if (item.Stroke.EndedAt is double ended && !keep)
            {
                if (opacity <= 0)
                {
                    item.Geometry?.Dispose();
                    ink.RemoveAt(i);
                    inkDirty = true;
                    continue;
                }
                if (seconds >= ended + delay)
                {
                    fading = true;
                }
            }
            if (item.Geometry is null || item.BuiltVersion != item.Stroke.Version)
            {
                item.Geometry?.Dispose();
                item.Geometry = OverlayRenderer.BuildPath(item.Stroke);
                item.BuiltVersion = item.Stroke.Version;
            }
        }
        foreach (var item in ink)
        {
            double opacity = item.Stroke.Opacity(seconds, delay, FadeDuration, keep);
            frame.Ink.Add(new InkDraw(item.Geometry!, item.Stroke.Color, (float)item.Stroke.Width, (float)opacity));
        }

        long key = ArmRig.PoseKey(pointerHidden ? 1 : 0, ink.Count, drawing?.Version ?? -1);
        if (!pointerHidden)
        {
            if (style.IsLaser)
            {
                var laser = rig.AimLaser(x, yUp, h);
                frame.Laser = laser;
                key = key * 31 + ArmRig.PoseKey(laser.Radius, laser.X, laser.Y);
            }
            else if (asset is not null && rig.AimHand(x, yUp, h, asset.Metrics) is HandPose pose)
            {
                var scaled = HandAssets.Scaled(asset, pointerSize * h);
                renderer.SetHand(scaled.Key, scaled.Image);
                frame.Hand = pose;
                frame.Metrics = asset.Metrics;
                key = key * 31 + ArmRig.PoseKey(pose.Rotation, pose.RodLength, pose.HandLength, pose.Width, pose.GripX, pose.Lift,
                    pose.Press, pose.ShadowOpacity, asset.Metrics.Aspect);
            }
        }
        if (key == lastPoseKey && !inkDirty && !fading)
        {
            return fading;
        }
        lastPoseKey = key;
        inkDirty = false;
        if (!renderer.Render(frame))
        {
            Log.Write("graphics device lost; rebuilding");
            renderer.Dispose();
            renderer = null;
            monitor = null;
        }
        return fading;
    }

    private void UpdatePace(bool busy, DateTime now)
    {
        if (pacer.Current == FramePacer.Pace.Idle)
        {
            if (busy)
            {
                Wake();
            }
            return;
        }
        bool still = now - stillSince > IdleAfter;
        if (!still || busy || rig.Held || rig.IsSettling || drawing is not null)
        {
            return;
        }
        hooks.ArmWake();
        pacer.Set(FramePacer.Pace.Idle);
    }

    private void UpdateZones(POINT mouse, Monitor m, DateTime now)
    {
        int S(double v) => (int)Math.Round(v * m.Scale);
        var bar = Toolbar.Frame;
        bool near = !bar.IsEmpty && bar.Inflate(S(80), 0).Union(bar.Offset(0, -S(90))).Inflate(0, S(10)).Contains(mouse);
        bool needsWarning = !hooks.IsInstalled;
        bool drawingNow = drawing is not null;
        Toolbar.SetRevealed(!drawingNow && (near || now < revealUntil || needsWarning));

        if (now - lastTaskbarScan > TimeSpan.FromSeconds(1))
        {
            lastTaskbarScan = now;
            taskbars = ShellZones.Taskbars();
        }
        bool overTaskbar = taskbars.Any(r => r.Contains(mouse));
        bool menuLikely = menuRects.Count > 0 || ownMenuOpen || now < menuOpeningUntil
            || now - lastButtonEvent < TimeSpan.FromSeconds(2)
            || taskbars.Any(r => r.Inflate(0, S(40)).Contains(mouse));
        var scanEvery = TimeSpan.FromSeconds(menuLikely ? 0.05 : 0.25);
        if (now - lastMenuScan > scanEvery)
        {
            lastMenuScan = now;
            menuRects = ShellZones.OpenMenus();
        }
        bool overMenu = overTaskbar || menuRects.Any(r => r.Contains(mouse)) || now < menuOpeningUntil;
        bool interacting = hooks.InteractHeld || hooks.InteractPressing;
        bool normal = !drawingNow && !keepsHand && (interacting || overMenu || ownMenuOpen);
        SetEscEnabled(menuRects.Count == 0 && !ownMenuOpen);

        if (normal != normalCursor)
        {
            string why = interacting ? "click-for-real key" : overTaskbar ? "taskbar" : "menu";
            Log.Write(normal ? $"real cursor ({why}) at {mouse.X},{mouse.Y}" : $"pointer back at {mouse.X},{mouse.Y}");
            normalCursor = normal;
            pointerHidden = normal;
            if (normal)
            {
                CursorHider.Show();
            }
            else
            {
                CursorHider.Hide();
            }
        }

        var rects = new List<RECT>(taskbars);
        rects.AddRange(menuRects);
        if (Toolbar.Revealed && !bar.IsEmpty)
        {
            rects.Add(bar);
        }
        hooks.PassThroughRects = [.. rects];
    }

    /// <summary>Windows quietly drops a slow low-level hook, so put it back if input arrives that it never saw.</summary>
    private void CheckHooks(DateTime now)
    {
        if (now - lastHookCheck < TimeSpan.FromSeconds(1) || !hooks.IsInstalled || ownMenuOpen)
        {
            return;
        }
        lastHookCheck = now;
        var info = new LASTINPUTINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
        {
            return;
        }
        int gap = unchecked((int)info.dwTime - hooks.LastEventTick);
        if (gap > 1500)
        {
            Log.Write($"hooks missed input for {gap} ms; reinstalling");
            hooks.Uninstall();
            bool ok = hooks.Install();
            hooks.InteractKey = Preferences.InteractKey;
            hooks.SetActive(ok);
            Toolbar.SetWarning(!ok);
        }
    }

    private void SetEscEnabled(bool enabled)
    {
        if (enabled && IsActive && escHotKey is null)
        {
            escHotKey = hotKeys.Register(VK_ESCAPE, 0, Exit);
        }
        if (!enabled && escHotKey is int id)
        {
            hotKeys.Unregister(id);
            escHotKey = null;
        }
    }

    /// <summary>For self-tests: the renderer of the live overlay, if one is up.</summary>
    public bool HasRenderer => renderer is not null;
}
