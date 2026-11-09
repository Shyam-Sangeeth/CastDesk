# CastDesk

A WinForms tray app (.NET 10, C#) that makes a Windows PC a **YouTube cast target**: the phone's YouTube app finds
"CastDesk-<PC name>" in its Cast menu like a smart TV, and CastDesk plays the video in YouTube's own TV app
(youtube.com/tv) hosted in WebView2. YouTube only: Spotify, Netflix and phone screen mirroring were tried and dropped
(see Rules). The README is the user documentation; keep it in step with the app.

## Commands

```powershell
dotnet build -c Debug       # bin\Debug\net10.0-windows10.0.19041.0\win-x64\CastDesk.exe
dotnet build -c Release     # bin\Release\net10.0-windows10.0.19041.0\win-x64\CastDesk.exe
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1   # MSI in installer\out (csproj version)
```

`dotnet run --project tools/readme-shots` retakes the README images off-screen (see `tools/README.md`); new helper
scripts go in `tools/`, which the app project excludes.

There are no unit tests: check changes with a build, the log (`%LOCALAPPDATA%\CastDesk\castdesk.log`, `[dial]` lines
show each phone request) and the user's phone. A running CastDesk locks its exe: `CastDesk.exe --exit` closes it (and
waits); it always starts hidden in the tray. `--add-startup` / `--remove-startup` are for the installer.

To inspect the TV app, start CastDesk once with
`WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS="--remote-debugging-port=9333 --remote-debugging-address=127.0.0.1"` and
drive it over CDP. `POST http://localhost:8765/apps/YouTube` opens the TV app as a phone would.

To check page-bridge.js without stopping the installed copy or making sound, use headless Edge
(`msedge --headless=new --mute-audio --remote-debugging-port=… --user-data-dir=<temp>`): set the TV user agent with
CDP `Network.setUserAgentOverride` (the `--user-agent` switch alone gets the desktop site), `Page.enable`, then
`Page.addScriptToEvaluateOnNewDocument` with a stub `window.chrome.webview` (`postMessage` into an array) followed by
the bridge, and open `https://www.youtube.com/tv#/watch?v=<id>`. Signed out is fine. Bringing another tab to the front
(`Target.activateTarget`) makes the page really hidden, for testing the visibility handling.

## Layout

- `Program.cs`: single instance (mutex `Local\CastDesk.SingleInstance`), the installer's command-line options
  (`--exit` sets the event `Local\CastDesk.Exit` and waits for the mutex), WebView2 check, app ID.
- `CastDeskController.cs`: the receiver. Owns `Dial/SsdpAdvertiser` (discovery on UDP 1900) and
  `Dial/DialHttpServer` (device description, `GET/POST /apps/YouTube`, `DELETE /apps/YouTube/run`); events are posted
  to the UI thread.
- `Tv/TvAppView.cs`: WebView2 with the TV user agent and its own profile (`%LOCALAPPDATA%\CastDesk\WebView2`, holds the
  YouTube sign-in). Opens `youtube.com/tv?<phone's launch data>&additionalDataUrl=...`, and answers the TV app's
  screen-details POST inside WebView2 (`WebResourceRequested`) so the phone can read it from `GET /apps/YouTube`.
- `Tv/page-bridge.js` (embedded resource): media state for Windows' media controls, F11/Alt+Enter, the Back and
  Fullscreen overlay buttons. It also keeps the page "visible" while hidden (minimized or in the tray) on the
  `#/watch` page: the TV app unloads its video when the page is hidden. Elsewhere (home screen, search) it passes the
  real visibility on, so previews stop. It says no to impossible formats in `MediaSource.isTypeSupported`
  (`width=99999`): Chromium says yes to them, and the TV app then stops at 720p. Its media report includes
  `watching` and `music` (`getPlayerResponse().videoDetails.musicVideoType` on `#ytlr-player__player-container-player`;
  the TV app gets its player data through encrypted `initplayback`, so it can't be read from the network).
  It also counts connected phones, for `KeepPlayingWhenPhoneDisconnects` (on by default): "Disconnect from TV" sends
  no DIAL `DELETE`, and the phone keeps polling `GET /apps/YouTube`, so the signal is the lounge's `loungeStatus`
  message on `/api/lounge/bc/bind` (its `devices` JSON has one `REMOTE_CONTROL` per phone; recorded 2026-11-09).
  It also puts the Cast menu name into the TV app's lounge requests (`name`, `screen_name`, `deviceInfo.model`;
  `TvAppView.ScreenName` sets `window.__castDeskScreenName` in a separate document script): otherwise phones that
  join over the lounge (a second phone, Link with TV code) show "YouTube on TV" / "Sabrina", the TV identity's model.
