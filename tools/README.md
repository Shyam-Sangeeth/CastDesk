# tools

Helper scripts for working on CastDesk. They aren't part of the app (the app project excludes this folder).

| Tool | What it does |
|---|---|
| `readme-shots/` | Renders the README images off-screen: `docs/screenshots/settings.png` (the Settings window, with default settings), `docs/screenshots/lock-screen-centred.jpg` and `lock-screen-filled.jpg` (the two lock screen looks, drawn by CastDesk's own code from a made-up album cover, with a clock like Windows'), and `Assets/logo-64.png` (from the app icon). Nothing appears on screen, your settings file isn't read and your lock screen isn't changed. |
| `make-discord-art.ps1` | Draws CastDesk's Discord application art in the installer's style: `docs/discord/discord-cover.png` (512 x 288, the cover image in the Developer Portal) and `docs/discord/discord-icon.png` (1024 x 1024, the app icon; Rich Presence also shows it as the small image, loaded from GitHub). Run with `powershell -ExecutionPolicy Bypass -File tools\make-discord-art.ps1`. |
| `check-bridge.js` | Checks `Tv/page-bridge.js` against the live TV app without CastDesk's window, sound or your sign-in: opens youtube.com/tv in a headless, muted Edge with a temporary profile (deleted afterwards), with CastDesk's TV identity (read from `CastDeskOptions.cs`), a stand-in for WebView2 and the bridge. It prints the name and `deviceInfo` the TV app sends on YouTube's connection (`/api/lounge/bc/bind`), YouTube's status for each, and the bridge's messages to CastDesk. Run with `node tools\check-bridge.js [--name CastDesk-TEST] [--video <id>] [--no-bridge] [--seconds 25] [--port 9345]`. With the bridge, `name` and `deviceInfo.model` should be the `--name` value with status 200; `--no-bridge` shows the TV app's own "YouTube on TV" / "Sabrina". Needs Node 22 or later and Edge. |

Retake the screenshots after changing the UI:

```powershell
dotnet build -c Debug                      # from the repo root: builds CastDesk.dll, which the tool loads
dotnet run --project tools\readme-shots
```
