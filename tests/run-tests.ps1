# Runs the widget's test suites. Every window opens off screen, every server is a fake on 127.0.0.1, and nothing touches
# the installed widget, the Claude Code sign-in files or the repo: all work happens in a short scratch folder.
#
#   run-tests.ps1 [-Mode quick|chat|widget|preview|all|png] [-Scratch <path>] [-TimeoutSeconds <n>] [-KeepScratch]
#
# quick    the safety lint, then the in-process feature suites in suites\features  needs: nothing but .NET Framework 4.8
# chat     chat-window: format, live, regen, edit, search and crash, each in its own process
# widget   the widget suite (every section), process-tree and kill-tree (real child processes), shared-pieces (needs node)  builds the app into scratch
# preview  file-preview                          needs: Python with reportlab, python-pptx, openpyxl, Pillow; LibreOffice
# all      quick, chat, widget and preview
# png      renders docs/widget.png from sample data into scratch (copying it to docs/ is a manual step)
param(
    [ValidateSet('quick', 'chat', 'widget', 'preview', 'all', 'png')][string]$Mode = 'quick',
    [string]$Scratch = '',
    [int]$TimeoutSeconds = 900,
    [switch]$KeepScratch
)
$ErrorActionPreference = 'Stop'
$tests = $PSScriptRoot
$repo = Split-Path $tests -Parent
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wm = "$env:WINDIR\System32\WinMetadata"
$csc = "$fw\csc.exe"
if (-not (Test-Path $csc)) { throw "The C# compiler wasn't found at $csc. The tests need .NET Framework 4.8." }

