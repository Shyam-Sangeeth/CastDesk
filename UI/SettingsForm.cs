using System.Diagnostics;
using CastDesk.Configuration;
using CastDesk.Diagnostics;
using CastDesk.Updates;

namespace CastDesk.UI;

/// <summary>
/// The Settings window (tray menu → Settings…), in two columns: general settings and updates on the left,
/// advanced ones on the right. Edits a copy of the settings; <see cref="Result"/> holds the new values after Save.
/// Updates are checked and installed straight away, without Save.
/// </summary>
internal sealed class SettingsForm : Form
{
    private const int FieldWidth = 400;

    private readonly TextBox _nameBox = new() { Width = FieldWidth, MaxLength = CastDeskOptions.MaxFriendlyNameLength };
    private readonly CheckBox _fullscreenOnCastBox = new() { Text = "Go fullscreen when a cast starts", AutoSize = true };
    private readonly CheckBox _hideWhenStoppedBox = new() { Text = "Hide the window when casting stops", AutoSize = true };
    private readonly CheckBox _keepPlayingBox = new() { Text = "Keep playing when my phone disconnects", AutoSize = true };
    private readonly CheckBox _startWithWindowsBox = new() { Text = "Start CastDesk when I sign in to Windows", AutoSize = true };
    private readonly NumericUpDown _portBox = new() { Minimum = CastDeskOptions.MinimumPort, Maximum = CastDeskOptions.MaximumPort, Width = 100 };
    private readonly TextBox _userAgentBox = new() { Width = FieldWidth, Height = 76, Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true };

