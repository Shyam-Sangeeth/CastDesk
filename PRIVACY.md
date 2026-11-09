# CastDesk privacy policy

Last updated: 9 November 2026 (thumbnail on the lock screen)

CastDesk is a Windows app that lets the YouTube app on your phone cast to your PC. It has no servers, no accounts and
no analytics: CastDesk itself collects nothing and sends nothing about you to its developer.

## What stays on your PC

CastDesk keeps these in `%LOCALAPPDATA%\CastDesk` on your PC, and never uploads them:

- **Settings** (`settings.json`): the receiver name, window options and other choices in Settings.
- **Device ID** (`device-id.txt`): a random ID that your phone uses to recognise CastDesk on your network.
- **Log** (`castdesk.log`): what CastDesk did, including requests from phones on your network (with their local IP
  addresses) and the results of update checks. It is for troubleshooting and is never sent anywhere.
- **The TV app's browser profile** (`WebView2`): YouTube's sign-in and cookies, as in any browser.
- **Lock screen files** (`LockScreen`), only while **Show the thumbnail on the lock screen** is showing one: the
  thumbnail, a copy of your lock screen picture and your lock screen settings, removed when your lock screen is put
  back. That thumbnail is visible to anyone who can see your locked screen.

To remove all of it, uninstall CastDesk and delete that folder.

## What CastDesk connects to

- **Your phone, on your local network.** CastDesk answers casting requests from phones on the same network (SSDP and
  DIAL). These stay on your network.
- **YouTube.** CastDesk shows YouTube's own TV app (youtube.com/tv). What you watch, your sign-in and your casting
  session are handled by YouTube, under [Google's Privacy Policy](https://policies.google.com/privacy). With
  **Show the thumbnail on the lock screen** on (it is off by default), CastDesk also downloads each video's thumbnail
  from YouTube's image server (`i.ytimg.com`) or the address the TV app gives for it.
- **GitHub, to check for updates.** Shortly after it starts (unless you turn this off in Settings), and when you click
  **Check for updates**, CastDesk asks `api.github.com` for the latest release, and downloads the installer from
  GitHub if you choose to update. GitHub sees your IP address, as with any web request, under
  [GitHub's Privacy Statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement).
- **Discord, while the option is on.** With **Show what I'm playing in Discord** on (it is on by default; turn it
  off in Settings), and only while the Discord desktop app is running on your PC, CastDesk
  passes the title, channel, thumbnail link, playback time and video link of what's playing to the Discord app on
  your PC, which shows it as your status. Your Discord friends, and others who can see your profile, can see that
  status. Discord handles it under [Discord's Privacy Policy](https://discord.com/privacy). Turning the option off,
  pausing or closing CastDesk clears the status. Discord loads CastDesk's small icon from GitHub.

## Children

CastDesk doesn't collect personal information from anyone, including children.

## Changes

If this policy changes, the new version will be published here, with the date above updated.

## Contact

Questions: open an issue at [github.com/Shyam-Sangeeth/CastDesk/issues](https://github.com/Shyam-Sangeeth/CastDesk/issues).
