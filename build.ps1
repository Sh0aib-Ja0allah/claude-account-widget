# Compiles the widget with the C# compiler included in Windows and installs it.
param(
    [string]$OutDir = "$env:LOCALAPPDATA\ClaudeAccountWidget"
)

$ErrorActionPreference = 'Stop'
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$src = $PSScriptRoot
$winmd = "$env:WINDIR\System32\WinMetadata"

New-Item -ItemType Directory -Force $OutDir | Out-Null
# A full path, so a relative -OutDir still finds a running copy to close.
$OutDir = (Resolve-Path $OutDir).Path

$exe = Join-Path $OutDir 'ClaudeAccountWidget.exe'
$running = @(Get-Process ClaudeAccountWidget -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $exe })
$running | Stop-Process
# Compiling while the old widget is still exiting fails, because its exe is still in use.
foreach ($old in $running) {
    if (-not $old.WaitForExit(10000)) {
        throw "The running widget (process $($old.Id)) didn't exit within 10 seconds, so $exe is still in use. End it in Task Manager, then run build.ps1 again."
    }
}

& "$fw\csc.exe" /nologo /target:winexe "/out:$exe" `
    "/win32icon:$(Join-Path $src 'claude-spark.ico')" `
    "/r:$fw\WPF\PresentationFramework.dll" `
    "/r:$fw\WPF\PresentationCore.dll" `
    "/r:$fw\WPF\WindowsBase.dll" `
    "/r:$fw\System.Xaml.dll" `
    "/r:$fw\System.Web.Extensions.dll" `
    "/r:$fw\System.Windows.Forms.dll" `
    "/r:$fw\System.Drawing.dll" `
    "/r:$fw\Microsoft.VisualBasic.dll" `
    "/r:$fw\WPF\ReachFramework.dll" `
    "/r:$fw\WPF\System.Printing.dll" `
    "/r:$fw\System.Runtime.dll" `
    "/r:$fw\System.Runtime.WindowsRuntime.dll" `
    "/r:$fw\System.Threading.Tasks.dll" `
    "/r:$fw\System.IO.dll" `
    "/r:$winmd\Windows.Foundation.winmd" `
    "/r:$winmd\Windows.Data.winmd" `
    "/r:$winmd\Windows.Storage.winmd" `
    "/r:$winmd\Windows.Graphics.winmd" `
    "/r:$winmd\Windows.Media.winmd" `
    "/r:$winmd\Windows.Globalization.winmd" `
    (Join-Path $src 'widget.cs') `
    (Join-Path $src 'preview.cs')
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

Copy-Item (Join-Path $src 'statusline.js') $OutDir -Force
Write-Host "Installed to $OutDir"