    private readonly Label _updateStatus = new() { AutoSize = true, MaximumSize = new Size(FieldWidth, 0), Margin = new Padding(0, 2, 0, 4) };
    private readonly Button _checkUpdatesButton = new() { Text = "Check for updates", AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
    private readonly Button _installUpdateButton = new() { AutoSize = true, Visible = false, Margin = new Padding(0, 0, 6, 0) };
    private readonly LinkLabel _whatsNewLink = new() { Text = "What's new", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    private readonly ProgressBar _downloadProgress = new() { Width = FieldWidth, Height = 12, Visible = false, Margin = new Padding(0, 6, 0, 2) };
    private readonly CheckBox _autoCheckUpdatesBox = new() { Text = "Check for updates automatically", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    private readonly CheckBox _showInDiscordBox = new() { Text = "Show what I'm playing in Discord", AutoSize = true };
    private readonly CheckBox _showOnLockScreenBox = new() { Text = "Show the thumbnail on the lock screen", AutoSize = true };

    private readonly CastDeskOptions _original;
    private CancellationTokenSource? _downloadCancel;
    private bool _updateBusy;

    /// <param name="current">The settings to edit.</param>
    /// <param name="startsWithWindows">Whether CastDesk currently starts at sign-in.</param>
    /// <param name="knownUpdate">A newer release found by the automatic check, if any, so it can be installed at once.</param>
    public SettingsForm(CastDeskOptions current, bool startsWithWindows, UpdateInfo? knownUpdate = null)
    {
        _original = current;
        FoundUpdate = knownUpdate;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "CastDesk Settings";
        Font = new Font("Segoe UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true; // the main window may be hidden in the tray
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16, 4, 16, 12);

        var left = Column();
        left.Controls.Add(Heading("Receiver"));
        left.Controls.Add(Label("Name in your phone's Cast menu"));
        left.Controls.Add(_nameBox);
        left.Controls.Add(Hint("Phones may keep showing the old name for a few minutes after a change."));

        left.Controls.Add(Heading("Window"));
        left.Controls.Add(_fullscreenOnCastBox);
        left.Controls.Add(_hideWhenStoppedBox);
        left.Controls.Add(_keepPlayingBox);
        left.Controls.Add(Hint("Turn this off only if you cast from YouTube Music. In the YouTube app, tapping a new video " +
            "disconnects the phone, so the cast would stop too."));

        left.Controls.Add(Heading("Startup"));
        left.Controls.Add(_startWithWindowsBox);
        left.Controls.Add(Hint("CastDesk starts quietly in the notification area, ready for casts."));

        left.Controls.Add(Heading("Updates"));
        left.Controls.Add(_updateStatus);
        var updateActions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 0) };
        updateActions.Controls.Add(_checkUpdatesButton);
        updateActions.Controls.Add(_installUpdateButton);
        updateActions.Controls.Add(_whatsNewLink);
        left.Controls.Add(updateActions);
        left.Controls.Add(_downloadProgress);
        left.Controls.Add(_autoCheckUpdatesBox);
        _checkUpdatesButton.Click += async (_, _) => await CheckForUpdatesAsync();
        _installUpdateButton.Click += async (_, _) => await InstallUpdateAsync();
        _whatsNewLink.LinkClicked += (_, _) => OpenInBrowser(FoundUpdate?.PageUrl ?? UpdateService.ReleasesPageUrl);

        var right = Column();
        right.Margin = new Padding(32, 0, 0, 0);
        right.Controls.Add(Heading("Discord"));
        right.Controls.Add(_showInDiscordBox);
        right.Controls.Add(Hint("Your Discord status shows the title, channel and thumbnail while a video or song plays. " +
            "Needs the Discord app running on this PC."));

        right.Controls.Add(Heading("Lock screen"));
        right.Controls.Add(_showOnLockScreenBox);
        right.Controls.Add(Hint("While a video or song plays, its thumbnail is your lock screen picture, if YouTube has a " +
            "sharp one. Your own lock screen comes back when playback stops."));

        right.Controls.Add(Heading("Advanced"));
        right.Controls.Add(Label("Port phones connect to"));
        right.Controls.Add(_portBox);
        right.Controls.Add(Hint("Default 8765. If Windows has reserved the port or another app uses it, CastDesk picks a free one."));
        right.Controls.Add(Label("TV identity (user agent) YouTube sees"));
        right.Controls.Add(_userAgentBox);
        var resetUserAgentButton = new Button { Text = "Use the default", AutoSize = true, Margin = new Padding(0, 4, 3, 3) };
        resetUserAgentButton.Click += (_, _) => _userAgentBox.Text = CastDeskOptions.DefaultUserAgent;
        right.Controls.Add(resetUserAgentButton);
        right.Controls.Add(Hint("Change this only if YouTube stops showing its TV app."));

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = 2, Dock = DockStyle.Fill };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.Controls.Add(left, 0, 0);
        layout.Controls.Add(right, 1, 0);
        var buttonRow = CreateButtonRow();
        layout.Controls.Add(buttonRow, 0, 1);
        layout.SetColumnSpan(buttonRow, 2);
        Controls.Add(layout);

        _nameBox.Text = current.FriendlyName;
        _fullscreenOnCastBox.Checked = current.FullscreenOnCast;
        _hideWhenStoppedBox.Checked = current.HideWhenCastingStops;
        _keepPlayingBox.Checked = current.KeepPlayingWhenPhoneDisconnects;
        _startWithWindowsBox.Checked = startsWithWindows;
        _portBox.Value = Math.Clamp(current.DialPort, CastDeskOptions.MinimumPort, CastDeskOptions.MaximumPort);
        _userAgentBox.Text = current.EffectiveUserAgent;
        _autoCheckUpdatesBox.Checked = current.CheckForUpdates;
        _showInDiscordBox.Checked = current.ShowInDiscord;
        _showOnLockScreenBox.Checked = current.ShowOnLockScreen;
        ShowUpdateState(FoundUpdate == null
            ? $"You have CastDesk {UpdateService.CurrentVersion}."
            : $"CastDesk {FoundUpdate.Version} is available (you have {UpdateService.CurrentVersion}).");

        ResumeLayout(performLayout: true);
    }

    /// <summary>The edited settings, after Save.</summary>
    public CastDeskOptions? Result { get; private set; }

    /// <summary>Whether CastDesk should start at sign-in, after Save.</summary>
    public bool StartWithWindows => _startWithWindowsBox.Checked;

    /// <summary>The newer release, if one was found (here or by the automatic check).</summary>
    public UpdateInfo? FoundUpdate { get; private set; }

    /// <summary>The update's installer is running: CastDesk should quit so it can be replaced.</summary>
    public bool ExitForUpdate { get; private set; }

    // ======================================================================
    //  Updates
    // ======================================================================