- `MainForm.cs`: window, tray menu, fullscreen. The close button (`CloseToTray`) keeps the TV app loaded on a video
  page: music keeps playing, other videos are paused, and the window comes back when a non-music video plays again.
  Elsewhere it unloads the TV app (`HideToTray`, also used when casting stops) so nothing plays hidden. After 5
  minutes unloaded in the tray, `TvAppView.ReleaseBrowser` disposes the WebView2 control (and its processes) and puts
  a new one in its place; the next cast initializes it again. The user chose this to save memory.
- `Media/WindowsMediaControls.cs`, `Interop/` (`AudioSessionRenamer`, `AppIdentity`, `ProcessTree`, `WindowTheme`):
  make Windows call the sound and media controls "CastDesk" instead of WebView2 / Unknown app. The media controls
  only resolve the name through the installer's Start menu shortcut with `System.AppUserModel.ID` =
  `CastDesk.Receiver` (`ShortcutProperty` in `Package.wxs`); the HKCU registration alone wasn't enough, so dev
  builds still show "Unknown app". Keep the ID the same in both places.
- `Media/DiscordPresence.cs`: Rich Presence over the Discord app's local pipe (`\\.\pipe\discord-ipc-0`…`9`; frames
  of opcode, length, JSON; handshake, then `SET_ACTIVITY` with a nonce). Application ID `1569019291867816131`
  (Shyam's Discord application; the cover was uploaded there, the small icon is a GitHub raw URL to
  `docs/discord/discord-icon.png`, so keep that path). On by default (`ShowInDiscord`; the user chose this for 2.1.4).
  Discord allows ~5 updates per 20 s: keep the 4 s spacing and only send when the activity changes (the bridge
  keeps `startedAt` steady to 2 s). `SET_ACTIVITY` needs a nonzero `pid`, and Discord puts its game overlay on that
  process's window: send the WebView2 browser process (`TvAppView.BrowserProcessId`, windowless), never CastDesk's
  own ID (with it, the overlay appeared over the player; confirmed gone on the user's PC on 2026-11-08).
  Testing it changes the user's real Discord status: keep tests short and clear it afterwards.
- `Media/LockScreenArtwork.cs`: the thumbnail as the lock screen picture while playing (`ShowOnLockScreen`, on by
  default since 2.1.5, the user's choice). Two looks (`LockScreenFillsScreen`, Settings → Look; a change redraws the
  picture at once). The default, `ComposePicture`, puts the art in the centre at half the screen's width (60% of its
  height at most) on frosted, glossy glass tinted with the art's main colour (`MainColor`); the user asked for this
  over a blur of the art. `ComposeFilledPicture` is Niranjen99's original (fills the screen, or the art over a blur of
  itself), kept as an option at the user's request. The glass is computed per pixel because GDI+ gradient brushes
  left rings; its terms are precomputed per row and column (glows are separable Gaussians, the gloss is a lookup
  table, the grain is xorshift) and the shadow fills only the ring outside the art. That took it from 1,056 ms to
  87 ms at 1080p and 5,249 ms to 393 ms at 4K: keep per-pixel work out of the inner loop.
  `FindPictureArea` crops black bars, and flat bars of any colour when they come in a matching pair and leave a
  square (album tracks' `maxresdefault.jpg` puts the cover between dark gray bars; requiring a square keeps a plain
  backdrop around a centred subject). Art under 600 px on its shorter side is skipped when filling the screen (the
  user's request), under 340 px when centred, which also tries `sddefault` and `hqdefault` so old videos show.
  To check the look without touching the lock screen, call `ComposePicture` / `ComposeFilledPicture` by reflection
  from a throwaway console program on downloaded thumbnails.
  Set as playback starts, not on lock: `LockScreen.SetImageFileAsync` takes ~0.8 s, so setting it on lock showed
  Spotlight first. It needs no admin rights and turns off Spotlight (`RotatingLockScreenEnabled`), but Picture mode
  inherits Spotlight's "fun facts, tips" switch, which put "Learn more about this picture" links over the thumbnail
  (the user's report), so `HideTips` turns it off (`RotatingLockScreenOverlayEnabled` and
  `SubscribedContent-338387Enabled`, as Settings does). `LockScreen\backup.json` saves the picture and the switches
  first; restored 5 s after playback stops, or at the next
  start after a crash. Windows reads the Spotlight switch only as the lock screen starts: stopped while locked, the
  underlying picture shows until the next lock (the user chose that over keeping the stopped song's thumbnail).
  Tested on the user's PC 2026-11-09 for Spotlight and Picture (and `HideTips`: links gone, both values back
  afterwards); restoring a slideshow is untested. Testing changes
  the user's real lock screen: back up the registry keys and leave Spotlight as it was.
- `Configuration/` (`CastDeskOptions` = `%LOCALAPPDATA%\CastDesk\settings.json`, `DeviceIdentity`,
  `StartupRegistration`), `UI/SettingsForm.cs` (two columns, so it fits 1080p at 150%; keep it from growing taller),
  `Diagnostics/AppLog.cs` (starts a new log past 1 MB as it writes, not only at start: phones poll while connected,
  and CastDesk runs for weeks).
- `Updates/UpdateService.cs`: GitHub latest-release check, MSI download verified by size and the asset's SHA-256
  digest, `msiexec /passive`. Used by Settings → Updates and the automatic check 30 s after start (tray balloon).
- `installer/`: WiX 5 MSI (`Package.wxs`, `build-installer.ps1`, `make-installer-art.ps1`); `payload/` and `out/` are
  build output. `tools/readme-shots`: README images. `docs/screenshots`: the README's screenshots.

## Rules

- **Keep the device ID** (`%LOCALAPPDATA%\CastDesk\device-id.txt`). A new one makes phones list CastDesk twice.
- **YouTube's page enforces Trusted Types and blocks inline `<style>`**: build injected UI with `createElement` and
  `element.style`, never `innerHTML`. Key presses the TV app must obey (Back = Esc) go through CDP
  `Input.dispatchKeyEvent`; script-made key events are untrusted and ignored.
- **YouTube only.** No stream extraction (v1 used YoutubeExplode + LibVLC and hit YouTube's bot check). Netflix can't
  be cast to uncertified devices; Miracast receive needs a UWP `CoreApplicationView` (`StartAsync` gives E_INVALIDARG
  from Win32); AirPlay and Google Cast mirroring would need reverse-engineering. Don't re-propose these.
- **The DIAL server refuses web pages** (`DialHttpServer.RefusalReason`): an `Origin` that isn't YouTube's (or an
  app's `package:` / `ios-app:`) gets 403, as does a `Host` that isn't an IP address or localhost (DNS rebinding).
  There are no CORS headers. The `[dial]` log lines show each request's `Origin`; check a real cast still works after
  changing this.
- **Catch everything on timer and pool threads.** An exception there ends CastDesk (WinForms only catches the UI
  thread). `AudioSessionRenamer` (every 3 s), the SSDP announcements and network-change handler, the `--exit` wait,
  and the media-key callback all catch `Exception` and log it; `Marshal.ThrowExceptionForHR` and adapter listing
  throw more types than you'd expect. Background workers (`DiscordPresence`, `LockScreenArtwork`) do the same.
- **Settings stay loadable:** older `settings.json` files must still load. Add new settings with defaults.

## Testing on the user's PC

- **Don't play audio:** opening the TV app plays previews and videos out loud. Mute through CDP or don't open it, and
  never cast test videos without asking.
- **Don't steal focus without need:** the user may be gaming. Put CastDesk back the way it was (window or tray).
- **Back up `%LOCALAPPDATA%\CastDesk\settings.json` before tests that change settings**, and restore it after.
- Don't delete the WebView2 profile: it signs the user out of YouTube.
- **The user runs the installed copy** (`C:\Program Files\CastDesk`, kept on the latest release, started at
  sign-in). Only one copy can run (single-instance mutex), so to try a dev build: note whether the installed copy is
  running, `"C:\Program Files\CastDesk\CastDesk.exe" --exit`, test the build, `--exit` it, then start the installed copy
  again with `explorer.exe "C:\Program Files\CastDesk\CastDesk.exe"` (not elevated). Both use the same
  `%LOCALAPPDATA%\CastDesk` data. A dev build shows "Unknown app" in the media controls and gets its own firewall
  prompt (which takes focus), so prefer testing through the installed copy where you can.

## Releases

1. Bump `<Version>` in `CastDesk.csproj`; update the README and retake screenshots if the UI changed.
2. Commit and push, then build the installer (after the commit, so the exe's version carries the right commit hash).
3. Create a GitHub release `v<version>` (title "CastDesk <version>", `--target main`) with
   `CastDesk-<version>-x64.msi` attached. Notes: what changed in plain words, then an **Install:** line, as in v2.1.1.
4. Check the asset's `digest` matches `sha256sum` of the local MSI, and rebuild the dev copy so it isn't older than the
   release (or its own updater offers the release).
5. The user updates the installed copy through **Settings → Updates**; the log's `[update]` lines and the
   `CastDesk starting` line after them show it worked.

Releases: v2.0.0 (first, no updater), v2.1.0 (updater), v2.1.1 (shortcut app ID, so the media controls say
"CastDesk"), v2.1.2 (PR #1 from Niranjen99: keeps playing while minimized, qualities above 720p), v2.1.3 (music keeps
playing in the tray, other videos pause and reopen the window; previews stop while minimized), v2.1.4 (Discord
status, on by default), v2.1.5 (PR #2 from Niranjen99: lock screen thumbnail, on by default with two looks, and the
phone-disconnect setting; the DIAL server refuses web pages; WebView2 closed after 5 minutes in the tray; crash and
log fixes from a full review), v2.1.6 (Spotlight's "Learn more about this picture" links hidden over the lock screen
thumbnail; README pictures of both looks). The updater
worked end to end on the user's PC for 2.1.0 → 2.1.1 (2026-11-07): download and SHA-256 check, `msiexec /passive`,
CastDesk back up 7 s later.

Outside contributions come as pull requests (PR #1 was the first). Review the diff, merge with a merge commit so the
contributor's commit keeps their name, then release as above and thank them in the notes.

The installer (`installer/Package.wxs`) is per-machine: Program Files by default, a folder page to choose another,
the folder kept for upgrades (`HKLM\Software\CastDesk\Installer\InstallFolder`), a firewall rule (local subnet), the
Start menu shortcut with CastDesk's app ID, and CastDesk started through Explorer so it doesn't run elevated. Keep
the `UpgradeCode`. The in-app updater reads the latest (non-draft, non-prerelease) release and needs its `.msi`
asset; the installer won't downgrade, so never publish a lower version than the latest. To test the updater without
a new release, build a copy with `-p:Version=1.9.0` and call `UpdateService` from it (don't run the installer).
Don't run an MSI on the user's PC yourself unless asked (admin rights; it replaces the copy they use). Commit and
publish only when the user asks.

## Docs

- README screenshots are in `docs/screenshots`: retake them with `tools/readme-shots` when the UI changes. The player
  window isn't screenshotted (opening the TV app plays sound). The lock screen pictures come from `ComposePicture` /
  `ComposeFilledPicture` on a cover the tool draws itself: keep real thumbnails (copyrighted) out of the repo.
- Write user-facing text and docs in plain, specific English.
- `PRIVACY.md` and `TERMS.md` are linked from CastDesk's Discord application (Developer Portal). Keep their paths, and
  update `PRIVACY.md` (and its date) whenever CastDesk starts connecting to something new or storing something new.
- In this environment, write commit messages to a file and use `git commit -F` (Windows PowerShell adds a BOM
  otherwise). Python one-liners and heredocs have turned `\b`, `\r` and `\n` in paths into control characters here:
  edit text containing backslashes with the Edit tool, and grep for control characters after scripted edits.
