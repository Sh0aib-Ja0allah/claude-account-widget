# Claude Account Widget

A small Windows desktop widget that shows which account Claude Code is signed in to and how much of your plan's usage limit you've used.

![The full widget and small icon in dark and light themes, plus the right-click menu with its ring color options, showing a sample account at 52% of the 7-day limit](docs/widget.png)

> **Unofficial project.** This isn't affiliated with or endorsed by Anthropic. Claude and the Claude logo are Anthropic's trademarks.

## Features

- **Account:** shows the signed-in email and plan name, with an optional warning if the wrong account is signed in.
- **Usage:** shows a 7-day ring with the percentage used and reset time, plus a 5-hour bar when your plan reports one.
- **Pace:** shows how your usage compares with the time passed in the 7-day window.
- **Small icon:** minimizes to a pill that keeps the percentage visible.
- **Appearance:** follows the Windows light or dark theme, with a choice of ring colors.
- **Alert colors:** turns amber at 70% and red at 90%.

## Requirements

- Windows 10 or 11, with .NET Framework 4.6 or later (included with Windows)
- Claude Code, signed in with a Claude subscription
- Node.js, only for the optional status line

## Install

**Download:** get `ClaudeAccountWidget.exe` from the [Releases page](https://github.com/Sh0aib-Ja0allah/claude-account-widget/releases), put it in its own folder, and run it. The file isn't digitally signed, so Windows SmartScreen or your antivirus may warn you.

**Build from source:** this lets you review the code first. Download the source, then run this from the repo folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

This compiles the widget with the C# compiler included in Windows and installs it to `%LOCALAPPDATA%\ClaudeAccountWidget`. Start it with:

```powershell
& "$env:LOCALAPPDATA\ClaudeAccountWidget\ClaudeAccountWidget.exe"
```

The widget keeps its files in the folder it runs from, so the optional settings and uninstall steps below use that folder if you downloaded the file.

## Using it

- **Move it:** drag the widget or the small icon.
- **Minimize and reopen:** click the eye icon, then click the small icon.
- **Refresh:** click the refresh icon. It checks at most once every 30 seconds.
- **More options:** right-click for Always on top, Start with Windows, Ring color, Refresh now and Exit.

## Optional settings

**Wrong-account warning:** create `expected-account.txt` in the install folder, containing only the email you expect. The widget shows a warning if a different account is signed in. Without this file, it doesn't warn about any account.

**Status line:** to also show usage in the Claude Code terminal footer, add this to `~/.claude/settings.json`, using your own Windows username:

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

The token is only sent to that Anthropic address. It isn't saved anywhere else or renewed by the widget. If it expires, usage checks wait until Claude Code renews it.

The widget saves its usage figures, your email and its settings in its install folder.

## Limitations

- **Undocumented usage address:** Anthropic doesn't document this address, so it could change or stop working without warning. The widget would then show a warning and keep the last figures. Review the code and decide whether you're comfortable with this before using it.
- **Windows only:** it's built with WPF.

## Uninstall

1. Right-click the widget, turn off **Start with Windows** if it's on, and choose **Exit**.
2. Delete `%LOCALAPPDATA%\ClaudeAccountWidget`.
3. Remove the `statusLine` setting if you added it.

## License

[MIT](LICENSE), copyright Shoaib Jadallah.