    private async Task CheckForUpdatesAsync()
    {
        if (_updateBusy) return;
        _updateBusy = true;
        ShowUpdateState("Checking for updates…");
        try
        {
            FoundUpdate = await UpdateService.CheckAsync();
            ShowUpdateState(FoundUpdate == null
                ? $"You're up to date (CastDesk {UpdateService.CurrentVersion})."
                : $"CastDesk {FoundUpdate.Version} is available (you have {UpdateService.CurrentVersion}).");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        {
            AppLog.Write("update", $"Update check failed: {exception.Message}");
            ShowUpdateState($"Couldn't check for updates: {exception.Message}");
        }
        finally
        {
            _updateBusy = false;
            ShowUpdateState(null);
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (FoundUpdate is not { } update || _updateBusy) return;
        _updateBusy = true;
        _downloadCancel = new CancellationTokenSource();
        _downloadProgress.Value = 0;
        _downloadProgress.Visible = true;
        ShowUpdateState($"Downloading CastDesk {update.Version}…");
        try
        {
            var installer = await UpdateService.DownloadAsync(update,
                new Progress<double>(fraction => _downloadProgress.Value = (int)Math.Clamp(fraction * 100, 0, 100)),
                _downloadCancel.Token);
            ShowUpdateState($"Installing CastDesk {update.Version}… Windows asks for admin rights; CastDesk then closes and starts again.");
            UpdateService.RunInstaller(installer);
            ExitForUpdate = true;
            DialogResult = DialogResult.Cancel;
            Close();
        }
        catch (OperationCanceledException) when (_downloadCancel.IsCancellationRequested)
        {
            // The window was closed during the download.
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write("update", $"Update failed: {exception.Message}");
            ShowUpdateState($"Update failed: {exception.Message}");
        }
        finally
        {
            _updateBusy = false;
            if (!IsDisposed)
            {
                _downloadProgress.Visible = false;
                ShowUpdateState(null);
            }
        }
    }

    /// <summary>Shows <paramref name="status"/> (null keeps the current text) and which update buttons apply.</summary>
    private void ShowUpdateState(string? status)
    {
        if (status != null) _updateStatus.Text = status;
        _checkUpdatesButton.Enabled = !_updateBusy;
        _installUpdateButton.Visible = FoundUpdate != null;
        _installUpdateButton.Enabled = !_updateBusy;
        if (FoundUpdate != null) _installUpdateButton.Text = $"Download and install {FoundUpdate.Version}";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _downloadCancel?.Cancel();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _downloadCancel?.Dispose();
        base.Dispose(disposing);
    }

    private static void OpenInBrowser(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    // ======================================================================
    //  Save, Cancel and layout helpers
    // ======================================================================

    private Control CreateButtonRow()
    {
        var row = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Fill, Margin = new Padding(0, 18, 0, 0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var logLink = new LinkLabel { Text = "Open log folder", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 0) };
        logLink.LinkClicked += (_, _) =>
        {
            Directory.CreateDirectory(CastDeskOptions.DataDirectory);
            Process.Start(new ProcessStartInfo(CastDeskOptions.DataDirectory) { UseShellExecute = true });
        };

        var saveButton = new Button { Text = "Save", AutoSize = true, MinimumSize = new Size(88, 0) };
        var cancelButton = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(88, 0), DialogResult = DialogResult.Cancel };
        saveButton.Click += (_, _) => Save();
        AcceptButton = saveButton;
        CancelButton = cancelButton;

        row.Controls.Add(logLink, 0, 0);
        row.Controls.Add(saveButton, 1, 0);
        row.Controls.Add(cancelButton, 2, 0);
        return row;
    }

    private void Save()
    {
        var name = _nameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Enter a name for the Cast menu.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _nameBox.Focus();
            return;
        }

        var result = _original.Clone();
        result.FriendlyName = name;
        result.FullscreenOnCast = _fullscreenOnCastBox.Checked;
        result.HideWhenCastingStops = _hideWhenStoppedBox.Checked;
        result.KeepPlayingWhenPhoneDisconnects = _keepPlayingBox.Checked;
        result.CheckForUpdates = _autoCheckUpdatesBox.Checked;
        result.ShowInDiscord = _showInDiscordBox.Checked;
        result.ShowOnLockScreen = _showOnLockScreenBox.Checked;
        result.DialPort = (int)_portBox.Value;
        // Store an empty value for the default, so a future CastDesk with a newer default picks it up.
        var userAgent = _userAgentBox.Text.Trim();
        result.UserAgent = userAgent.Length == 0 || userAgent == CastDeskOptions.DefaultUserAgent ? "" : userAgent;

        Result = result;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static TableLayoutPanel Column() => new()
    {
        AutoSize = true,
        ColumnCount = 1,
        Dock = DockStyle.Top,
        Margin = Padding.Empty,
    };

    private static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 10.5F),
        Margin = new Padding(0, 14, 0, 4),
    };

    private static Label Label(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 6, 0, 2) };

    private static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(FieldWidth, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 2, 0, 2),
    };
}
