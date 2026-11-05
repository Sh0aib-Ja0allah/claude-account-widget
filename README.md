# Claude Account Widget

A small Windows desktop widget that shows which account Claude Code is signed in to and how much of your plan's usage limit you've used.

![The full widget and small icon in dark and light themes, plus the right-click menu with its ring color options, showing a sample account at 52% of the 7-day limit](docs/widget.png)

> **Unofficial project.** This isn't affiliated with or endorsed by Anthropic. Claude and the Claude logo are Anthropic's trademarks.

This is an early pre-release, so expect some rough edges.

## Features

- **Account:** shows the signed-in email and plan name, with an optional warning if the wrong account is signed in.
- **Usage:** shows a 7-day ring with the percentage used and reset time. A 5-hour bar appears only when a 5-hour figure is reported.
- **Pace:** shows how your usage compares with the time passed in the 7-day window.
- **Small icon:** minimizes to a pill that keeps the percentage visible.
- **Appearance:** follows the Windows light or dark theme, with six ring colors.
- **Alert colors:** turns amber at 70% and red at 90%, whichever ring color you choose.

## Requirements

- Windows 10 or 11, with .NET Framework 4.6 or later (included with Windows)
- Claude Code, signed in with a Claude subscription
- Node.js, only for the optional status line

## Install

### Download

1. Get `ClaudeAccountWidget.exe` from the [latest release](https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases).
2. Put it in its own folder, because the widget saves its files next to the app.
3. Run it.

The file isn't digitally signed, so Windows SmartScreen or your antivirus may warn you. The release notes list its SHA-256 hash if you want to check your download.

### Build from source

Building lets you review the code first, and it also installs the optional status line script. Download the source, then run this from the repo folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

This compiles the widget with the C# compiler included in Windows and installs it to `%LOCALAPPDATA%\ClaudeAccountWidget`. Start it with:

```powershell
& "$env:LOCALAPPDATA\ClaudeAccountWidget\ClaudeAccountWidget.exe"
```

## Using it

| To do this | Do this |
|---|---|
| Move it | Drag the widget or the small icon |
| Minimize it | Click the eye icon |
| Reopen it | Click the small icon |
| Check usage now | Click the refresh icon (at most once every 30 seconds) |
| See more options | Right-click for Always on top, Start with Windows, Ring color, Refresh now and Exit |

Always on top starts on, and Start with Windows starts off.

### What pace means

The tick on the ring marks how much of the 7-day window has passed. The label compares that with your usage:

| Label | Meaning |
|---|---|
| **Below pace** | Your usage is more than 5 points behind the time passed |
| **On pace** | Your usage is within 5 points of the time passed |
| **Above pace** | Your usage is more than 5 points ahead of the time passed |

The widget assumes the window started exactly 7 days before it resets.

## Optional settings

Both settings use the folder where the widget is installed.

**Wrong-account warning:** create `expected-account.txt` containing only the email you expect, then restart the widget. It shows a warning if a different account is signed in. Without this file, it doesn't warn about any account.

**Status line:** this shows usage in the Claude Code terminal footer. It needs Node.js and `statusline.js`, which the source build installs. Add this to `~/.claude/settings.json`, using your own Windows username:

```json
{
  "statusLine": {
    "type": "command",
    "command": "node C:/Users/YOUR_USERNAME/AppData/Local/ClaudeAccountWidget/statusline.js"
  }
}
```

## What it reads and sends

| Item | Use |
|---|---|
| `~/.claude.json` | Reads the signed-in email and plan name |
| `~/.claude/.credentials.json` | Reads the plan name and your Claude login token, without changing the file |
| `https://api.anthropic.com/api/oauth/usage` | Receives the token every 5 minutes to get your usage |

The token is only sent to that Anthropic address. The widget doesn't save it anywhere else or renew it.

The widget saves its usage figures, your email and its settings in its install folder.

## Troubleshooting

| What you see | What it means |
|---|---|
| Waiting for Claude Code to renew its login. | The login token has expired or was rejected. Checks resume after Claude Code renews it, which may need a Claude Code session. |
| Usage checks are paused for a while. | Anthropic asked the widget to slow down. Checks restart automatically. |
| Couldn't check usage. | The check failed, so the widget keeps the last figures and tries again later. |
| No 5-hour row | No 5-hour figure was reported for your account. |

## Limitations

- **Undocumented usage address:** Anthropic doesn't document this address, so it could change or stop working without warning. The widget would then show a warning and keep the last figures. Review the code and decide whether you're comfortable with this before using it.
- **Windows only:** it's built with WPF.

## Uninstall

1. Right-click the widget, turn off **Start with Windows** if it's on, and choose **Exit**.
2. Delete the widget's folder. For a source build, that's `%LOCALAPPDATA%\ClaudeAccountWidget`.
3. Remove the `statusLine` setting if you added it.

## License

Released under the [MIT License](LICENSE). Copyright © 2026 Shoaib Jadallah.

You can use, change and share this software, including commercially, as long as you keep the copyright and license notice. It comes with no warranty.

The license covers this project's own code. It doesn't grant any rights to Anthropic's trademarks, including the Claude name and logo.
