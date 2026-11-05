using System.Windows;
using System.Windows.Threading;
using Velopack;
using Velopack.Sources;

namespace TinyHand;

/// <summary>Updates TinyHand from GitHub releases with Velopack, standing in for Sparkle in the macOS app.</summary>
internal sealed class Updater
{
    public const string RepoUrl = "https://github.com/sajeeldigitali/tinyhand";

    private readonly UpdateManager? manager;
    private DispatcherTimer? timer;
    private bool checking;

    /// <summary>Set by the app so an update never restarts TinyHand while the pointer is on.</summary>
    public Func<bool>? IsBusy { get; set; }

    public Updater()
    {
        try
        {
            manager = new UpdateManager(new GithubSource(RepoUrl, null, false, null), null, null);
        }
        catch (Exception e)
        {
            Log.Write($"updates unavailable: {e.Message}");
        }
    }

    /// <summary>Only an installed copy can update itself; a copy run from a zip can't.</summary>
    public bool IsAvailable
    {
        get
        {
            try
            {
                return manager?.IsInstalled == true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public bool AutomaticallyChecks
    {
        get => Preferences.AutoUpdate;
        set => Preferences.AutoUpdate = value;
    }

    public void Start()
    {
        if (!IsAvailable || timer is not null)
        {
            return;
        }
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        timer.Tick += (_, _) =>
        {
            timer.Interval = TimeSpan.FromHours(12);
            if (AutomaticallyChecks)
            {
                CheckForUpdates(manual: false);
            }
        };
        timer.Start();
    }

    public async void CheckForUpdates(bool manual = true)
    {
        if (manager is null || !IsAvailable)
        {
            if (manual)
            {
                MessageBox.Show("Updates work when TinyHand is installed with its setup program.", "TinyHand",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }
        if (checking)
        {
            return;
        }
        checking = true;
        try
        {
            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                if (manual)
                {
                    MessageBox.Show($"TinyHand {Paths.Version} is the newest version.", "You're up to date",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return;
            }
            var version = update.TargetFullRelease.Version.ToString();
            if (manual && MessageBox.Show($"TinyHand {version} is available. Download it and restart now?", "Update TinyHand",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }
            await manager.DownloadUpdatesAsync(update);
            Log.Write($"update {version} downloaded");
            if (!manual && (IsBusy?.Invoke() == true || MessageBox.Show($"TinyHand {version} is ready. Restart now to use it?",
                    "Update TinyHand", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes))
            {
                return;
            }
            CursorHider.Restore();
            CursorHider.StopGuard();
            manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
        }
        catch (Exception e)
        {
            Log.Write($"update check failed: {e.Message}");
            if (manual)
            {
                MessageBox.Show("TinyHand couldn't check for updates. Try again later.", "Update TinyHand",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            checking = false;
        }
    }
}
