using CastDesk.Configuration;
using CastDesk.Diagnostics;
using CastDesk.Interop;
using CastDesk.Media;
using CastDesk.Tv;
using CastDesk.UI;
using CastDesk.Updates;

namespace CastDesk;

/// <summary>
/// The player window (YouTube's TV app) and the tray icon. CastDesk starts in the tray; the window comes
/// to the front when a phone casts, and hides again a few seconds after the phone stops casting.
/// F11 (or Alt+Enter) toggles fullscreen. Inside the TV app, Esc is its Back button. The window's close
/// button hides it to the tray (see <see cref="CloseToTray"/>); only Exit in the tray menu quits.
/// </summary>
public sealed class MainForm : Form
{
    private const string AppName = "CastDesk";
    private const int MaxTrayTextLength = 127;
    private const int HideAfterStopMilliseconds = 3000;
    private const int UpdateCheckDelayMilliseconds = 30_000;
    private const int PauseTimeoutMilliseconds = 3000;
    private const int VideoPlayingInTrayMilliseconds = 1500;
    private const int LeftVideoPageMilliseconds = 3000;
    private const int PhoneGoneMilliseconds = 5000; // so a phone that reconnects straight away doesn't stop the video
    private const int ReleaseBrowserMilliseconds = 5 * 60 * 1000; // hidden this long with nothing loaded: free WebView2

    private readonly CastDeskController _controller;
    private readonly TvAppView _tvApp;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusMenuItem;
    private readonly System.Windows.Forms.Timer _hideAfterStopTimer;
    private readonly System.Windows.Forms.Timer _updateCheckTimer;
    private readonly System.Windows.Forms.Timer _phoneGoneTimer;
    private readonly System.Windows.Forms.Timer _releaseBrowserTimer;
    private readonly AudioSessionRenamer _audioSessionRenamer;
    private readonly DiscordPresence _discord;
    private readonly LockScreenArtwork _lockScreen;
    private WindowsMediaControls? _mediaControls;
    private CastDeskOptions _options;
    private SettingsForm? _settingsForm;
    private UpdateInfo? _availableUpdate;

    // Closed to the tray with the TV app still loaded (see CloseToTray).
    private readonly System.Windows.Forms.Timer _pauseTimeoutTimer;
    private readonly System.Windows.Forms.Timer _videoPlayingInTrayTimer;
    private readonly System.Windows.Forms.Timer _leftVideoPageTimer;
    private MediaState _mediaState = MediaState.Nothing;
    private bool _inTrayWithVideo;
    private bool _waitingForPause;

    private bool _allowVisible;
    private bool _isExiting;
    private bool _isFullscreen;
    private Rectangle _windowedBounds;
    private FormWindowState _windowedState = FormWindowState.Normal;