# ---------- scratch folder and its guards ----------
if ($Scratch -eq '') { $Scratch = Join-Path "$env:SystemDrive\" ('cawt\run-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$Scratch = [IO.Path]::GetFullPath($Scratch).TrimEnd('\')
function Test-Inside([string]$path, [string]$folder) {
    if (-not $folder) { return $false }
    $f = [IO.Path]::GetFullPath($folder).TrimEnd('\') + '\'
    return ($path + '\').StartsWith($f, [StringComparison]::OrdinalIgnoreCase)
}
if (Test-Inside $Scratch $repo) { throw "The scratch folder can't be inside the repo: $Scratch" }
if (Test-Inside $Scratch (Join-Path $env:LOCALAPPDATA 'ClaudeAccountWidget')) { throw "The scratch folder can't be inside the installed widget's folder: $Scratch" }
if (Test-Inside $Scratch (Join-Path $env:USERPROFILE '.claude')) { throw "The scratch folder can't be inside the Claude Code folder: $Scratch" }
if ($Scratch.Length -gt 60) { throw "The scratch folder path is too long ($($Scratch.Length) characters, 60 at most). LibreOffice fails with long TEMP paths." }
if ($Scratch.Length -gt 40) { Write-Warning "The scratch folder path is $($Scratch.Length) characters long. Under 40 is safest for LibreOffice." }
if ((Test-Path $Scratch) -and (Get-ChildItem $Scratch -Force | Select-Object -First 1)) { throw "The scratch folder already exists and isn't empty: $Scratch" }

$snapshot = Join-Path $Scratch 'snapshot'   # the widget suite's test-copy-windows section reads <scratch>\snapshot\widget.cs (two levels above its out folder)
$bin = Join-Path $Scratch 'bin'
$out = Join-Path $Scratch 'out'
$temp = Join-Path $Scratch 't'
New-Item -ItemType Directory -Force $snapshot, $bin, $out, $temp | Out-Null

# The source is copied once, so an edit during the run can't mix versions.
foreach ($f in 'widget.cs', 'preview.cs', 'statusline.js') { Copy-Item (Join-Path $repo $f) $snapshot }
$widgetMd5 = (Get-FileHash (Join-Path $snapshot 'widget.cs') -Algorithm MD5).Hash.ToLower()
$previewMd5 = (Get-FileHash (Join-Path $snapshot 'preview.cs') -Algorithm MD5).Hash.ToLower()

$savedTemp = $env:TEMP; $savedTmp = $env:TMP
$env:TEMP = $temp; $env:TMP = $temp

# Free loopback ports for the fake servers, held together while picked so they are all different. Each suite reads its port
# from CAWT_PORT_<the port it was written with> (tests\fakes\TestPorts.cs).
$writtenPorts = 18765, 18767, 18769, 18771, 18773, 18774, 18775, 18777, 18779
$held = @()
foreach ($w in $writtenPorts) {
    $l = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0); $l.Start(); $held += $l
    [Environment]::SetEnvironmentVariable("CAWT_PORT_$w", [string]$l.LocalEndpoint.Port, 'Process')
}
foreach ($l in $held) { $l.Stop() }
$portText = ($writtenPorts | ForEach-Object { [Environment]::GetEnvironmentVariable("CAWT_PORT_$_", 'Process') }) -join ','

$results = New-Object System.Collections.Generic.List[object]
function Add-Result([string]$suite, [int]$pass, [int]$fail, [string]$skip, [double]$seconds, [string]$note) {
    $results.Add([pscustomobject]@{ Suite = $suite; Pass = $pass; Fail = $fail; Skip = $skip; Seconds = [math]::Round($seconds, 0); Note = $note })
}

function Invoke-Csc([string]$exe, [string]$main, [string[]]$files) {
    $cscArgs = @('/nologo', '/nowarn:1591,4014,0219,0649,0414,0169,0168,0618', '/target:exe', "/lib:$fw,$fw\WPF,$wm", "@$tests\references.rsp", "/out:$exe")
    if ($main) { $cscArgs += "/main:$main" }
    $text = & $csc @cscArgs @files (Join-Path $tests 'fakes\TestPorts.cs') 2>&1 | Where-Object { "$_" -notmatch 'warning CS' }
    if ($LASTEXITCODE -ne 0) { return ($text | Select-Object -First 5) -join ' ' }
    return $null
}
function Invoke-Csc-Plain([string]$exe, [string]$file) {
    $text = & $csc /nologo "/out:$exe" $file 2>&1
    if ($LASTEXITCODE -ne 0) { return ($text | Select-Object -First 5) -join ' ' }
    return $null
}

# Runs one test program, then counts its PASS and FAIL lines. Any UNHANDLED, HARNESS ERROR or SECTION ERROR line is a failure.
function Invoke-Suite([string]$suite, [string]$exe, [string[]]$arguments, [switch]$Survive, [switch]$NoSummary) {
    $dir = Join-Path $out $suite
    New-Item -ItemType Directory -Force $dir | Out-Null
    $log = Join-Path $dir 'log.txt'; $err = Join-Path $dir 'err.txt'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    if ($arguments -and $arguments.Count -gt 0) {
        $p = Start-Process $exe -ArgumentList $arguments -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError $err
    } else {
        $p = Start-Process $exe -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError $err
    }
    $finished = $p.WaitForExit($TimeoutSeconds * 1000)
    if (-not $finished) { try { $p.Kill() } catch { } }
    $watch.Stop()
    $lines = if (Test-Path $log) { Get-Content $log -Encoding UTF8 } else { @() }
    $pass = @($lines | Where-Object { $_ -like 'PASS *' }).Count
    $fail = @($lines | Where-Object { $_ -like 'FAIL *' }).Count
    $errors = @($lines | Where-Object { $_ -match '^(UNHANDLED|HARNESS ERROR|SECTION ERROR)' }).Count
    $note = ''
    if (-not $finished) { $fail++; $note = "timed out after $TimeoutSeconds s" }
    if ($errors -gt 0) { $fail += $errors; $note = "$errors error line(s), see $log" }
    # A suite that crashed before reporting has no results. That is a failure, never a pass. C# suites end with a summary line.
    if (-not $Survive -and $finished) {
        $summary = @($lines | Where-Object { $_ -match '^(passes=\d+ )?fails=\d+|^passes=\d+' }).Count
        if ($pass + $fail -eq 0) { $fail++; $note = "no results: the suite stopped before reporting, see $err" }
        elseif (-not $NoSummary -and $summary -eq 0) { $fail++; $note = "no summary line: the suite stopped part way, see $err" }
    }
    if ($Survive) {
        if (@($lines | Where-Object { $_ -eq 'SURVIVED' }).Count -gt 0) { $pass++; $note = 'survived' } else { $fail++; $note = 'did not survive' }
    }
    Add-Result $suite $pass $fail '' $watch.Elapsed.TotalSeconds $note
}

$src = @((Join-Path $snapshot 'widget.cs'), (Join-Path $snapshot 'preview.cs'))
$features = Join-Path $tests 'suites\features'
$widgetSuite = Join-Path $tests 'suites\widget'
$widgetFiles = 'widget-main.cs', 'settings-window.cs', 'usage-alerts.cs', 'limit-forecast.cs', 'review-fixes-s2.cs', 'history-recording.cs', 'history-chart.cs', 'review-fixes-s4.cs', 'chat-shortcut.cs', 'review-fixes-s5.cs', 'update-notice.cs', 'review-fixes-s6.cs', 'screenshot.cs', 'review-fixes-s7.cs', 'strict-release-links.cs', 'account-profile.cs', 'test-copy-windows.cs', 'settings-layout.cs', 'chat-width.cs', 'settings-switches.cs', 'card-notes.cs', 'sidebar-account.cs' | ForEach-Object { Join-Path $widgetSuite $_ }

# Each feature suite is its own program; $main is its entry class (the classes kept their original H-names).
function Invoke-Feature([string]$name, [string]$main, [string[]]$arguments) {
    $exe = Join-Path $bin "$name.exe"
    $problem = Invoke-Csc $exe $main ($src + (Join-Path $features "$name.cs"))
    if ($problem) { Add-Result $name 0 1 '' 0 "build failed: $problem"; return }
    Invoke-Suite $name $exe $arguments
}

function Build-App {
    # build.ps1 without -OutDir installs over the real widget, so it's only ever called with a scratch folder.
    $app = Join-Path $Scratch 'app'
    if (-not (Test-Inside $app $Scratch)) { throw "Refusing to build outside the scratch folder." }
    $copy = Join-Path $Scratch 'buildsrc'
    New-Item -ItemType Directory -Force $copy | Out-Null
    foreach ($f in 'build.ps1', 'claude-spark.ico', 'statusline.js') { Copy-Item (Join-Path $repo $f) $copy }
    Copy-Item $src $copy
    $text = & (Join-Path $copy 'build.ps1') -OutDir $app 2>&1 | Where-Object { "$_" -notmatch 'warning' }
    $exe = Join-Path $app 'ClaudeAccountWidget.exe'
    if (-not (Test-Path $exe)) { throw "The app build failed: $text" }
    return $exe
}

function Run-Quick {
    Invoke-Suite 'lint' "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $tests 'lint.ps1'), '-Root', $tests) -NoSummary
    $quick = [ordered]@{ 'settings-window' = 'H6'; 'usage-alerts' = 'H7'; 'limit-forecast' = 'H8'; 'history-recording' = 'H9'; 'history-chart' = 'H10'; 'chat-shortcut' = 'H11'; 'update-notice' = 'H12'; 'account-profile' = 'H14'; 'account-tiers' = 'H15'; 'sidebar-account' = 'H16'; 'saved-instructions' = 'H17' }
    foreach ($n in $quick.Keys) { Invoke-Feature $n $quick[$n] @((Join-Path $out "$n\run")) }
}

function Run-Chat {
    $fake = Join-Path $bin 'fake'
    New-Item -ItemType Directory -Force $fake | Out-Null
    $problem = Invoke-Csc-Plain (Join-Path $fake 'fake.exe') (Join-Path $tests 'fakes\fake-claude.cs')
    if ($problem) { Add-Result 'chat' 0 1 '' 0 "fake claude build failed: $problem"; return }
    "@`"$fake\fake.exe`" %*`r`n@exit /b %ERRORLEVEL%" | Set-Content -Encoding ascii (Join-Path $fake 'claude.cmd')
    $exe = Join-Path $bin 'chat-window.exe'
    $problem = Invoke-Csc $exe 'H3' ($src + (Join-Path $tests 'suites\chat\chat-window.cs'))
    if ($problem) { Add-Result 'chat' 0 1 '' 0 "build failed: $problem"; return }
    # Each mode gets its own process and its own history folder (one shared folder makes search fail).
    foreach ($m in 'format', 'live', 'regen', 'edit', 'search') { Invoke-Suite "chat-$m" $exe @($m, (Join-Path $out "chat-$m\run"), $fake) }
    Invoke-Suite 'chat-crash' $exe @('crash', (Join-Path $out 'chat-crash\run'), $fake) -Survive
}

function Run-Widget {
    $app = Build-App
    $server = Join-Path $bin 'fake-server.exe'
    $problem = Invoke-Csc-Plain $server (Join-Path $tests 'fakes\fake-server.cs')
    if ($problem) { Add-Result 'widget' 0 1 '' 0 "fake server build failed: $problem"; return }
    $exe = Join-Path $bin 'widget-main.exe'
    $problem = Invoke-Csc $exe 'H4' ($src + $widgetFiles)
    if ($problem) { Add-Result 'widget' 0 1 '' 0 "build failed: $problem" } else { Invoke-Suite 'widget' $exe @('all', (Join-Path $out 'widget'), $app, $server) }

    $spawner = Join-Path $bin 'spawner.exe'
    $problem = Invoke-Csc-Plain $spawner (Join-Path $tests 'fakes\spawner.cs')
    if ($problem) { Add-Result 'process-tree' 0 1 '' 0 "spawner build failed: $problem" } else { Invoke-Feature 'process-tree' 'HK' @($spawner) }
    Invoke-Feature 'kill-tree' 'H13' @()
    if (Get-Command node -ErrorAction SilentlyContinue) {
        Invoke-Feature 'shared-pieces' 'H5' @((Join-Path $out 'shared-pieces\run'), (Join-Path $snapshot 'statusline.js'))
    } else { Add-Result 'shared-pieces' 0 0 'node not found' 0 'the status line checks need Node.js' }
}

function Find-Soffice {
    foreach ($p in @("$env:ProgramFiles\LibreOffice\program\soffice.exe", "${env:ProgramFiles(x86)}\LibreOffice\program\soffice.exe")) { if ($p -and (Test-Path $p)) { return $p } }
    $cmd = Get-Command soffice.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Run-Preview {
    $py = Get-Command python -ErrorAction SilentlyContinue
    if (-not $py) { Add-Result 'file-preview' 0 0 'Python not found' 0 'the preview samples are generated with Python'; return }
    if (-not (Find-Soffice)) { Add-Result 'file-preview' 0 0 'LibreOffice not found' 0 'the docx, pptx and xlsx previews need LibreOffice'; return }
    $p2 = Join-Path $Scratch 'fixtures\p2'
    New-Item -ItemType Directory -Force $p2 | Out-Null
    $text = & $py.Source -I (Join-Path $tests 'fixtures\make-samples.py') $p2 2>&1
    if ($LASTEXITCODE -ne 0) { Add-Result 'file-preview' 0 0 'sample generation failed' 0 ("needs reportlab, python-pptx, openpyxl and Pillow: " + (($text | Select-Object -Last 2) -join ' ')); return }
    $exe = Join-Path $bin 'file-preview.exe'
    $problem = Invoke-Csc $exe 'H2' ($src + (Join-Path $tests 'suites\preview\file-preview.cs'))
    if ($problem) { Add-Result 'file-preview' 0 1 '' 0 "build failed: $problem"; return }
    Invoke-Suite 'file-preview' $exe @('all', (Join-Path $out 'file-preview\run'), $p2)
}

function Run-Png {
    $app = Build-App
    $server = Join-Path $bin 'fake-server.exe'
    $null = Invoke-Csc-Plain $server (Join-Path $tests 'fakes\fake-server.cs')
    $exe = Join-Path $bin 'widget-main.exe'
    $problem = Invoke-Csc $exe 'H4' ($src + $widgetFiles)
    if ($problem) { Add-Result 'png' 0 1 '' 0 "build failed: $problem"; return }
    Invoke-Suite 'png' $exe @('png', (Join-Path $out 'png'), $app, $server)
    $shot = Join-Path $out 'png\widget.png'
    if (Test-Path $shot) { $KeepScratch = $true; Write-Host "Screenshot: $shot (copy it to docs\widget.png by hand if it looks right)" }
}

try {
    switch ($Mode) {
        'quick' { Run-Quick }
        'chat' { Run-Chat }
        'widget' { Run-Widget }
        'preview' { Run-Preview }
        'png' { Run-Png }
        'all' { Run-Quick; Run-Chat; Run-Widget; Run-Preview }
    }
}
finally {
    $env:TEMP = $savedTemp; $env:TMP = $savedTmp
    foreach ($w in $writtenPorts) { [Environment]::SetEnvironmentVariable("CAWT_PORT_$w", $null, 'Process') }
}

Write-Host ""
Write-Host "widget.cs $widgetMd5   preview.cs $previewMd5   scratch $Scratch   ports $portText"
$results | Format-Table Suite, Pass, Fail, Skip, Seconds, Note -AutoSize | Out-String -Width 200 | Write-Host
$failed = @($results | Where-Object { $_.Fail -gt 0 }).Count
$skipped = @($results | Where-Object { $_.Skip -ne '' }).Count
if ($failed -gt 0) { Write-Host "FAILED: $failed suite(s). Scratch kept at $Scratch"; exit 1 }
if ($skipped -gt 0) { Write-Host "INCOMPLETE: $skipped suite(s) skipped, see the Skip column." }
else { Write-Host "PASSED" }
if (-not $KeepScratch -and $Mode -ne 'png') { Remove-Item $Scratch -Recurse -Force -ErrorAction SilentlyContinue }
exit 0
