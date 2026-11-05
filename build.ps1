# Compiles the widget with the C# compiler included in Windows and installs it.
param(
    [string]$OutDir = "$env:LOCALAPPDATA\ClaudeAccountWidget"
)

$ErrorActionPreference = 'Stop'
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$src = $PSScriptRoot

New-Item -ItemType Directory -Force $OutDir | Out-Null

$exe = Join-Path $OutDir 'ClaudeAccountWidget.exe'
Get-Process ClaudeAccountWidget -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $exe } |
    Stop-Process
Start-Sleep -Milliseconds 500

& "$fw\csc.exe" /nologo /target:winexe "/out:$exe" `
    "/r:$fw\WPF\PresentationFramework.dll" `
    "/r:$fw\WPF\PresentationCore.dll" `
    "/r:$fw\WPF\WindowsBase.dll" `
    "/r:$fw\System.Xaml.dll" `
    "/r:$fw\System.Web.Extensions.dll" `
    (Join-Path $src 'widget.cs')
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

Copy-Item (Join-Path $src 'statusline.js') $OutDir -Force
Write-Host "Installed to $OutDir"