    public MainForm(CastDeskOptions options)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1280, 720);
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.Black;
        Text = AppName;
        Icon = LoadAppIcon(SystemInformation.IconSize);
        KeyPreview = true;

        _options = options;
        _controller = new CastDeskController(options);

        _tvApp = new TvAppView { Dock = DockStyle.Fill, ScreenName = options.FriendlyName };
        _discord = new DiscordPresence(() => _tvApp.BrowserProcessId) { Enabled = options.ShowInDiscord };
        _lockScreen = new LockScreenArtwork(options.ShowOnLockScreen, options.LockScreenFillsScreen);
        _tvApp.ScreenInfoReported += (_, details) => _controller.ReportScreenInfo(details);
        _tvApp.ExitRequested += (_, _) =>
        {
            _controller.ReportTvAppStopped();
            HideToTray();
        };
        _tvApp.MediaStateChanged += (_, state) =>
        {
            _mediaState = state;
            _mediaControls?.Show(state);
            _discord.Show(state);
            _lockScreen.Show(state);
            if (_inTrayWithVideo) OnMediaStateInTray(state);
        };
        _tvApp.ConnectedPhonesChanged += (_, phones) =>
        {
            AppLog.Write("tv", $"Phones connected: {phones}.");
            if (phones > 0) _phoneGoneTimer!.Stop();
            else _phoneGoneTimer!.Start();
        };
        _tvApp.FullscreenToggleRequested += (_, _) => ToggleFullscreen();
        _tvApp.PageFullscreenChanged += (_, isFullscreen) =>
        {
            if (isFullscreen) EnterFullscreen();
            else ExitFullscreen();
        };
        Controls.Add(_tvApp);

        // The volume mixer would otherwise call the TV app's sound "Microsoft Edge WebView2".
        _audioSessionRenamer = new AudioSessionRenamer(AppName, $"{Environment.ProcessPath},0", () => _tvApp.BrowserProcessId);

        _statusMenuItem = new ToolStripMenuItem("Starting receiver…") { Enabled = false };
        var showPlayerMenuItem = new ToolStripMenuItem("Show Player", null, (_, _) => ShowPlayer());
        showPlayerMenuItem.Font = new Font(showPlayerMenuItem.Font, FontStyle.Bold);
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add(_statusMenuItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(showPlayerMenuItem);
        trayMenu.Items.Add(new ToolStripMenuItem("Reload YouTube", null, (_, _) => _tvApp.Reload()));
        trayMenu.Items.Add(new ToolStripMenuItem("Restart Receiver", null, (_, _) => _controller.RestartReceiver()));
        trayMenu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings()));
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApplication()));

        _trayIcon = new NotifyIcon
        {
            Icon = LoadAppIcon(SystemInformation.SmallIconSize),
            Text = AppName,
            ContextMenuStrip = trayMenu,
            Visible = true,
        };
        _trayIcon.MouseClick += (_, click) => { if (click.Button == MouseButtons.Left) ShowPlayer(); };

        _hideAfterStopTimer = new System.Windows.Forms.Timer { Interval = HideAfterStopMilliseconds };
        _hideAfterStopTimer.Tick += (_, _) =>
        {
            _hideAfterStopTimer.Stop();
            HideToTray();
        };

        _controller.LaunchRequested += (_, launchData) => OnLaunchRequested(launchData);
        _pauseTimeoutTimer = new System.Windows.Forms.Timer { Interval = PauseTimeoutMilliseconds };
        _pauseTimeoutTimer.Tick += (_, _) => OnPauseTimedOut();
        _videoPlayingInTrayTimer = new System.Windows.Forms.Timer { Interval = VideoPlayingInTrayMilliseconds };
        _videoPlayingInTrayTimer.Tick += (_, _) =>
        {
            _videoPlayingInTrayTimer.Stop();
            AppLog.Write("tv", "A video is playing again; showing the window.");
            ShowWindow();
        };
        _leftVideoPageTimer = new System.Windows.Forms.Timer { Interval = LeftVideoPageMilliseconds };
        _leftVideoPageTimer.Tick += (_, _) =>
        {
            _leftVideoPageTimer.Stop();
            AppLog.Write("tv", "The TV app left its video page while in the tray.");
            HideToTray();
        };

        _phoneGoneTimer = new System.Windows.Forms.Timer { Interval = PhoneGoneMilliseconds };
        _phoneGoneTimer.Tick += (_, _) =>
        {
            _phoneGoneTimer.Stop();
            if (_options.KeepPlayingWhenPhoneDisconnects) return;
            AppLog.Write("tv", "The last phone disconnected; stopping.");
            _controller.ReportTvAppStopped(); // the phone's next cast launches the TV app again
            StopCasting();
        };

        // Hidden in the tray with the TV app unloaded (not playing music there): after a while, close the browser.
        _releaseBrowserTimer = new System.Windows.Forms.Timer { Interval = ReleaseBrowserMilliseconds };
        _releaseBrowserTimer.Tick += (_, _) =>
        {
            _releaseBrowserTimer.Stop();
            if (!Visible && !_inTrayWithVideo) _tvApp.ReleaseBrowser();
        };

        _controller.StopRequested += (_, _) => StopCasting();
        _controller.ReceiverStatusChanged += (_, status) => SetReceiverStatus(status);

        // A notification about a new version opens Settings, where it can be installed.
        _trayIcon.BalloonTipClicked += (_, _) => { if (_availableUpdate != null) OpenSettings(); };
        _updateCheckTimer = new System.Windows.Forms.Timer { Interval = UpdateCheckDelayMilliseconds };
        _updateCheckTimer.Tick += async (_, _) =>
        {
            _updateCheckTimer.Stop();
            await CheckForUpdatesQuietlyAsync();
        };

        ResumeLayout(performLayout: false);
        _controller.Start();
        if (_options.CheckForUpdates) _updateCheckTimer.Start();
    }

    /// <summary>A phone asked to stop, or the last one left: back to the home screen, then hide if the settings say so.</summary>
    private void StopCasting()
    {
        if (_inTrayWithVideo || !Visible)
        {
            // Unload rather than open the home screen, whose previews would play in the tray. When the window is
            // already hidden (and the TV app unloaded), this also keeps GoHome from loading it in the background.
            HideToTray();
            return;
        }
        _tvApp.GoHome();
        if (_options.HideWhenCastingStops) _hideAfterStopTimer.Start();
    }

    /// <summary>
    /// The automatic check, shortly after CastDesk starts (so it doesn't slow the start or Windows sign-in).
    /// Shows a tray notification if a newer version is out; failures are only logged.
    /// </summary>
    private async Task CheckForUpdatesQuietlyAsync()
    {
        if (!_options.CheckForUpdates) return;
        try
        {
            if (await UpdateService.CheckAsync() is not { } update) return;
            _availableUpdate = update;
            AppLog.Write("update", $"CastDesk {update.Version} is available.");
            _trayIcon.ShowBalloonTip(15000, $"CastDesk {update.Version} is available",
                "Click to open Settings and update.", ToolTipIcon.Info);
        }
        catch (Exception exception)
        {
            // Called from a timer (async void): any failure must only be logged, never crash CastDesk.
            AppLog.Write("update", $"Automatic update check failed: {exception.Message}");
        }
    }

    // ======================================================================
    //  Showing and hiding
    // ======================================================================

    protected override void SetVisibleCore(bool value)
    {
        // Application.Run makes the main form visible; CastDesk starts in the tray instead.
        // The handle is still created so the window can receive Close().
        if (!_allowVisible)
        {
            value = false;
            if (!IsHandleCreated) CreateHandle();
        }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowTheme.UseDarkTitleBar(Handle);
        if (_mediaControls != null) return;
        try
        {
            _mediaControls = new WindowsMediaControls(Handle);
            // Media buttons arrive on a background thread; WebView2 must be called on the UI thread.
            _mediaControls.CommandRequested += (_, command) =>
            {
                try
                {
                    if (!IsDisposed) BeginInvoke(() => _tvApp.SendMediaCommand(command));
                }
                catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
                {
                    // A media key pressed as CastDesk closes.
                }
            };
        }
        catch (Exception exception)
        {
            AppLog.Write("host", $"Windows media controls unavailable: {exception.Message}");
        }
    }

    private async void OnLaunchRequested(string launchData)
    {
        ShowWindow();
        if (_options.FullscreenOnCast) EnterFullscreen();
        try
        {
            await _tvApp.LaunchAsync(launchData, _controller.UserAgent, _controller.DialPort);
        }
        catch (Exception exception)
        {
            AppLog.Write("tv", $"Could not open the TV app: {exception}");
            SetReceiverStatus("Could not open YouTube. See castdesk.log.");
        }
    }

    /// <summary>Shows the window and, the first time, the TV app's home screen (Show Player, tray click).</summary>
    private async void ShowPlayer()
    {
        ShowWindow();

        // The browser is created the first time the window shows (WebView2 needs a visible window).
        try
        {
            await _tvApp.ShowHomeIfEmptyAsync(_controller.UserAgent, _controller.DialPort);
            _tvApp.Focus();
        }
        catch (Exception exception)
        {
            AppLog.Write("tv", $"Could not start the TV app browser: {exception}");
            SetReceiverStatus("Could not start the browser. See castdesk.log.");
        }
    }

    private void ShowWindow()
    {
        _hideAfterStopTimer.Stop();
        _releaseBrowserTimer.Stop();
        LeaveTrayWithVideo();
        _allowVisible = true;
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = _isFullscreen ? FormWindowState.Normal : _windowedState;
        BringToForeground();
    }

    private void BringToForeground()
    {
        Activate();
        // Windows may refuse to give focus to a background app; briefly making the window
        // topmost still raises it above the other windows.
        TopMost = true;
        TopMost = false;
    }

    /// <summary>
    /// Hides the window and unloads the TV app, so nothing (videos, home-screen previews) plays while
    /// CastDesk sits in the tray. A cast or Show Player loads it again. The close button uses
    /// <see cref="CloseToTray"/>, which keeps a video's cast going instead.
    /// </summary>
    private void HideToTray()
    {
        _hideAfterStopTimer.Stop();
        _phoneGoneTimer.Stop(); // the TV app is unloaded; a phone that left no longer matters
        LeaveTrayWithVideo();
        Hide();
        _tvApp.Unload();
        _releaseBrowserTimer.Stop();
        _releaseBrowserTimer.Start(); // restarts the 5 minutes
        _mediaControls?.Show(MediaState.Nothing);
        _discord.Show(MediaState.Nothing);
        _lockScreen.Show(MediaState.Nothing);
        _controller.ReportTvAppStopped(); // the phone's next cast launches the TV app again
    }

    /// <summary>
    /// The window's close button. On the TV app's home screen (or anywhere without a video) it hides to the tray
    /// and unloads the TV app, as <see cref="HideToTray"/>. With a video open, the TV app stays loaded and the
    /// phone stays connected: music (YouTube marks songs, music videos and podcasts) keeps playing in the tray,
    /// and any other video is paused. When a video plays again (from the phone, the media keys or Windows'
    /// media controls), the window opens again.
    /// </summary>
    private void CloseToTray()
    {
        var state = _mediaState;
        if (!state.Watching || !state.HasVideo)
        {
            HideToTray();
            return;
        }

        _hideAfterStopTimer.Stop();
        Hide();
        _inTrayWithVideo = true;
        if (state.Music)
        {
            AppLog.Write("tv", "Closed to the tray; the music keeps playing.");
            return;
        }

        AppLog.Write("tv", "Closed to the tray; the video is paused until it plays again.");
        if (!state.Playing) return;
        // Until the TV app reports the pause, its "playing" reports are from before it.
        _waitingForPause = true;
        _pauseTimeoutTimer.Start();
        _tvApp.SendMediaCommand("pause");
    }

    /// <summary>What the TV app reports while CastDesk is closed to the tray with a video open.</summary>
    private void OnMediaStateInTray(MediaState state)
    {
        if (!state.Watching)
        {
            // Back on the home screen (the queue ended, say): unload, so its previews don't play in the tray.
            _videoPlayingInTrayTimer.Stop();
            if (!_leftVideoPageTimer.Enabled) _leftVideoPageTimer.Start();
            return;
        }
        _leftVideoPageTimer.Stop();

        if (_waitingForPause)
        {
            if (state.Playing) return;
            _waitingForPause = false;
            _pauseTimeoutTimer.Stop();
        }

        // A short wait, so a report from between two songs (before the next one's details load) doesn't count.
        if (state.Playing && !state.Music)
        {
            if (!_videoPlayingInTrayTimer.Enabled) _videoPlayingInTrayTimer.Start();
        }
        else
        {
            _videoPlayingInTrayTimer.Stop();
        }
    }

    /// <summary>The video didn't pause: show it rather than let it play unseen.</summary>
    private void OnPauseTimedOut()
    {
        _pauseTimeoutTimer.Stop();
        if (!_inTrayWithVideo || !_waitingForPause) return;
        _waitingForPause = false;
        if (_mediaState.Playing && !_mediaState.Music)
        {
            AppLog.Write("tv", "The video didn't pause in the tray; showing the window.");
            ShowWindow();
        }
    }

    private void LeaveTrayWithVideo()
    {
        _inTrayWithVideo = false;
        _waitingForPause = false;
        _pauseTimeoutTimer.Stop();
        _videoPlayingInTrayTimer.Stop();
        _leftVideoPageTimer.Stop();
    }

    private void SetReceiverStatus(string status)
    {
        _statusMenuItem.Text = status;
        var trayText = $"{AppName}: {status}";
        _trayIcon.Text = trayText.Length <= MaxTrayTextLength ? trayText : trayText[..(MaxTrayTextLength - 1)] + "…";
    }

    // ======================================================================
    //  Fullscreen (borderless, covering the taskbar)
    // ======================================================================

    private void ToggleFullscreen()
    {
        if (_isFullscreen) ExitFullscreen();
        else EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        if (_isFullscreen) return;
        _windowedState = WindowState == FormWindowState.Minimized ? FormWindowState.Normal : WindowState;
        _windowedBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _isFullscreen = true;
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Normal;
        Bounds = Screen.FromControl(this).Bounds;
        _tvApp.ShowFullscreenState(true);
    }

    private void ExitFullscreen()
    {
        if (!_isFullscreen) return;
        _isFullscreen = false;
        FormBorderStyle = FormBorderStyle.Sizable;
        Bounds = _windowedBounds;
        WindowState = _windowedState;
        _tvApp.ShowFullscreenState(false);
    }

    // ======================================================================
    //  Keyboard, tray menu and closing
    // ======================================================================

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        // Only reached when the TV app doesn't have focus; inside it, page-bridge.js catches these.
        if (keyData == Keys.F11 || keyData == (Keys.Alt | Keys.Enter))
        {
            ToggleFullscreen();
            return true;
        }
        return base.ProcessCmdKey(ref message, keyData);
    }

    /// <summary>Opens the Settings window (or brings it forward if it is already open) and applies Save.</summary>
    private void OpenSettings()
    {
        if (_settingsForm != null)
        {
            _settingsForm.Activate();
            return;
        }

        using var settingsForm = new SettingsForm(_options, StartupRegistration.IsEnabled, _availableUpdate);
        _settingsForm = settingsForm;
        try
        {
            var dialogResult = settingsForm.ShowDialog();
            _availableUpdate = settingsForm.FoundUpdate;
            if (settingsForm.ExitForUpdate)
            {
                // The installer replaces CastDesk's files and starts the new version.
                ExitApplication();
                return;
            }
            if (dialogResult != DialogResult.OK || settingsForm.Result is not { } updated) return;

            var problems = new List<string>();
            if (updated.Save() is { } saveProblem) problems.Add($"Could not save the settings: {saveProblem}");
            if (settingsForm.StartWithWindows != StartupRegistration.IsEnabled &&
                StartupRegistration.SetEnabled(settingsForm.StartWithWindows) is { } startupProblem)
                problems.Add($"Could not change the startup setting: {startupProblem}");

            var userAgentChanged = updated.EffectiveUserAgent != _options.EffectiveUserAgent;
            _options = updated;
            _controller.ApplyOptions(updated);
            _discord.Enabled = updated.ShowInDiscord;
            _lockScreen.Enabled = updated.ShowOnLockScreen;
            _lockScreen.FillScreen = updated.LockScreenFillsScreen;
            _tvApp.ScreenName = updated.FriendlyName;
            if (userAgentChanged) _tvApp.ChangeUserAgent(updated.EffectiveUserAgent);

            if (problems.Count > 0)
                MessageBox.Show(string.Join("\n\n", problems), "CastDesk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _settingsForm = null;
        }
    }

    /// <summary>Quits CastDesk (tray menu Exit, or <c>CastDesk.exe --exit</c>).</summary>
    public void ExitApplication()
    {
        _isExiting = true;
        // An open Settings window runs its own message loop, which would keep CastDesk (and --exit) waiting.
        // Close it, and finish exiting once its loop has ended and OpenSettings has returned.
        if (_settingsForm is { IsDisposed: false, Visible: true } settings)
        {
            settings.Close();
            BeginInvoke(ExitApplication);
            return;
        }
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The window's close button hides to the tray; only Exit (or Windows shutting down) quits.
        if (!_isExiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            CloseToTray();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _trayIcon.Visible = false;
        _controller.Dispose(); // stops discovery and tells phones CastDesk is gone
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hideAfterStopTimer.Dispose();
            _updateCheckTimer.Dispose();
            _phoneGoneTimer.Dispose();
            _releaseBrowserTimer.Dispose();
            _pauseTimeoutTimer.Dispose();
            _videoPlayingInTrayTimer.Dispose();
            _leftVideoPageTimer.Dispose();
            _audioSessionRenamer.Dispose();
            _discord.Dispose(); // closing the connection clears the Discord status
            _lockScreen.Dispose(); // puts the user's lock screen back
            _trayIcon.ContextMenuStrip?.Dispose();
            _trayIcon.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Icon LoadAppIcon(Size size)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "CastDesk.ico");
        try
        {
            return new Icon(iconPath, size);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        {
            AppLog.Write("host", $"Could not load {iconPath}: {exception.Message}");
            return SystemIcons.Application;
        }
    }
}
