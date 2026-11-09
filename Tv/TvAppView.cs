using System.Diagnostics;
using System.Text;
using CastDesk.Configuration;
using CastDesk.Diagnostics;
using CastDesk.Dial;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CastDesk.Tv;

/// <summary>
/// YouTube's own TV app (youtube.com/tv) in WebView2. YouTube serves that app only to TV browsers, so
/// the view identifies itself as one (<see cref="CastDeskOptions.EffectiveUserAgent"/>). The TV app does
/// everything a TV does: sign-in with a code, playback, and the casting session with the phone.
/// </summary>
internal sealed class TvAppView : UserControl
{
    private const string TvAppUrl = "https://www.youtube.com/tv";

    private WebView2 _webView = NewWebView(); // replaced by ReleaseBrowser
    private CoreWebView2Environment? _environment;
    private Task? _initialization;
    private string _screenInfoUrl = "";

    public TvAppView()
    {
        BackColor = Color.Black;
        Controls.Add(_webView);
    }

    private static WebView2 NewWebView() => new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };

    /// <summary>
    /// Closes the browser and its processes, which hold a few hundred MB even on a blank page. The sign-in stays
    /// in the profile; the next cast or Show Player starts the browser again (a second or two slower). Only for
    /// while the window is hidden and the TV app unloaded; does nothing while the browser is still starting.
    /// </summary>
    public void ReleaseBrowser()
    {
        if (_initialization is not { IsCompleted: true }) return;
        AppLog.Write("tv", "Nothing cast for a while; closing the TV app's browser to free its memory.");
        Controls.Remove(_webView);
        _webView.Dispose();
        _webView = NewWebView();
        Controls.Add(_webView);
        _environment = null;
        _initialization = null;
        BrowserProcessId = null;
    }

    /// <summary>The TV app reported its screen details (sent after a launch from the phone).</summary>
    public event EventHandler<IReadOnlyDictionary<string, string>>? ScreenInfoReported;

    /// <summary>The TV app asked to close (its own Exit).</summary>
    public event EventHandler? ExitRequested;

    public bool IsReady => _webView.CoreWebView2 != null;

    /// <summary>
    /// Creates the browser (once); it stays empty until a launch or <see cref="ShowHomeIfEmptyAsync"/>.
    /// Call after the control is visible.
    /// </summary>
    /// <param name="userAgent">The TV identity to present.</param>
    /// <param name="dialPort">The DIAL server port; the TV app reports its screen details to a URL on it.</param>
    public async Task InitializeAsync(string userAgent, int dialPort)
    {
        var initialization = _initialization ??= InitializeCoreAsync(userAgent, dialPort);
        try
        {
            await initialization;
        }
        catch
        {
            // If the browser couldn't be created (a locked profile, say), let the next cast try again rather than
            // fail for the rest of the session. Once it exists, a retry would only add its handlers twice.
            if (_initialization == initialization && _webView.CoreWebView2 == null) _initialization = null;
            throw;
        }
    }

    private async Task InitializeCoreAsync(string userAgent, int dialPort)
    {
        // The TV app's sign-in lives in this profile, separate from Edge.
        var profileFolder = Path.Combine(CastDeskOptions.DataDirectory, "WebView2");
        // HardwareMediaKeyHandling off: WebView2's own media controls would show as "Unknown app";
        // CastDesk shows its own instead (see WindowsMediaControls).
        var options = new CoreWebView2EnvironmentOptions(
            "--autoplay-policy=no-user-gesture-required --disable-features=HardwareMediaKeyHandling");
        _environment = await CoreWebView2Environment.CreateAsync(null, profileFolder, options);
        await _webView.EnsureCoreWebView2Async(_environment);

        var core = _webView.CoreWebView2;
        core.Settings.UserAgent = userAgent;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false; // no Ctrl+F, Ctrl+P and so on in a TV app

        // After a launch from the phone, the TV app posts its screen details to additionalDataUrl. That
        // URL is on the DIAL server, but answering it here avoids Chromium blocking a public page's
        // request to localhost, and the details go to the DIAL server for the phone to read.
        _screenInfoUrl = $"http://localhost:{dialPort}/apps/{DialHttpServer.AppName}/dial_data";
        core.AddWebResourceRequestedFilter(_screenInfoUrl + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;

        core.WindowCloseRequested += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        core.NewWindowRequested += OnNewWindowRequested;
        core.ProcessFailed += (_, e) => AppLog.Write("tv", $"Browser process failed: {e.ProcessFailedKind}");
        core.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess) AppLog.Write("tv", $"Navigation failed: {e.WebErrorStatus}");
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(LoadMediaSessionBridge());
        core.WebMessageReceived += OnWebMessageReceived;
        core.ContainsFullScreenElementChanged += (_, _) => PageFullscreenChanged?.Invoke(this, core.ContainsFullScreenElement);
        BrowserProcessId = (int)core.BrowserProcessId;

        AppLog.Write("tv", "TV app browser ready.");
    }

    /// <summary>The WebView2 browser process (its audio runs in a child of it); null before initialization.</summary>
    public int? BrowserProcessId { get; private set; }

    /// <summary>F11, Alt+Enter or the fullscreen button, inside the TV app. Raised on the UI thread.</summary>
    public event EventHandler? FullscreenToggleRequested;

    /// <summary>The page entered (true) or left (false) fullscreen itself. Raised on the UI thread.</summary>
    public event EventHandler<bool>? PageFullscreenChanged;

    /// <summary>What the TV app is playing changed. Raised on the UI thread.</summary>
    public event EventHandler<MediaState>? MediaStateChanged;

    /// <summary>The number of phones connected to the TV app changed. Raised on the UI thread.</summary>
    public event EventHandler<int>? ConnectedPhonesChanged;

    /// <summary>Sends "play", "pause", "togglePlay", "nexttrack" or "previoustrack" to the TV app.</summary>
    public void SendMediaCommand(string command) => PostToPage(command);

    /// <summary>Tells the fullscreen button which icon to show.</summary>
    public void ShowFullscreenState(bool isFullscreen) => PostToPage(isFullscreen ? "fullscreen-on" : "fullscreen-off");

    /// <summary>
    /// Presses Esc, the TV app's Back, as a real key press. A key event made by page script would be
    /// marked untrusted, and the TV app ignores those.
    /// </summary>
    public async Task PressBackAsync()
    {
        if (!IsReady) return;
        try
        {
            foreach (var eventType in new[] { "rawKeyDown", "keyUp" })
            {
                var keyEvent = $$"""{"type":"{{eventType}}","key":"Escape","code":"Escape","windowsVirtualKeyCode":27,"nativeVirtualKeyCode":27}""";
                await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", keyEvent);
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("tv", $"Back failed: {exception.Message}");
        }
    }

    private void PostToPage(string message)
    {
        if (IsReady) _webView.CoreWebView2.PostWebMessageAsString(message);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object || !root.TryGetProperty("type", out var type)) return;
            if (type.GetString() == "fullscreen")
            {
                FullscreenToggleRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (type.GetString() == "back")
            {
                _ = PressBackAsync();
                return;
            }
            if (type.GetString() == "phones")
            {
                if (root.TryGetProperty("connected", out var connected) && connected.TryGetInt32(out var phones))
                    ConnectedPhonesChanged?.Invoke(this, phones);
                return;
            }
            if (type.GetString() != "media") return;
            string Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() ?? "" : "";
            bool Flag(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.True;
            long Time(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number &&
                value.TryGetInt64(out var time) && time > 0 ? time : 0;
            MediaStateChanged?.Invoke(this, new MediaState(Flag("hasVideo"), Flag("playing"), Text("title"), Text("artist"), Text("artwork"),
                Flag("hasNext"), Flag("hasPrevious"), Flag("watching"), Flag("music"), Text("videoId"), Time("startedAt"), Time("endsAt")));
        }
        catch (System.Text.Json.JsonException)
        {
            // Not one of ours.
        }
    }

    private static string LoadMediaSessionBridge()
    {
        using var stream = typeof(TvAppView).Assembly.GetManifestResourceStream("CastDesk.page-bridge.js")
            ?? throw new InvalidOperationException("page-bridge.js is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Opens the TV app's home screen if nothing is loaded yet (for Show Player).</summary>
    public async Task ShowHomeIfEmptyAsync(string userAgent, int dialPort)
    {
        await InitializeAsync(userAgent, dialPort);
        var source = _webView.CoreWebView2.Source;
        if (string.IsNullOrEmpty(source) || source == "about:blank") _webView.CoreWebView2.Navigate(TvAppUrl);
    }

    /// <summary>Opens the TV app with the launch data a phone sent (pairing code, video, start time).</summary>
    public async Task LaunchAsync(string launchData, string userAgent, int dialPort)
    {
        await InitializeAsync(userAgent, dialPort);
        var query = string.IsNullOrEmpty(launchData) ? "" : launchData + "&";
        var url = $"{TvAppUrl}?{query}additionalDataUrl={Uri.EscapeDataString(_screenInfoUrl)}";
        AppLog.Write("tv", "Opening the TV app for a cast.");
        _webView.CoreWebView2.Navigate(url);
        _webView.Focus();
    }

    /// <summary>
    /// Unloads the TV app (blank page), so nothing plays or runs while CastDesk is hidden in the tray.
    /// The sign-in is kept in the browser profile.
    /// </summary>
    public void Unload()
    {
        if (!IsReady) return;
        var source = _webView.CoreWebView2.Source;
        if (string.IsNullOrEmpty(source) || source == "about:blank") return;
        AppLog.Write("tv", "Window hidden; unloading the TV app so nothing plays in the background.");
        _webView.CoreWebView2.Navigate("about:blank");
    }

    /// <summary>Back to the TV app's home screen (stops any video).</summary>
    public void GoHome()
    {
        if (IsReady) _webView.CoreWebView2.Navigate(TvAppUrl);
    }

    public void Reload()
    {
        if (IsReady) _webView.CoreWebView2.Navigate(TvAppUrl);
    }

    /// <summary>Switches the TV identity YouTube sees and reloads the TV app with it.</summary>
    public void ChangeUserAgent(string userAgent)
    {
        if (!IsReady || _webView.CoreWebView2.Settings.UserAgent == userAgent) return;
        _webView.CoreWebView2.Settings.UserAgent = userAgent;
        AppLog.Write("tv", "TV identity changed; reloading the TV app.");
        Reload();
    }

    public new void Focus() => _webView.Focus();

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var request = e.Request;
        if (request.Method != "OPTIONS")
        {
            var body = "";
            if (request.Content != null)
            {
                using var reader = new StreamReader(request.Content, Encoding.UTF8);
                body = reader.ReadToEnd();
            }
            var query = new Uri(request.Uri).Query.TrimStart('?');
            var details = HttpRequest.ParseForm(query + "&" + body);
            if (details.Count > 0)
            {
                AppLog.Write("tv", $"TV app reported its screen details ({details.Count} fields) for the phone.");
                ScreenInfoReported?.Invoke(this, details);
            }
        }
        e.Response = _environment!.CreateWebResourceResponse(Stream.Null, 200, "OK",
            "Access-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: GET, POST, OPTIONS\r\nAccess-Control-Allow-Headers: *\r\nContent-Type: text/plain");
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // A TV app has no second windows; open links (such as Terms and privacy) in the normal browser.
        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }
}
