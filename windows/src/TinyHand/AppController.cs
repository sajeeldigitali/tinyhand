using System.Windows;
using System.Windows.Threading;
using TinyHand.Core;
using TinyHand.Hands;
using TinyHand.Overlay;
using TinyHand.Tray;
using TinyHand.UI;
using Velopack;
using static TinyHand.Native;

namespace TinyHand;

/// <summary>Sets everything up and connects the tray icon, shortcuts, pointer and windows. A port of AppDelegate.</summary>
internal sealed class AppController
{
    private const int MenuToggle = 1;
    private const int MenuClear = 2;
    private const int MenuSound = 3;
    private const int MenuSettings = 4;
    private const int MenuUpdates = 5;
    private const int MenuQuit = 6;

    private readonly EventWaitHandle showSignal;
    private MessageWindow messages = null!;
    private HotKeys hotKeys = null!;
    private TrayIcon tray = null!;
    private OverlayController overlay = null!;
    private SettingsWindow settings = null!;
    private WelcomeWindow welcome = null!;
    private readonly Updater updater = new();
    private int? toggleHotKey;
    private int? clearHotKey;
    private IntPtr desktopHook;
    private WinEventProc? desktopProc;

    public AppController(EventWaitHandle showSignal)
    {
        this.showSignal = showSignal;
    }

