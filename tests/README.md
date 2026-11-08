# Tests

These tests compile the widget's own source with the C# compiler that comes with Windows, then drive it from small test programs. They need no project file, NuGet package or extra install for the main checks.

They're built to be safe to run on your own PC:

- Every window opens off screen and never takes focus.
- Every server is a fake one on `127.0.0.1`, on ports picked when the run starts. Nothing is sent to Anthropic or GitHub, and no real Claude request is made.
- Nothing touches your installed widget, your Claude Code sign-in files or your files. All the work happens in a scratch folder outside the repo.
- The app is only ever built with `build.ps1 -OutDir <scratch folder>`. Without `-OutDir`, `build.ps1` installs over the real widget, so the test script never calls it that way.

## Running them

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\run-tests.ps1                 # quick checks
powershell -ExecutionPolicy Bypass -File .\tests\run-tests.ps1 -Mode all       # everything
powershell -ExecutionPolicy Bypass -File .\tests\run-tests.ps1 -Mode chat -KeepScratch
```

| Mode | What it runs | Needs |
|---|---|---|
| `quick` (default) | The safety lint, then the feature checks that run in one process: h6 to h12 and h14 to h16 | .NET Framework 4.8, which Windows 10 and 11 include |
| `chat` | h3, the chat window: formatted replies, live replies, regenerate, edit, search, and a crash check. Each part runs in its own process with its own history folder | The same |
| `widget` | h4, the widget and its features, including runs of the built app in test mode. Also hk and h13 (stopping a chat's processes) and h5 (the status line) | The same. h5 needs Node.js and is skipped without it |
| `preview` | h2, the file preview. The sample files are generated when it runs | Python with `reportlab`, `python-pptx`, `openpyxl` and `Pillow`, and LibreOffice for the Office pages. Skipped without them |
| `all` | quick, chat, widget and preview | All of the above |
| `png` | Renders `docs/widget.png` from sample data into the scratch folder. Copy it to `docs/` by hand if it looks right | .NET Framework 4.8 |

Options:

- `-Scratch <folder>`: where the run works. The default is `C:\cawt\run-<date and time>`. It must be short (60 characters at most), because LibreOffice fails with long temp paths. It can't be inside the repo, the installed widget's folder or your `.claude` folder.
- `-TimeoutSeconds <n>`: how long each test program may run (default 900).
- `-KeepScratch`: keep the scratch folder after a passing run. It's always kept after a failure.

## Reading the results

The script ends with a table of each suite's PASS and FAIL counts, any skip reason, and the time taken. It prints `PASSED`, `INCOMPLETE` (something was skipped, for example because LibreOffice isn't installed) or `FAILED`, and exits with 1 on any failure.

A suite fails if any of its checks fail, if it times out, if it prints an `UNHANDLED`, `HARNESS ERROR` or `SECTION ERROR` line, or if it stops without reporting any results. A skip is never counted as a pass. Each suite's full output is in `<scratch>\out\<suite>\log.txt`, with screenshots beside it.

## The safety lint

`lint.ps1` runs first in `quick`, and on its own with `powershell -File .\tests\lint.ps1`. It fails if any test:

- points a request, the widget or a started process at an address other than `127.0.0.1`;
- contains personal data: a `C:\Users\<name>` path, an API key or bearer token, a refresh token, an email outside the reserved example domains, or the owner's names and account words. Escaped characters and look-alike letters (Cyrillic, Greek, fullwidth) are normalised first, so `\u0430` for "a" doesn't hide a name. The owner's words are stored only as hashes;
- starts the widget with `live`. That argument shows real Windows notices and registers a real global shortcut, so it's only for hand testing and stays out of the repo.

## Layout

```
tests/
  run-tests.ps1     the entry point
  lint.ps1          the safety lint
  references.rsp    the compiler's reference list
  fakes/            fake-claude.cs (stands in for the claude command), fake-server.cs (usage, profile and releases),
                    spawner.cs (a process tree for hk), TestPorts.cs (the run's free ports)
  fixtures/         make-samples.py (generates the preview samples)
  suites/preview    h2.cs
  suites/chat       h3.cs
  suites/widget     h4*.cs, one test program in several files
  suites/features   h5.cs to h16.cs and hk.cs, each its own program
```

The `h` numbers come from the order the tests were written, and are kept for now.

## Writing a test

- Open windows in test mode: `new WidgetWindow(folder, accountFile, ..., testMode: true)`, with a `settings.txt` that puts the widget at `-6000,0`.
- Use fake files only: a scratch account file, fake credentials with an obvious marker token, and the fake server.
- Get server ports from `TestPorts.Get(<port>)` or `TestPorts.Base(<port>)`, never a fixed number.
- Print one line per check, starting with `PASS ` or `FAIL `, and end with a summary line such as `passes=12 fails=0`.
- Run `run-tests.ps1` before committing. The lint must pass too.
