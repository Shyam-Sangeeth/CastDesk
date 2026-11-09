# tools

Helper scripts for working on CastDesk. They aren't part of the app (the app project excludes this folder).

| Tool | What it does |
|---|---|
| `readme-shots/` | Renders the README images off-screen: `docs/screenshots/settings.png` (the Settings window, with default settings), `docs/screenshots/lock-screen-centred.jpg` and `lock-screen-filled.jpg` (the two lock screen looks, drawn by CastDesk's own code from a made-up album cover, with a clock like Windows'), and `Assets/logo-64.png` (from the app icon). Nothing appears on screen, your settings file isn't read and your lock screen isn't changed. |
| `make-discord-art.ps1` | Draws CastDesk's Discord application art in the installer's style: `docs/discord/discord-cover.png` (512 x 288, the cover image in the Developer Portal) and `docs/discord/discord-icon.png` (1024 x 1024, the app icon; Rich Presence also shows it as the small image, loaded from GitHub). Run with `powershell -ExecutionPolicy Bypass -File tools\make-discord-art.ps1`. |

Retake the screenshots after changing the UI:

```powershell
dotnet build -c Debug                      # from the repo root: builds CastDesk.dll, which the tool loads
dotnet run --project tools\readme-shots
```