    public void Start()
    {
        Log.Write($"launch {Environment.ProcessPath} version={Paths.Version} os={Environment.OSVersion.Version}");
        Pointers.Custom = new CustomHandStore(Paths.Hands);
        Theme.Refresh();
        HandAssets.PreloadThumbnails(Pointers.AllCases.Select(Pointers.AssetName).OfType<string>(), () => overlay?.Toolbar.Select(overlay.Style));

        messages = new MessageWindow();
        hotKeys = new HotKeys(messages);
        overlay = new OverlayController(Dispatcher.CurrentDispatcher, hotKeys);
        overlay.ActiveChanged += UpdateIcon;
        overlay.OnWarning = () => OpenSettings(SettingsSection.About);
        updater.IsBusy = () => overlay.IsActive;

        tray = new TrayIcon(messages)
        {
            OnClick = () => overlay.Toggle(),
            OnMenu = ShowMenu,
        };
        UpdateIcon();

        settings = new SettingsWindow
        {
            ApplyToggleShortcut = s => Apply(s, toggle: true),
            ApplyClearShortcut = s => Apply(s, toggle: false),
            OnRecording = recording =>
            {
                if (recording)
                {
                    UnregisterHotKeys();
                }
                else
                {
                    RegisterHotKeys();
                }
            },
            Updater = updater.IsAvailable ? updater : null,
        };
        welcome = new WelcomeWindow { OnStart = () => overlay.Activate() };

        messages.Listen(OnSystemMessage);
        WTSRegisterSessionNotification(messages.Handle, NOTIFY_FOR_THIS_SESSION);
        desktopProc = (_, _, _, _, _, _, _) => Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            if (overlay.IsActive)
            {
                Log.Write("desktop switched; pointer off");
                overlay.Exit();
            }
        });
        desktopHook = SetWinEventHook(EVENT_SYSTEM_DESKTOPSWITCH, EVENT_SYSTEM_DESKTOPSWITCH, IntPtr.Zero, desktopProc, 0, 0, WINEVENT_OUTOFCONTEXT);

        EnableLaunchAtLoginOnFirstRun();
        LaunchAtLogin.Refresh();
        updater.Start();
        if (!RegisterHotKeys())
        {
            Log.Write($"shortcut {Preferences.ToggleShortcut.Display} or {Preferences.ClearShortcut.Display} is taken by another app");
        }

        var dispatcher = Dispatcher.CurrentDispatcher;
        ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => dispatcher.BeginInvoke(() => OpenSettings(null)), null, -1, false);

        if (!Preferences.Flag("welcomeShown"))
        {
            welcome.Show();
        }
    }

    public void Stop()
    {
        overlay.Deactivate();
        CursorHider.Show();
        CursorHider.StopGuard();
        if (desktopHook != IntPtr.Zero)
        {
            UnhookWinEvent(desktopHook);
        }
        WTSUnRegisterSessionNotification(messages.Handle);
        tray.Dispose();
        Log.Write("quit");
    }

    private bool OnSystemMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_WTSSESSION_CHANGE when (int)wParam == WTS_SESSION_LOCK:
            case WM_POWERBROADCAST when (int)wParam == PBT_APMSUSPEND:
                if (overlay.IsActive)
                {
                    Log.Write("locked or sleeping; pointer off");
                    overlay.Exit();
                }
                break;
            case WM_SETTINGCHANGE when MessageWindow.SettingName(lParam) == "ImmersiveColorSet":
                Theme.Refresh();
                break;
        }
        return false;
    }

    private void ShowMenu()
    {
        bool on = overlay.IsActive;
        var items = new List<MenuItem>
        {
            new(MenuToggle, $"{(on ? "Turn Pointer Off" : "Turn Pointer On")}\t{Preferences.ToggleShortcut.Display}"),
            new(MenuClear, $"Clear Drawing\t{Preferences.ClearShortcut.Display}"),
            new(MenuSound, "Tap Sound", Checked: overlay.SoundOn),
            MenuItem.Line,
            new(MenuSettings, "Settings…"),
        };
        if (updater.IsAvailable)
        {
            items.Add(new(MenuUpdates, "Check for Updates…"));
        }
        items.Add(MenuItem.Line);
        items.Add(new(MenuQuit, "Quit TinyHand"));

        overlay.SuspendInput(true);
        int chosen = tray.ShowMenu(items);
        overlay.SuspendInput(false);
        switch (chosen)
        {
            case MenuToggle:
                overlay.Toggle();
                break;
            case MenuClear:
                overlay.ClearInk();
                break;
            case MenuSound:
                overlay.SoundOn = !overlay.SoundOn;
                break;
            case MenuSettings:
                OpenSettings(null);
                break;
            case MenuUpdates:
                overlay.Exit();
                updater.CheckForUpdates(manual: true);
                break;
            case MenuQuit:
                Application.Current.Shutdown();
                break;
        }
    }

    private void OpenSettings(SettingsSection? section)
    {
        overlay.Exit();
        settings.Show(section);
    }

    private bool Apply(Shortcut s, bool toggle)
    {
        UnregisterHotKeys();
        var old = toggle ? Preferences.ToggleShortcut : Preferences.ClearShortcut;
        if (toggle)
        {
            Preferences.ToggleShortcut = s;
        }
        else
        {
            Preferences.ClearShortcut = s;
        }
        if (RegisterHotKeys())
        {
            return true;
        }
        UnregisterHotKeys();
        if (toggle)
        {
            Preferences.ToggleShortcut = old;
        }
        else
        {
            Preferences.ClearShortcut = old;
        }
        RegisterHotKeys();
        return false;
    }

    private bool RegisterHotKeys()
    {
        if (toggleHotKey is not null || clearHotKey is not null)
        {
            return true;
        }
        var t = Preferences.ToggleShortcut;
        var c = Preferences.ClearShortcut;
        toggleHotKey = hotKeys.Register(t.Vk, t.Modifiers, () => overlay.Toggle());
        clearHotKey = hotKeys.Register(c.Vk, c.Modifiers, () => overlay.ClearInk());
        return toggleHotKey is not null && clearHotKey is not null;
    }

    private void UnregisterHotKeys()
    {
        if (toggleHotKey is int t)
        {
            hotKeys.Unregister(t);
        }
        if (clearHotKey is int c)
        {
            hotKeys.Unregister(c);
        }
        toggleHotKey = null;
        clearHotKey = null;
    }

    private void UpdateIcon()
    {
        tray.SetState(overlay.IsActive, overlay.IsActive
            ? "TinyHand is on — click to turn off, right-click for the menu"
            : "TinyHand — click to turn on, right-click for the menu");
    }

    /// <summary>An installed copy opens at sign-in from the start, as the macOS app does from /Applications.</summary>
    private static void EnableLaunchAtLoginOnFirstRun()
    {
        if (Preferences.Flag("loginItemSetUp"))
        {
            return;
        }
        bool installed;
        try
        {
            installed = new UpdateManager(new Velopack.Sources.GithubSource(Updater.RepoUrl, null, false, null), null, null).IsInstalled;
        }
        catch (Exception)
        {
            installed = false;
        }
        if (!installed)
        {
            return;
        }
        Preferences.SetFlag("loginItemSetUp", true);
        LaunchAtLogin.Enabled = true;
        Log.Write($"first run: open at sign-in = {LaunchAtLogin.Enabled}");
    }
}
